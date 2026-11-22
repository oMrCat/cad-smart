using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;
using CadSmart.Server;

namespace CadSmart.Tests;

/// <summary>
/// 阶段 2 重构 #1 验证：引擎进程化后，远程客户端与进程内引擎行为一致。
/// </summary>
public class ServerEngineTests : IDisposable
{
    private readonly MiniHttpServer _server;
    private readonly RemoteSuggestionEngine _remote;
    private readonly JsonFileBehaviorStore _behavior;
    private readonly string _usageFile;

    public ServerEngineTests()
    {
        _usageFile = Path.Combine(Path.GetTempPath(), $"cadsmart-srv-{Guid.NewGuid():N}.json");
        _behavior = new JsonFileBehaviorStore(_usageFile);

        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream, _behavior);

        var engine = new InProcessSuggestionEngine(
            new SuggestionService(repo, _behavior, new MatchEngine(), new NullLlmProvider()));

        // 随机端口避免并行测试冲突
        var port = Random.Shared.Next(46300, 46900);
        _server = new MiniHttpServer(engine, port);
        _server.Start();
        _remote = new RemoteSuggestionEngine(port);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (File.Exists(_usageFile))
        {
            File.Delete(_usageFile);
        }
    }

    [Fact]
    public async Task 服务启动_健康检查可达()
        => Assert.True(await _remote.PingAsync());

    [Fact]
    public async Task 远程建议_与进程内结果一致()
    {
        var r = await _remote.SuggestAsync("画个圆");
        Assert.NotEmpty(r.Items);
        Assert.Equal("CIRCLE", r.Items[0].Command.Id);
    }

    [Fact]
    public async Task 远程建议_拼音与错别字同样有效()
    {
        var r1 = await _remote.SuggestAsync("zhixian");
        Assert.Equal("LINE", r1.Items[0].Command.Id);

        var r2 = await _remote.SuggestAsync("circls");
        Assert.Equal("CIRCLE", r2.Items[0].Command.Id);
    }

    [Fact]
    public async Task 远程Accept_行为计数落盘()
    {
        _remote.Accept("ARC");
        _remote.Accept("ARC");
        // Accept 是 async void（fire-and-forget），轮询等待落盘
        for (var i = 0; i < 20 && _behavior.GetUsageCounts().GetValueOrDefault("ARC") < 2; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, _behavior.GetUsageCounts().GetValueOrDefault("ARC"));
    }

    [Fact]
    public async Task 服务停止_远程客户端优雅降级()
    {
        await _server.StopAsync();
        var r = await _remote.SuggestAsync("画个圆");
        Assert.Empty(r.Items); // 空结果而非异常
        Assert.False(await _remote.PingAsync());
    }

    [Fact]
    public async Task 并发请求_全部成功()
    {
        var tasks = Enumerable.Range(0, 16).Select(_ => _remote.SuggestAsync("yuan")).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("CIRCLE", r.Items[0].Command.Id));
    }
}
