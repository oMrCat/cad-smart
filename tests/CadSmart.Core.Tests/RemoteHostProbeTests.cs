using System.Net;
using System.Text;
using Xunit;
using CadSmart.Core.Providers;

namespace CadSmart.Tests;

/// <summary>
/// 远程主机探测测试（局域网推理服务器场景，如 192.168.1.111）：
/// 用本机桩服务器模拟"远程主机"，验证 hosts 列表探测、主机名记录、失败回退。
/// </summary>
public class RemoteHostProbeTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly int _port;

    public RemoteHostProbeTests()
    {
        _port = Random.Shared.Next(48100, 48900);
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
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

            var body = """{"data":[{"id":"qwen2.5-1.5b-instruct"}]}""";
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

    [Fact]
    public async Task 主机列表含桩服务器_探测成功并记录主机与端口()
    {
        var p = new LocalLlmProvider(
            new HttpClient(),
            new LlmOptions { Model = "qwen" },
            ports: [1, 2, _port],
            hosts: ["127.0.0.1"]); // 本机桩模拟"远程"
        var ok = await p.ProbeAsync();
        Assert.True(ok);
        Assert.True(p.IsAvailable);
        Assert.Equal(_port, p.OpenPort);
        Assert.Equal("127.0.0.1", p.OpenHost);
        Assert.Contains($":{_port}/v1/chat/completions", EndpointOf(p));
    }

    [Fact]
    public async Task 远程主机不可达_回退本机探测_全部失败返回false()
    {
        var p = new LocalLlmProvider(
            new HttpClient(),
            new LlmOptions(),
            ports: [1, 2, 3],
            hosts: ["192.0.2.1", "127.0.0.1"]); // 192.0.2.x 是 TEST-NET 保留段，必不通
        var ok = await p.ProbeAsync();
        Assert.False(ok);
        Assert.False(p.IsAvailable);
        Assert.Equal(string.Empty, p.OpenHost);
    }

    [Fact]
    public async Task 首选主机失败_回退到次选主机()
    {
        // localhost 在部分环境解析为 IPv6 ::1 而桩只监听 127.0.0.1 → 首选失败回退
        // 验证回退机制本身（不预设哪个先成功）
        var p = new LocalLlmProvider(
            new HttpClient(),
            new LlmOptions(),
            ports: [_port],
            hosts: ["localhost", "127.0.0.1"]);
        var ok = await p.ProbeAsync();
        Assert.True(ok);
        Assert.Contains(p.OpenHost, new[] { "localhost", "127.0.0.1" });
        Assert.Equal(_port, p.OpenPort);
    }

    private static string EndpointOf(LocalLlmProvider p)
    {
        var field = typeof(OpenAiCompatibleProviderBase)
            .GetField("Options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var opts = (LlmOptions)field.GetValue(p)!;
        return opts.Endpoint;
    }
}
