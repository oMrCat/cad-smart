using System.Diagnostics;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;

// 控制台中文输出
Console.OutputEncoding = System.Text.Encoding.UTF8;

var usageFile = Path.Combine(AppContext.BaseDirectory, "usage.json");
var behavior = new JsonFileBehaviorStore(usageFile);
var repo = JsonCommandRepository.LoadFromStream(OpenEmbedded(), behavior);

MatchEngine engine = new();
ILlmProvider? llm = new NullLlmProvider();
var service = new SuggestionService(repo, behavior, engine, llm);

const string Banner = """
    ==============================================
     CAD 智能命令提示 · 命令行模拟器 (阶段0/1)
    ==============================================
     直接输入命令片段（中文/拼音/缩写/错别字均可）
     指令：
       :llm mock   启用 Mock 语义供应商
       :llm off    关闭 LLM（纯规则）
       :stats      查看使用统计
       :bench      跑自动化性能测试
       :help       显示帮助
       :quit       退出
    ==============================================
    """;

Console.WriteLine(Banner);

// 支持 --bench 直接跑自动化测试后退出（供无人值守验证）
if (args.Contains("--bench"))
{
    return RunBench(service, repo, engine);
}

while (true)
{
    Console.Write("\n输入> ");
    var input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    var trimmed = input.Trim();
    switch (trimmed.ToLowerInvariant())
    {
        case "" or ":help":
            Console.WriteLine(Banner);
            continue;
        case ":quit" or ":q":
            repo.Save();
            Console.WriteLine("使用统计已保存，再见。");
            return 0;
        case ":llm mock":
            llm = new MockLlmProvider(BuildMockMap(), delayMs: 120);
            service = new SuggestionService(repo, behavior, engine, llm);
            Console.WriteLine("已启用 Mock LLM（延迟 120ms）。");
            continue;
        case ":llm off":
            llm = new NullLlmProvider();
            service = new SuggestionService(repo, behavior, engine, llm);
            Console.WriteLine("已关闭 LLM，纯规则模式。");
            continue;
        case ":stats":
            PrintStats(repo);
            continue;
        case ":bench":
            return RunBench(service, repo, engine);
    }

    if (trimmed.StartsWith(':'))
    {
        Console.WriteLine("未知指令。:help 查看帮助。");
        continue;
    }

    var sw = Stopwatch.StartNew();
    var result = await service.SuggestAsync(trimmed);
    sw.Stop();

    Console.WriteLine($"\n候选（规则 {result.RuleLatencyMs}ms / 总计 {sw.ElapsedMilliseconds}ms，LLM: {DescribeLlm(result)}）：");
    if (result.Items.Count == 0)
    {
        Console.WriteLine("  （无匹配。试试拼音、缩写或中文口语）");
        continue;
    }

    for (var i = 0; i < result.Items.Count; i++)
    {
        var item = result.Items[i];
        var llmMark = item.Source == "rule" ? "" : $" [{item.Source} {item.LlmConfidence:F2}]";
        Console.WriteLine(
            $"  {i + 1}. {item.Command.Id,-12} {item.Command.ChineseName,-8} " +
            $"[{item.Command.Category}] 得分 {item.Score,5:F1} 经由 {item.MatchedVia}{llmMark}");
        if (!string.IsNullOrEmpty(item.Command.ParamsHint))
        {
            Console.WriteLine($"     操作: {item.Command.ParamsHint}");
        }
    }

    Console.Write("执行编号（回车跳过）> ");
    var pick = Console.ReadLine();
    if (int.TryParse(pick, out var idx) && idx >= 1 && idx <= result.Items.Count)
    {
        var chosen = result.Items[idx - 1];
        service.Accept(chosen.Command.Id);
        repo.Save();
        Console.WriteLine($"→ 已执行 {chosen.Command.Id}（使用次数 +1）");

        // 上下文感知：执行后推荐下一个命令
        var next = service.SuggestNext();
        if (next.Items.Count > 0)
        {
            Console.WriteLine($"  下一步建议：{string.Join(" / ", next.Items.Take(4).Select(i => $"{i.Command.Id}({i.Command.ChineseName})"))}");
        }
    }
}

return 0;

static string DescribeLlm(SuggestionResult r)
    => r.LlmUsed ? "已融合" : r.LlmDegraded ? "超时降级" : "未启用";

