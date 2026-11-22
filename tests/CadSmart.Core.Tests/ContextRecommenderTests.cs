using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Core.Suggestion.Context;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 上下文感知推荐测试：转移表先验 + 用户行为叠加 + SuggestNext 全链路。
/// </summary>
public class ContextRecommenderTests
{
    private static JsonCommandRepository LoadRepo(IBehaviorStore? behavior = null)
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonCommandRepository.LoadFromStream(stream, behavior);
    }

    [Fact]
    public void 画完圆_首推偏移()
    {
        var r = new TransitionTableRecommender().RecommendNext(["CIRCLE"]);
        Assert.NotEmpty(r);
        Assert.Equal("OFFSET", r[0].CommandId);
        // 默认 PersonalWeight=0.35 稀释后：0.30 × 0.65 ≈ 0.195
        Assert.True(r[0].Confidence > 0.15, $"实际 {r[0].Confidence}");
    }

    [Fact]
    public void 线性标注后_首推连续标注()
    {
        var r = new TransitionTableRecommender().RecommendNext(["DIMLINEAR"]);
        Assert.Equal("DIMCONTINUE", r[0].CommandId);
    }

    [Fact]
    public void 半径标注后_首推直径标注()
    {
        var r = new TransitionTableRecommender().RecommendNext(["DIMRADIUS"]);
        Assert.Equal("DIMDIAMETER", r[0].CommandId);
    }

    [Fact]
    public void 无记录_返回空()
    {
        var r = new TransitionTableRecommender().RecommendNext([]);
        Assert.Empty(r);
    }

    [Fact]
    public void 未知命令_返回空()
    {
        var r = new TransitionTableRecommender().RecommendNext(["TOTALLY_UNKNOWN"]);
        Assert.Empty(r);
    }

    [Fact]
    public void 只看最近一条命令()
    {
        // 历史序列 [LINE, CIRCLE]：最近一条是 CIRCLE → 按 CIRCLE 的转移表
        var r = new TransitionTableRecommender().RecommendNext(["LINE", "CIRCLE"]);
        Assert.Equal("OFFSET", r[0].CommandId);
    }

    [Fact]
    public void 用户行为叠加_高频命令提升()
    {
        var behavior = new InMemoryBehaviorStore();
        for (var i = 0; i < 50; i++)
        {
            behavior.Increment("HATCH");
        }

        var recommender = new TransitionTableRecommender(behavior) { PersonalWeight = 0.8 };
        var r = recommender.RecommendNext(["RECTANG"]);
        // RECTANG 先验里 HATCH 0.20 排第二，行为加权后应排到 OFFSET 前面
        Assert.Equal("HATCH", r[0].CommandId);
    }

    [Fact]
    public void 推荐结果不超过maxCount()
    {
        var r = new TransitionTableRecommender().RecommendNext(["CIRCLE"], maxCount: 3);
        Assert.True(r.Count <= 3);
    }

    [Fact]
    public async Task 服务级SuggestNext_Accept后给出上下文推荐()
    {
        var behavior = new InMemoryBehaviorStore();
        var repo = LoadRepo(behavior);
        var service = new SuggestionService(repo, behavior, new MatchEngine(), new NullLlmProvider());

        // 模拟用户刚执行了画圆
        service.Accept("CIRCLE");

        var next = service.SuggestNext();
        Assert.NotEmpty(next.Items);
        Assert.Equal("OFFSET", next.Items[0].Command.Id);
        Assert.Equal("context", next.Items[0].Source);
        Assert.Equal("context-next", next.Items[0].MatchedVia);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 服务级_上下文推荐命令必须在知识库中()
    {
        var behavior = new InMemoryBehaviorStore();
        var repo = LoadRepo(behavior);
        var service = new SuggestionService(repo, behavior, new MatchEngine(), new NullLlmProvider());

        service.Accept("CIRCLE");
        var next = service.SuggestNext();
        // 所有推荐命令都能在知识库中找到（FindById 过滤保证）
        Assert.All(next.Items, i => Assert.NotNull(repo.FindById(i.Command.Id)));
        await Task.CompletedTask;
    }
}
