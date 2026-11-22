namespace CadSmart.Core.Providers;

using System.Text.Json;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Prompting;

/// <summary>
/// LLM 供应商公共配置。
/// </summary>
public sealed class LlmOptions
{
    /// <summary>OpenAI 兼容 chat/completions 完整地址。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>模型名，如 qwen2.5-1.5b-instruct。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>API Key（本地运行时可为空）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Provider 级超时（毫秒）。编排层另有 300ms 融合窗口。</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>温度（低值保证输出稳定）。</summary>
    public double Temperature { get; set; } = 0.1;
}

/// <summary>
/// OpenAI 兼容 chat/completions 调用的公共实现（云端与本地共用）。
/// </summary>
public abstract class OpenAiCompatibleProviderBase : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    protected readonly HttpClient Http;
    protected readonly LlmOptions Options;

    protected OpenAiCompatibleProviderBase(HttpClient http, LlmOptions options)
    {
        Http = http;
        Options = options;
    }

    public abstract string Name { get; }

    public abstract bool IsAvailable { get; }

    public async Task<IReadOnlyList<LlmRecommendation>?> RecommendAsync(
        string userInput,
        IReadOnlyList<CadCommand> candidateScope,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || candidateScope.Count == 0)
        {
            return null;
        }

        var payload = new
        {
            model = Options.Model,
            temperature = Options.Temperature,
            messages = new object[]
            {
                new { role = "system", content = PromptTemplates.BuildSystemPrompt(candidateScope) },
                new { role = "user", content = PromptTemplates.BuildUserPrompt(userInput) },
            },
            response_format = new { type = "json_object" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Options.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(Options.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Options.ApiKey);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Options.TimeoutMs);

        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var content = ExtractMessageContent(body);
            return PromptTemplates.ParseRecommendations(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 外部真取消 → 上抛由编排层处理
        }
        catch (OperationCanceledException)
        {
            return null; // 自身超时 → 降级：规则兜底
        }
        catch (Exception)
        {
            return null; // 网络/协议错误 → 规则兜底
        }
    }

    private static string? ExtractMessageContent(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

/// <summary>
/// 云端供应商（OpenAI 兼容 API：DeepSeek / 智谱 GLM / OpenAI 等）。
/// </summary>
public sealed class CloudLlmProvider : OpenAiCompatibleProviderBase
{
    public CloudLlmProvider(HttpClient http, LlmOptions options)
        : base(http, options)
    {
    }

    public override string Name => "cloud";

    public override bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Options.Endpoint) &&
        !string.IsNullOrWhiteSpace(Options.Model);
}

/// <summary>
/// 本地供应商（LocModel / Ollama / LM Studio 等 OpenAI 兼容本地运行时）。
/// 启动时自动探测本地端口，发现哪个就用哪个。
/// </summary>
public sealed class LocalLlmProvider : OpenAiCompatibleProviderBase
{
    private static readonly int[] DefaultPorts = [11434, 1234, 8080];

    private readonly int[] _ports;
    private DateTime _lastProbeUtc = DateTime.MinValue;
    private int _openPort;
    private readonly object _lock = new();

    public LocalLlmProvider(HttpClient http, LlmOptions options, int[]? ports = null)
        : base(http, options)
    {
        _ports = ports is { Length: > 0 } ? ports : DefaultPorts;
    }

    public override string Name => "local";

    /// <summary>注释：LocModel 的 API 端口以其实际文档为准，可通过 ports 参数配置。</summary>
    public static IReadOnlyList<int> KnownPorts => DefaultPorts;

    public override bool IsAvailable => _openPort > 0 && RecentlyProbed();

    /// <summary>探测本地端口（Ollama 11434 / LM Studio 1234 / 自定义）。结果缓存 60 秒。</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsAvailable)
            {
                return true;
            }
        }

        foreach (var port in _ports)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(800);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/models");
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    lock (_lock)
                    {
                        _openPort = port;
                        _lastProbeUtc = DateTime.UtcNow;
                        Options.Endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
                    }

                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 端口探测超时 → 尝试下一个
            }
            catch (Exception)
            {
                // 连接失败 → 尝试下一个
            }
        }

        lock (_lock)
        {
            _openPort = 0;
            _lastProbeUtc = DateTime.UtcNow;
        }

        return false;
    }

    private bool RecentlyProbed() => DateTime.UtcNow - _lastProbeUtc < TimeSpan.FromSeconds(60);
}

/// <summary>
/// 空供应商：LLM 完全不可用（离线/未配置）时的稳定兜底。
/// </summary>
public sealed class NullLlmProvider : ILlmProvider
{
    public string Name => "null";

    public bool IsAvailable => false;

    public Task<IReadOnlyList<LlmRecommendation>?> RecommendAsync(
        string userInput, IReadOnlyList<CadCommand> candidateScope, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LlmRecommendation>?>(null);
}