static void PrintStats(JsonCommandRepository repo)
{
    Console.WriteLine("\n使用统计 Top 10：");
    var top = repo.GetAll().Where(c => c.UsageCount > 0)
        .OrderByDescending(c => c.UsageCount).Take(10).ToList();
    if (top.Count == 0)
    {
        Console.WriteLine("  （暂无使用记录）");
        return;
    }

    foreach (var c in top)
    {
        Console.WriteLine($"  {c.Id,-12} {c.ChineseName,-8} ×{c.UsageCount}");
    }
}

static Dictionary<string, IReadOnlyList<LlmRecommendation>> BuildMockMap() => new()
{
    ["画个圆"] = [new("CIRCLE", 0.95)],
    ["倒个角"] = [new("CHAMFER", 0.85), new("FILLET", 0.72)],
    ["把这两条线倒个圆角"] = [new("FILLET", 0.92)],
    ["标一下尺寸"] = [new("DIMLINEAR", 0.90), new("DIMALIGNED", 0.75)],
    ["出图"] = [new("PLOT", 0.93)],
};

static int RunBench(SuggestionService service, JsonCommandRepository repo, MatchEngine engine)
{
    Console.WriteLine("\n===== 自动化性能与正确性测试 =====");

    var cases = new[]
    {
        "l", "line", "LINE", "y", "yuan", "画个圆", "zx", "zhixian", "duoduoxian",
        "trz", "line", "剪断", "chamfer", "dj", "标尺寸", "出图", "dy", "fz", "复制",
        "xuanzhuan", "xz", "suofang", "tr", "修剪", "tucheng", "tc", "图层", "hk",
    };

    // 乱码用例：正确行为是"无候选且不崩溃"，不计入失败
    var garbageCases = new HashSet<string> { "hk", "zzzzxxxx999" };

    long worst = 0, sum = 0;
    var failures = new List<string>();

    foreach (var c in cases)
    {
        var sw = Stopwatch.StartNew();
        var r = service.SuggestAsync(c).GetAwaiter().GetResult();
        sw.Stop();
        sum += sw.ElapsedMilliseconds;
        worst = Math.Max(worst, sw.ElapsedMilliseconds);

        var status = sw.ElapsedMilliseconds <= 50 ? "OK " : "SLOW";
        var first = r.Items.Count > 0 ? $"{r.Items[0].Command.Id}({r.Items[0].Score:F0})" : "无结果";
        Console.WriteLine($"  [{status}] {c,-12} → top1: {first,-16} 规则 {r.RuleLatencyMs}ms 总 {sw.ElapsedMilliseconds}ms");

        if (r.Items.Count == 0 && !string.IsNullOrWhiteSpace(c) && !garbageCases.Contains(c))
        {
            failures.Add(c);
        }
    }

    // 全库模糊扫描性能（200 次平均）
    var allInputs = repo.GetAll().SelectMany(c =>
        new[] { c.Id[..Math.Min(2, c.Id.Length)], c.Pinyin.Length > 3 ? c.Pinyin[..4] : c.Pinyin, c.ChineseName })
        .Where(s => !string.IsNullOrEmpty(s)).ToList();

    var sw2 = Stopwatch.StartNew();
    foreach (var input in allInputs)
    {
        engine.Rank(input, repo.GetAll());
    }

    sw2.Stop();
    var avgScan = allInputs.Count == 0 ? 0 : sw2.ElapsedMilliseconds / (double)allInputs.Count;

    Console.WriteLine($"\n  用例数 {cases.Length}  平均 {sum / (double)cases.Length:F1}ms  最慢 {worst}ms");
    Console.WriteLine($"  全库扫描 {allInputs.Count} 次平均 {avgScan:F2}ms/次（阈值 50ms）");
    Console.WriteLine(failures.Count == 0
        ? "  结果：全部用例有候选，PASS ✔"
        : $"  结果：以下用例无候选 → {string.Join(", ", failures)}");

    return worst <= 50 && failures.Count == 0 ? 0 : 1;
}

static Stream OpenEmbedded()
{
    var asm = typeof(JsonCommandRepository).Assembly;
    var name = asm.GetManifestResourceNames()
        .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
    return asm.GetManifestResourceStream(name)!;
}
