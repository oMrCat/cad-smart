using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// NL2Command 评估集（阶段 2 验收模式的骨架）：
/// 用 Mock 供应商模拟 LLM 返回，验证"输入 → 期望命令"的 Top-1 准确率 ≥ 85%。
/// 接入真实 LLM 后，此文件的数据表即评估集，跑分模式不变。
/// </summary>
public class EvalSetTests
{
    /// <summary>评估集：中文口语/长句 → 期望 Top-1 命令。</summary>
    public static readonly IReadOnlyList<(string Input, string Expected)> EvalSet = new[]
    {
        ("画个圆", "CIRCLE"),
        ("画一条线", "LINE"),
        ("把这两条线倒个圆角", "FILLET"),
        ("倒个斜角", "CHAMFER"),
        ("标一下尺寸", "DIMLINEAR"),
        ("标个半径", "DIMRADIUS"),
        ("标个直径", "DIMDIAMETER"),
        ("把图形放大一点", "SCALE"),
        ("把这个转个角度", "ROTATE"),
        ("复制一份到那边", "COPY"),
        ("镜像对称一下", "MIRROR"),
        ("剪掉多余的部分", "TRIM"),
        ("填充剖面线", "HATCH"),
        ("出图打印", "PLOT"),
        ("新建一个图层", "LAYER"),
        ("把这个做成块", "BLOCK"),
        ("插入那个图块", "INSERT"),
        ("测量两点距离", "DIST"),
        ("算一下面积", "AREA"),
        ("撤销上一步", "U"),
    };

    private static (JsonCommandRepository Repo, JsonFileBehaviorStore Behavior) NewEnv()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        var stream = asm.GetManifestResourceStream(name)!;
        var behavior = new InMemoryBehaviorStore();
        return (JsonCommandRepository.LoadFromStream(stream, behavior), ToFileBehavior(behavior));
    }

    private static JsonFileBehaviorStore ToFileBehavior(InMemoryBehaviorStore _)
        => new(Path.Combine(Path.GetTempPath(), $"cadsmart-eval-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task 评估集_Top1准确率不低于85百分比()
    {
        var (repo, behavior) = NewEnv();

        // Mock LLM：对评估集每条输入返回期望命令（模拟真实 LLM 的理想输出）
        var map = new Dictionary<string, IReadOnlyList<LlmRecommendation>>();
        foreach (var (input, expected) in EvalSet)
        {
            map[input] = new[] { new LlmRecommendation(expected, 0.9) };
        }

        var mock = new MockLlmProvider(map, delayMs: 20);
        var service = new SuggestionService(repo, behavior, new MatchEngine(), mock) { LlmWaitMs = 500 };

        var passed = new List<string>();
        var failed = new List<string>();

        foreach (var (input, expected) in EvalSet)
        {
            var r = await service.SuggestAsync(input);
            var top1 = r.Items.Count > 0 ? r.Items[0].Command.Id : "(无结果)";
            if (top1 == expected)
            {
                passed.Add(input);
            }
            else
            {
                failed.Add($"[{input}] 期望 {expected} 实得 {top1}");
            }
        }

        var accuracy = passed.Count / (double)EvalSet.Count;
        Assert.True(accuracy >= 0.85,
            $"Top-1 准确率 {accuracy:P0}（{passed.Count}/{EvalSet.Count}）低于 85%。\n失败用例:\n{string.Join("\n", failed)}");
    }

    [Fact]
    public async Task 评估集_无LLM时规则兜底_准确率不低于50百分比()
    {
        // 规则引擎对中文长句的兜底能力（LLM 全部不可用场景）
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream);
        var service = new SuggestionService(repo, new InMemoryBehaviorStore(), new MatchEngine(), new NullLlmProvider());

        var shortInputs = EvalSet.Where(e => e.Input.Length <= 4).ToList();
        var passed = 0;
        foreach (var (input, expected) in shortInputs)
        {
            var r = await service.SuggestAsync(input);
            if (r.Items.Count > 0 && r.Items[0].Command.Id == expected)
            {
                passed++;
            }
        }

        var accuracy = passed / (double)shortInputs.Count;
        Assert.True(accuracy >= 0.5, $"纯规则兜底准确率 {accuracy:P0} 低于 50%");
    }
}
