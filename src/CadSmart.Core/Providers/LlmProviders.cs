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
    public int TimeoutMs { get; set; } = 60000;

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
        // 序列化载荷全为匿名类型/字典，属性名手写已是 snake_case（如 response_format、chat_template_kwargs），
        // 无需命名策略（SnakeCaseLower 为 net8 API，net6 无此成员）。
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
        CancellationToken cancellationToken = default,
        IReadOnlyList<CadCommand>? fullCatalog = null)
    {
        if (!IsAvailable || candidateScope.Count == 0)
        {
            return null;
        }

        // 思考型模型（Qwen3/3.5 等）需要禁用推理模式，否则输出全是 reasoning、content 为空，
        // 且 4096 token 都冲不出思考区。chat_template_kwargs.enable_thinking=false 是 llama.cpp/
        // Ollama 兼容层的标准开关；云端 OpenAI 兼容 API 会忽略未知字段，不影响云端路径。
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Options.Model,
            ["temperature"] = Options.Temperature,
            ["max_tokens"] = 512,
            ["messages"] = new object[]
            {
                new { role = "system", content = PromptTemplates.BuildSystemPrompt(candidateScope, catalog: fullCatalog) },
                new { role = "user", content = PromptTemplates.BuildUserPrompt(userInput) },
            },
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };

        // response_format=json_object 部分本地运行时不支持（llama.cpp 老版本会报错），
        // 提示词已含严格 JSON 约束，云端可加、本地探测式跳过——统一不加，靠解析容错兜底。

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
            Console.Error.WriteLine($"[CadSmart.LLM] scope={candidateScope.Count} contentLen={content?.Length ?? 0} content={(content ?? "(null)").Truncate(200)}");
            return PromptTemplates.ParseRecommendations(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 外部真取消 → 上抛由编排层处理
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine($"[CadSmart.LLM] timeout {Options.TimeoutMs}ms: {ex.GetType().Name}");
            return null; // 自身超时 → 降级：规则兜底
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"[CadSmart.LLM] HTTP error: {ex.Message} (status={ex.StatusCode})");
            return null; // 网络/协议错误 → 规则兜底
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CadSmart.LLM] {ex.GetType().Name}: {ex.Message}");
            return null; // 其他错误 → 规则兜底
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
/// 探测目标 = 本机端口 + 可选远程主机（局域网推理服务器场景，如 192.168.1.111）。
/// </summary>
public sealed class LocalLlmProvider : OpenAiCompatibleProviderBase
{
    private static readonly int[] DefaultPorts = [11434, 1234, 8080];

    private readonly int[] _ports;
    private readonly string[] _hosts;
    private DateTime _lastProbeUtc = DateTime.MinValue;
    private int _openPort;
    private string _openHost = "127.0.0.1";
    private readonly object _lock = new();

    /// <summary>
    /// 构造本地/局域网供应商。
    /// </summary>
    /// <param name="http">HTTP 客户端</param>
    /// <param name="options">LLM 配置（探测成功后 Endpoint 会被自动写入）</param>
    /// <param name="ports">探测端口列表（默认 11434/1234/8080）</param>
    /// <param name="hosts">探测主机列表（默认仅 127.0.0.1；可加局域网 IP，如 192.168.1.111）</param>
    public LocalLlmProvider(HttpClient http, LlmOptions options, int[]? ports = null, string[]? hosts = null)
        : base(http, options)
    {
        _ports = ports is { Length: > 0 } ? ports : DefaultPorts;
        _hosts = hosts is { Length: > 0 } ? hosts : ["127.0.0.1"];
    }

    public override string Name => "local";

    /// <summary>注释：LocModel 的 API 端口以其实际文档为准，可通过 ports 参数配置。</summary>
    public static IReadOnlyList<int> KnownPorts => DefaultPorts;

    /// <summary>探测成功的主机（127.0.0.1 或局域网 IP）。</summary>
    public string OpenHost => _openHost;

    /// <summary>探测成功的端口。</summary>
    public int OpenPort => _openPort;

    public override bool IsAvailable => _openPort > 0 && RecentlyProbed();

    /// <summary>是否需要重新探测（缓存过期即 true；已过期失败结果 60s 后重试）。</summary>
    public bool NeedsProbe => !RecentlyProbed();

    /// <summary>
    /// 探测主机 × 端口组合（800ms/目标 超时）。结果缓存 60 秒。
    /// 主机列表含局域网 IP 时即为"局域网推理服务器"模式。
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsAvailable)
            {
                return true;
            }
        }

        foreach (var host in _hosts)
        {
            foreach (var port in _ports)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(800);
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}/v1/models");
                    using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        lock (_lock)
                        {
                            _openPort = port;
                            _openHost = host;
                            _lastProbeUtc = DateTime.UtcNow;
                            Options.Endpoint = $"http://{host}:{port}/v1/chat/completions";
                        }

                        return true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 探测超时 → 尝试下一个组合
                }
                catch (Exception)
                {
                    // 连接失败 → 尝试下一个组合
                }
            }
        }

        lock (_lock)
        {
            _openPort = 0;
            _openHost = string.Empty;
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
        string userInput, IReadOnlyList<CadCommand> candidateScope, CancellationToken cancellationToken = default,
        IReadOnlyList<CadCommand>? fullCatalog = null)
        => Task.FromResult<IReadOnlyList<LlmRecommendation>?>(null);
}

/// <summary>调试辅助：截断字符串。</summary>
internal static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
