using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 全量评估集（八类输入 × 100+ 用例），规则引擎（无 LLM）Top-1 ≥ 85% 验收。
/// 类别：别名 / 全拼 / 首字母 / 拼音前缀 / 中文口语 / 错别字 / 全角 / 拼音变体。
/// </summary>
public class FullEvalSetTests
{
    /// <summary>期望 Top-1（允许并列时在其中任一）。</summary>
    public static readonly IReadOnlyList<(string Input, string[] Expected)> EvalSet = new[]
    {
        // ---- 1. 别名（20）----
        ("l", new[]{"LINE"}), ("pl", new[]{"PLINE"}), ("c", new[]{"CIRCLE"}), ("a", new[]{"ARC"}),
        ("rec", new[]{"RECTANG"}), ("pol", new[]{"POLYGON"}), ("el", new[]{"ELLIPSE"}), ("spl", new[]{"SPLINE"}),
        ("xl", new[]{"XLINE"}), ("h", new[]{"HATCH"}), ("e", new[]{"ERASE"}), ("co", new[]{"COPY"}),
        ("mi", new[]{"MIRROR"}), ("o", new[]{"OFFSET"}), ("ar", new[]{"ARRAY"}), ("m", new[]{"MOVE"}),
        ("ro", new[]{"ROTATE"}), ("sc", new[]{"SCALE"}), ("tr", new[]{"TRIM"}), ("f", new[]{"FILLET"}),

        // ---- 2. 全拼（20）----
        ("zhixian", new[]{"LINE"}), ("duoduanxian", new[]{"PLINE"}), ("yuan", new[]{"CIRCLE"}),
        ("yuanhu", new[]{"ARC"}), ("juxing", new[]{"RECTANG"}), ("zhengduobianxing", new[]{"POLYGON"}),
        ("tuoyuan", new[]{"ELLIPSE"}), ("yangtiaoquxian", new[]{"SPLINE"}), ("gouchengxian", new[]{"XLINE"}),
        ("tuantianchong", new[]{"HATCH"}), ("shanchu", new[]{"ERASE"}), ("fuzhi", new[]{"COPY"}),
        ("jingxiang", new[]{"MIRROR"}), ("pianyi", new[]{"OFFSET"}), ("zhenlie", new[]{"ARRAY"}),
        ("yidong", new[]{"MOVE"}), ("xuanzhuan", new[]{"ROTATE"}), ("suofang", new[]{"SCALE"}),
        ("xiujian", new[]{"TRIM"}), ("yuanjiao", new[]{"FILLET"}),

        // ---- 3. 首字母（20）----
        ("zx", new[]{"LINE"}), ("ddx", new[]{"PLINE"}), ("y", new[]{"CIRCLE"}), ("yh", new[]{"ARC"}),
        ("jx", new[]{"RECTANG"}), ("ytqx", new[]{"SPLINE"}), ("gcx", new[]{"XLINE"}), ("tatc", new[]{"HATCH"}),
        ("sc", new[]{"ERASE"}), ("fz", new[]{"COPY"}), ("py", new[]{"OFFSET"}), ("zl", new[]{"ARRAY"}),
        ("yd", new[]{"MOVE"}), ("xz", new[]{"ROTATE"}), ("sf", new[]{"SCALE"}), ("xj", new[]{"TRIM"}),
        ("yj", new[]{"FILLET"}), ("dj", new[]{"CHAMFER"}), ("bg", new[]{"TABLE"}), ("dy", new[]{"PLOT"}),

        // ---- 4. 拼音前缀（10）----
        ("zhix", new[]{"LINE"}), ("yua", new[]{"CIRCLE"}), ("jux", new[]{"RECTANG"}),
        ("xuanz", new[]{"ROTATE"}), ("pia", new[]{"OFFSET"}), ("fuz", new[]{"COPY"}),
        ("shan", new[]{"ERASE"}), ("tuan", new[]{"HATCH"}), ("biaozhuj", new[]{"DIMANGULAR"}), ("day", new[]{"PLOT"}),

        // ---- 5. 中文口语（15）----
        ("画个圆", new[]{"CIRCLE"}), ("直线", new[]{"LINE"}), ("填充", new[]{"HATCH"}),
        ("倒个角", new[]{"CHAMFER","FILLET"}), ("复制", new[]{"COPY"}), ("移动", new[]{"MOVE"}),
        ("旋转", new[]{"ROTATE"}), ("修剪", new[]{"TRIM"}), ("图层", new[]{"LAYER"}),
        ("标尺寸", new[]{"DIMLINEAR"}), ("量距离", new[]{"DIST"}), ("出图", new[]{"PLOT"}),
        ("删除", new[]{"ERASE"}), ("阵列", new[]{"ARRAY"}), ("镜像", new[]{"MIRROR"}),

        // ---- 6. 错别字/手滑（10）----
        ("linee", new[]{"LINE"}), ("circls", new[]{"CIRCLE"}), ("zhixianz", new[]{"LINE"}),
        ("yuanh", new[]{"ARC"}), ("coyp", new[]{"COPY"}), ("miror", new[]{"MIRROR"}),
        ("offst", new[]{"OFFSET"}), ("trmi", new[]{"TRIM"}), ("scael", new[]{"SCALE"}), ("rotat", new[]{"ROTATE"}),

        // ---- 7. 全角（10）----
        ("ＬＩＮＥ", new[]{"LINE"}), ("ｃｉｒｃｌｅ", new[]{"CIRCLE"}), ("ＴＲＩＭ", new[]{"TRIM"}),
        ("ｃｏｐｙ", new[]{"COPY"}), ("ＭＯＶＥ", new[]{"MOVE"}), ("ｙｕａｎ", new[]{"CIRCLE"}),
        ("ｚｈｉｘｉａｎ", new[]{"LINE"}), ("Ｌ", new[]{"LINE"}), ("ｚｘ", new[]{"LINE"}), ("ＲＥＣ", new[]{"RECTANG"}),

        // ---- 8. 拼音变体（方言/快打）（10）----
        ("zixian", new[]{"LINE"}), ("sanchu", new[]{"ERASE"}), ("cupy", new[]{"COPY"}),
        ("zuantianchong", new[]{"HATCH"}), ("yidon", new[]{"MOVE"}), ("xuanzuan", new[]{"ROTATE"}),
        ("fuhi", new[]{"COPY"}), ("scael", new[]{"SCALE"}), ("yuanjao", new[]{"FILLET"}), ("piany", new[]{"OFFSET"}),
    };

