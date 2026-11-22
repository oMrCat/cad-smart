using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 端到端全链路测试：知识库 → 建议 → 执行 → 行为持久化 → 重载生效。
/// 对应真实使用闭环（无 CAD 环境下的系统级验证）。
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _usageFile;

    public EndToEndTests()
    {
        _usageFile = Path.Combine(Path.GetTempPath(), $"cadsmart-e2e-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_usageFile))
        {
            File.Delete(_usageFile);
        }
    }

    private static JsonCommandRepository LoadRepo(IBehaviorStore behavior)
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonCommandRepository.LoadFromStream(stream, behavior);
    }

    [Fact]
    public async Task 全链路_建议到执行到持久化()
    {
        // ---- 会话 1：建议 → 用户选择执行 ----
        var behavior1 = new JsonFileBehaviorStore(_usageFile);
        var repo1 = LoadRepo(behavior1);
        var service1 = new SuggestionService(repo1, behavior1, new MatchEngine(), new NullLlmProvider());

        var r1 = await service1.SuggestAsync("y");
        Assert.Equal("CIRCLE", r1.Items[0].Command.Id);
        var arcScoreBefore = r1.Items.FirstOrDefault(i => i.Command.Id == "ARC")?.Score ?? 0;

        // 用户实际常用 ARC（模拟 30 次选择）
        for (var i = 0; i < 30; i++)
        {
            service1.Accept("ARC");
        }

        repo1.Save(); // 落盘

        Assert.True(File.Exists(_usageFile), "行为文件未生成");

        // ---- 会话 2：全新进程语境重载，个性化应生效 ----
        var behavior2 = new JsonFileBehaviorStore(_usageFile);
        Assert.Equal(30, behavior2.GetUsageCounts()["ARC"]);

        var repo2 = LoadRepo(behavior2);
        var service2 = new SuggestionService(repo2, behavior2, new MatchEngine(), new NullLlmProvider());
        var r2 = await service2.SuggestAsync("y");

        var arcAfter = r2.Items.FirstOrDefault(i => i.Command.Id == "ARC");
        Assert.NotNull(arcAfter);
        Assert.True(arcAfter!.Score > arcScoreBefore,
            $"个性化加权未生效: 前 {arcScoreBefore} 后 {arcAfter.Score}");
        // 注：UsageCount 存于行为文件（commands.json 为静态知识库），重载后行为计数由 behavior2 提供
        Assert.Equal(30, behavior2.GetUsageCounts()["ARC"]);
    }

    [Fact]
    public async Task 全链路_LLM不可用时系统仍完整可用()
    {
        // 模拟完全离线 + 无本地运行时：NullLlmProvider
        var behavior = new JsonFileBehaviorStore(_usageFile);
        var repo = LoadRepo(behavior);
        var service = new SuggestionService(repo, behavior, new MatchEngine(), new NullLlmProvider());

        foreach (var input in new[] { "l", "画个圆", "zhixian", "剪断", "标尺寸" })
        {
            var r = await service.SuggestAsync(input);
            Assert.False(r.LlmUsed);
            Assert.False(r.LlmDegraded);
            Assert.NotEmpty(r.Items);
            Assert.True(r.RuleLatencyMs <= 50);
        }
    }

    [Fact]
    public async Task 全链路_MockLLM与规则双通道结果融合去重()
    {
        var behavior = new JsonFileBehaviorStore(_usageFile);
        var repo = LoadRepo(behavior);

        // Mock 对同一输入同时返回规则也命中的命令 + 规则没给的命令
        var mock = new MockLlmProvider(new Dictionary<string, IReadOnlyList<LlmRecommendation>>
        {
            ["倒个角"] = [new LlmRecommendation("CHAMFER", 0.9), new LlmRecommendation("FILLET", 0.8)],
        }, delayMs: 30);
        var service = new SuggestionService(repo, behavior, new MatchEngine(), mock) { LlmWaitMs = 500 };

        var r = await service.SuggestAsync("倒个角");
        Assert.True(r.LlmUsed);

        var ids = r.Items.Select(i => i.Command.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // 无重复
        Assert.Contains("CHAMFER", ids);
        Assert.Contains("FILLET", ids);
        Assert.True(r.Items[0].LlmConfidence > 0); // top1 来自 LLM 融合
    }
}
