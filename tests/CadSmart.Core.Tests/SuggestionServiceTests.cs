using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;

namespace CadSmart.Tests;

public class SuggestionServiceTests
{
    private static JsonCommandRepository NewRepo()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonCommandRepository.LoadFromStream(stream);
    }

    private static JsonFileBehaviorStore NewTempBehavior()
        => new(Path.Combine(Path.GetTempPath(), $"cadsmart-tests-{Guid.NewGuid():N}.json"));

    // ---------- 纯规则模式 ----------

    [Fact]
    public async Task 纯规则模式_返回候选且无LLM标记()
    {
        var service = new SuggestionService(NewRepo(), NewTempBehavior(), llm: new NullLlmProvider());
        var r = await service.SuggestAsync("画个圆");
        Assert.False(r.LlmUsed);
        Assert.False(r.LlmDegraded);
        Assert.NotEmpty(r.Items);
        Assert.Equal("CIRCLE", r.Items[0].Command.Id);
        Assert.True(r.RuleLatencyMs <= 50, $"规则耗时 {r.RuleLatencyMs}ms");
    }

    // ---------- LLM 融合 ----------

    [Fact]
    public async Task MockLLM融合_中文口语命中并标记llm来源()
    {
        var mock = new MockLlmProvider(new Dictionary<string, IReadOnlyList<LlmRecommendation>>
        {
            ["画个圆"] = [new LlmRecommendation("CIRCLE", 0.95)],
        }, delayMs: 50);
        var service = new SuggestionService(NewRepo(), NewTempBehavior(), llm: mock) { LlmWaitMs = 400 };

        var r = await service.SuggestAsync("画个圆");
        Assert.True(r.LlmUsed);
        Assert.False(r.LlmDegraded);
        var top = r.Items[0];
        Assert.Equal("CIRCLE", top.Command.Id);
        Assert.True(top.Source is "llm" or "both");
        Assert.True(top.LlmConfidence > 0.9);
    }

    [Fact]
    public async Task LLM超时_降级为纯规则结果()
    {
        var slowMock = new MockLlmProvider(new Dictionary<string, IReadOnlyList<LlmRecommendation>>
        {
            ["画个圆"] = [new LlmRecommendation("CIRCLE", 0.95)],
        }, delayMs: 2000);
        var service = new SuggestionService(NewRepo(), NewTempBehavior(), llm: slowMock) { LlmWaitMs = 300 };

        var r = await service.SuggestAsync("画个圆");
        Assert.True(r.LlmDegraded);
        Assert.False(r.LlmUsed);
        Assert.NotEmpty(r.Items); // 规则结果仍在
        Assert.Equal("CIRCLE", r.Items[0].Command.Id); // 规则同样命中
    }

    [Fact]
    public async Task LLM编造命令_被丢弃()
    {
        var mock = new MockLlmProvider(new Dictionary<string, IReadOnlyList<LlmRecommendation>>
        {
            ["画个圆"] = [new LlmRecommendation("FAKE_COMMAND", 0.99)],
        }, delayMs: 20);
        var service = new SuggestionService(NewRepo(), NewTempBehavior(), llm: mock) { LlmWaitMs = 400 };

        var r = await service.SuggestAsync("画个圆");
        Assert.DoesNotContain(r.Items, i => i.Command.Id == "FAKE_COMMAND");
    }

    // ---------- 行为个性化 ----------

    [Fact]
    public async Task Accept后再次建议_个性化加权生效()
    {
        var repo = NewRepo();
        var behavior = NewTempBehavior();
        var service = new SuggestionService(repo, behavior);

        // "y" 命中圆（y 是 CIRCLE 别名），模拟用户多次使用
        for (var i = 0; i < 30; i++)
        {
            service.Accept("ARC");
        }

        var r = await service.SuggestAsync("yh");
        Assert.Equal("ARC", r.Items[0].Command.Id); // 50 次封顶加权后 ARC 超过 CIRCLE
    }

    // ---------- 本地探测 ----------

    [Fact]
    public async Task 本地探测_无服务端口_不可用()
    {
        var http = new HttpClient();
        // 使用极不可能开放的端口
        var provider = new LocalLlmProvider(http, new LlmOptions(), ports: [49091, 49092, 49093]);
        var ok = await provider.ProbeAsync();
        Assert.False(ok);
        Assert.False(provider.IsAvailable);
    }
}