    [Fact]
    public void 评估集规模_不少于100条()
        => Assert.True(EvalSet.Count >= 100, $"当前 {EvalSet.Count} 条");

    [Fact]
    public void 规则引擎Top1准确率_不低于85百分比()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream);
        var engine = new MatchEngine();
        var commands = repo.GetAll();

        var passed = 0;
        var failures = new List<string>();

        foreach (var (input, expected) in EvalSet)
        {
            var items = engine.Rank(input, commands);
            var top1 = items.Count > 0 ? items[0].Command.Id : "(无结果)";
            if (expected.Contains(top1))
            {
                passed++;
            }
            else
            {
                failures.Add($"[{input}] 期望 {string.Join("/", expected)} 实得 {top1}" +
                    (items.Count > 1 ? $"（次选 {items[1].Command.Id}）" : string.Empty));
            }
        }

        var accuracy = passed / (double)EvalSet.Count;
        Assert.True(accuracy >= 0.85,
            $"Top-1 准确率 {accuracy:P1}（{passed}/{EvalSet.Count}）低于 85%。\n失败用例:\n{string.Join("\n", failures.Take(20))}");
    }

    [Fact]
    public void Top3命中率达到95百分比()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream);
        var engine = new MatchEngine();
        var commands = repo.GetAll();

        var passed = 0;
        foreach (var (input, expected) in EvalSet)
        {
            var items = engine.Rank(input, commands, topN: 3);
            if (items.Any(i => expected.Contains(i.Command.Id)))
            {
                passed++;
            }
        }

        var accuracy = passed / (double)EvalSet.Count;
        Assert.True(accuracy >= 0.95, $"Top-3 命中率 {accuracy:P1}（{passed}/{EvalSet.Count}）低于 95%");
    }

    [Fact]
    public void 性能_全评估集平均不超5毫秒()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream);
        var engine = new MatchEngine();
        var commands = repo.GetAll();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (input, _) in EvalSet)
        {
            engine.Rank(input, commands);
        }

        sw.Stop();
        var avg = sw.ElapsedMilliseconds / (double)EvalSet.Count;
        Assert.True(avg <= 5, $"平均 {avg:F2}ms 超过 5ms");
    }
}
