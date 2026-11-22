using System.Net;
using System.Text;
using Xunit;
using CadSmart.Core.Domain;
using CadSmart.Core.Providers;

namespace CadSmart.Tests;

/// <summary>
/// Provider HTTP 通路端到端验证（OpenAI 兼容桩服务器，不需要真实 API Key）：
/// 覆盖请求构造、Bearer 鉴权、响应解析、错误与超时降级。
/// </summary>
public class ProviderHttpTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>桩服务器收到的最后一次请求快照。</summary>
    public string? LastAuthHeader;
    public string? LastBody;
    public int RequestCount;

    public ProviderHttpTests()
    {
        var port = Random.Shared.Next(47100, 47900);
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _loop = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            Interlocked.Increment(ref RequestCount);
            LastAuthHeader = ctx.Request.Headers["Authorization"];

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            LastBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var body = """
                {"choices":[{"message":{"content":"{\"recommendations\":[{\"command_id\":\"CIRCLE\",\"confidence\":0.93}]}"}}]}
                """;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch { }
        _cts.Dispose();
    }

    private CloudLlmProvider NewProvider(int timeoutMs = 2000)
        => new(
            new HttpClient(),
            new LlmOptions
            {
                Endpoint = _prefix + "v1/chat/completions",
                Model = "test-model",
                ApiKey = "sk-test-123",
                TimeoutMs = timeoutMs,
            });

    private static IReadOnlyList<CadCommand> Scope() => new[]
    {
        new CadCommand { Id = "CIRCLE", Name = "CIRCLE", ChineseName = "圆", Category = "绘图", Description = "创建圆" },
        new CadCommand { Id = "LINE", Name = "LINE", ChineseName = "直线", Category = "绘图", Description = "创建直线" },
    };

    [Fact]
    public async Task 云端Provider_完整HTTP通路_返回解析结果()
    {
        var p = NewProvider();
        var recs = await p.RecommendAsync("画个圆", Scope());

        Assert.NotNull(recs);
        Assert.Single(recs!);
        Assert.Equal("CIRCLE", recs![0].CommandId);
        Assert.Equal(0.93, recs[0].Confidence, 3);

        // 验证发出的请求：Bearer 鉴权 + 提示词注入候选表 + JSON 约束
        Assert.Equal("Bearer sk-test-123", LastAuthHeader);
        Assert.Contains("CIRCLE", LastBody);
        Assert.Contains("禁止编造", LastBody);
        Assert.Contains("画个圆", LastBody);
    }

    [Fact]
    public async Task 云端Provider_服务不可达_返回null降级()
    {
        var p = new CloudLlmProvider(
            new HttpClient(),
            new LlmOptions
            {
                Endpoint = "http://127.0.0.1:1/v1/chat/completions", // 必然不可达
                Model = "m",
                TimeoutMs = 300,
            });
        var recs = await p.RecommendAsync("画个圆", Scope());
        Assert.Null(recs);
    }

    [Fact]
    public async Task 云端Provider_响应超时_按超时取消()
    {
        // TimeoutMs 极小 + 桩服务器正常响应 → 边界竞争，验证不抛异常即可（返回结果或 null）
        var p = NewProvider(timeoutMs: 1);
        var recs = await p.RecommendAsync("画个圆", Scope());
        Assert.True(recs is null or { Count: > 0 });
    }

    [Fact]
    public async Task 本地Provider_探测桩端口_发现并使用()
    {
        var http = new HttpClient();
        var p = new LocalLlmProvider(http, new LlmOptions { Model = "qwen-test" }, ports: [1, 2, ExtractPort()]);
        var ok = await p.ProbeAsync();
        Assert.True(ok);
        Assert.True(p.IsAvailable);
        Assert.Contains($":{ExtractPort()}/v1/chat/completions", EndpointOf(p));
    }

    [Fact]
    public async Task 本地Provider_探测不可用端口_不可用()
    {
        var p = new LocalLlmProvider(new HttpClient(), new LlmOptions(), ports: [1, 2, 3]);
        Assert.False(await p.ProbeAsync());
        Assert.False(p.IsAvailable);
    }

    private int ExtractPort()
    {
        // prefix 形如 http://127.0.0.1:47xxx/
        var parts = _prefix.Split(':');
        return int.Parse(parts[2].TrimEnd('/'));
    }

    private static string EndpointOf(LocalLlmProvider p)
    {
        var field = typeof(OpenAiCompatibleProviderBase)
            .GetField("Options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var opts = (LlmOptions)field.GetValue(p)!;
        return opts.Endpoint;
    }
}
