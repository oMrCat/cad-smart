namespace CadSmart.Data;

using System.Text;
using System.Text.Json;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Suggestion;

/// <summary>
/// 远程建议引擎客户端：通过 localhost JSON API 调用独立引擎服务进程（MiniHttpServer）。
/// 插件薄壳用此实现替代 InProcessSuggestionEngine，业务代码零改动。
/// 不可达时抛出/返回空由调用方策略决定——本实现选择"返回空结果 + 错误码标记"，保证 UI 不崩。
/// </summary>
public sealed class RemoteSuggestionEngine : ISuggestionEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;

    public RemoteSuggestionEngine(int port = 46200, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        BaseUrl = $"http://127.0.0.1:{port}/";
    }

    public string BaseUrl { get; }

    /// <summary>服务是否可达（供插件启动时选择进程内/远程模式）。</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            using var resp = await _http.GetAsync(BaseUrl + "health", cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<SuggestionResult> SuggestAsync(string? userInput, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { input = userInput ?? string.Empty }, JsonOpts);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(BaseUrl + "suggest", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SuggestionResult>(body, JsonOpts)
                ?? EmptyResult();
        }
        catch (Exception)
        {
            return EmptyResult(); // 服务不可达 → 空结果（UI 隐藏候选框，不崩）
        }
    }

    /// <summary>上下文感知推荐：根据最近执行序列推荐"下一个命令"（服务端 /next 端点）。</summary>
    public SuggestionResult SuggestNext()
        => SuggestNextAsync().GetAwaiter().GetResult();

    /// <summary>上下文感知推荐（异步版本）。</summary>
    public async Task<SuggestionResult> SuggestNextAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsync(BaseUrl + "next", content: null, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SuggestionResult>(body, JsonOpts) ?? EmptyResult();
        }
        catch (Exception)
        {
            return EmptyResult();
        }
    }

    public async void Accept(string commandId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { commandId }, JsonOpts);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(BaseUrl + "accept", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception)
        {
            // 统计上报失败不致命
        }
    }

    private static SuggestionResult EmptyResult() => new()
    {
        Items = Array.Empty<CommandCandidate>(),
        RuleLatencyMs = 0,
        TotalLatencyMs = 0,
    };
}
