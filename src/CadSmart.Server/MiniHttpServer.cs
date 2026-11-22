namespace CadSmart.Server;

using System.Net;
using System.Text;
using System.Text.Json;
using CadSmart.Core.Domain;
using CadSmart.Core.Setup;
using CadSmart.Core.Suggestion;

/// <summary>
/// 极简 HTTP 服务：把 ISuggestionEngine 暴露为 localhost JSON API。
/// 阶段 2 重构 #1 的服务端形态——独立进程托管，任何平台的插件薄壳通过 HTTP 调用。
///
/// API：
///   GET  /health                       → {"ok":true}
///   GET  /llm/status                   → 智能模式 + 安装引导清单（路线A）
///   POST /suggest  {"input":"画个圆"}   → SuggestionResult JSON
///   POST /accept   {"commandId":"..."} → {"ok":true}
///
/// 仅绑定 127.0.0.1（不对外网暴露），本机进程间通信无认证需求。
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ISuggestionEngine _engine;
    private readonly LlmAvailability? _availability;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MiniHttpServer(ISuggestionEngine engine, int port, LlmAvailability? availability = null)
    {
        _engine = engine;
        _availability = availability;
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 已停止
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // listener 已停止
            }

            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;
        var status = 200;

        try
        {
            object payload;
            switch (path, method)
            {
                case ("/health", "GET"):
                    payload = new { ok = true };
                    break;
                case ("/llm/status", "GET"):
                    payload = _availability
                        ?? (object)new { mode = "unknown", userMessage = "未检测" };
                    break;
                case ("/suggest", "POST"):
                    payload = await HandleSuggestAsync(ctx, ct).ConfigureAwait(false);
                    break;
                case ("/next", "POST"):
                    payload = _engine.SuggestNext();
                    break;
                case ("/accept", "POST"):
                    payload = await HandleAcceptAsync(ctx, ct).ConfigureAwait(false);
                    break;
                default:
                    payload = new { error = "not found" };
                    status = 404;
                    break;
            }

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                ctx.Response.StatusCode = 500;
            }
            catch
            {
                // 客户端已断开
            }
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task<SuggestionResult> HandleSuggestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ctx.Request, ct).ConfigureAwait(false);
        var input = ExtractString(body, "input");
        return await _engine.SuggestAsync(input, ct).ConfigureAwait(false);
    }

    private async Task<object> HandleAcceptAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ctx.Request, ct).ConfigureAwait(false);
        var id = ExtractString(body, "commandId");
        if (!string.IsNullOrWhiteSpace(id))
        {
            _engine.Accept(id);
        }

        return new { ok = true };
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ExtractString(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
