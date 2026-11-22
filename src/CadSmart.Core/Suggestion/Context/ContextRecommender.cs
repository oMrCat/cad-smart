namespace CadSmart.Core.Suggestion.Context;

using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;

/// <summary>
/// 上下文感知推荐（产品三大智能能力之三）：
/// 基于命令转移概率表（"刚执行完 X → 接下来常用什么"）推荐下一个命令。
/// 数据来源：行业通用工作流先验（内置）+ 用户行为统计（后续可叠加，本地个性化）。
/// </summary>
public interface IContextRecommender
{
    /// <summary>
    /// 根据最近执行的命令序列推荐下一个命令（返回命令 Id + 置信度）。
    /// </summary>
    IReadOnlyList<(string CommandId, double Confidence)> RecommendNext(
        IReadOnlyList<string> recentCommands,
        int maxCount = 5);
}

/// <summary>
/// 内置转移表实现：以 CAD 行业通用工作流为先验。
/// key = 上一个命令，value = [(下一命令, 概率)]。
/// </summary>
public sealed class TransitionTableRecommender : IContextRecommender
{
    /// <summary>内置工作流先验（通用设计流程，与行业弱相关部分收敛在标注/块/打印收尾）。</summary>
    private static readonly Dictionary<string, IReadOnlyList<(string, double)>> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // 画完基础图形 → 修饰/编辑/标注
        ["CIRCLE"] = [("OFFSET", 0.30), ("TRIM", 0.22), ("DIMRADIUS", 0.18), ("COPY", 0.14), ("HATCH", 0.10)],
        ["LINE"] = [("TRIM", 0.26), ("OFFSET", 0.22), ("EXTEND", 0.16), ("FILLET", 0.14), ("DIMLINEAR", 0.12)],
        ["PLINE"] = [("OFFSET", 0.28), ("PEDIT", 0.20), ("HATCH", 0.18), ("TRIM", 0.16), ("AREA", 0.10)],
        ["RECTANG"] = [("OFFSET", 0.30), ("HATCH", 0.20), ("DIMLINEAR", 0.18), ("EXPLODE", 0.12), ("COPY", 0.10)],
        ["ARC"] = [("TRIM", 0.25), ("FILLET", 0.20), ("DIMRADIUS", 0.18), ("MIRROR", 0.12)],
        ["POLYGON"] = [("OFFSET", 0.24), ("HATCH", 0.20), ("ROTATE", 0.16), ("DIMLINEAR", 0.12)],
        ["ELLIPSE"] = [("OFFSET", 0.22), ("HATCH", 0.20), ("DIMDIAMETER", 0.15)],
        ["SPLINE"] = [("OFFSET", 0.20), ("TRIM", 0.18), ("BLEND", 0.15), ("DIMSTYLE", 0.10)],

        // 修改完 → 继续编辑/检查/标注
        ["TRIM"] = [("ERASE", 0.22), ("EXTEND", 0.18), ("FILLET", 0.16), ("DIMLINEAR", 0.14), ("U", 0.12)],
        ["EXTEND"] = [("TRIM", 0.30), ("FILLET", 0.18), ("DIMLINEAR", 0.14)],
        ["FILLET"] = [("CHAMFER", 0.26), ("TRIM", 0.22), ("DIMRADIUS", 0.14), ("COPY", 0.12)],
        ["CHAMFER"] = [("FILLET", 0.26), ("TRIM", 0.18), ("DIMLINEAR", 0.14)],
        ["OFFSET"] = [("TRIM", 0.28), ("EXTEND", 0.18), ("HATCH", 0.16), ("DIMCONTINUE", 0.12)],
        ["COPY"] = [("MOVE", 0.24), ("ROTATE", 0.18), ("ARRAY", 0.16), ("DIMLINEAR", 0.12)],
        ["MIRROR"] = [("ERASE", 0.16), ("COPY", 0.16), ("TRIM", 0.14), ("DIMLINEAR", 0.12)],
        ["ARRAY"] = [("EXPLODE", 0.20), ("TRIM", 0.18), ("DIMLINEAR", 0.14)],
        ["MOVE"] = [("COPY", 0.24), ("ROTATE", 0.18), ("SCALE", 0.14), ("DIMLINEAR", 0.12)],
        ["ROTATE"] = [("MOVE", 0.24), ("COPY", 0.18), ("SCALE", 0.14), ("DIMANGULAR", 0.12)],
        ["SCALE"] = [("MOVE", 0.24), ("DIMLINEAR", 0.20), ("COPY", 0.14)],
        ["STRETCH"] = [("MOVE", 0.22), ("DIMLINEAR", 0.18), ("TRIM", 0.14)],
        ["EXPLODE"] = [("ERASE", 0.24), ("TRIM", 0.18), ("OFFSET", 0.14), ("HATCH", 0.10)],
        ["HATCH"] = [("DIMLINEAR", 0.22), ("MATCHPROP", 0.18), ("ERASE", 0.14), ("AREA", 0.12)],

        // 标注完 → 连续标注/收尾
        ["DIMLINEAR"] = [("DIMCONTINUE", 0.34), ("DIMBASELINE", 0.24), ("DIMALIGNED", 0.16), ("DIMSTYLE", 0.10)],
        ["DIMALIGNED"] = [("DIMCONTINUE", 0.28), ("DIMLINEAR", 0.22), ("DIMSTYLE", 0.12)],
        ["DIMRADIUS"] = [("DIMDIAMETER", 0.32), ("DIMANGULAR", 0.18), ("DIMCENTER", 0.14)],
        ["DIMDIAMETER"] = [("DIMRADIUS", 0.30), ("DIMANGULAR", 0.16), ("DIMCENTER", 0.12)],
        ["DIMANGULAR"] = [("DIMLINEAR", 0.26), ("DIMRADIUS", 0.18), ("QLEADER", 0.14)],
        ["DIMCONTINUE"] = [("DIMBASELINE", 0.26), ("DIMLINEAR", 0.22), ("MOVE", 0.12)],
        ["DIMBASELINE"] = [("DIMCONTINUE", 0.26), ("DIMLINEAR", 0.22), ("DIMSTYLE", 0.12)],
        ["QLEADER"] = [("MLEADER", 0.22), ("TOLERANCE", 0.18), ("MTEXT", 0.14)],

        // 块流程
        ["BLOCK"] = [("INSERT", 0.30), ("ATTDEF", 0.24), ("WBLOCK", 0.16), ("COPY", 0.12)],
        ["ATTDEF"] = [("BLOCK", 0.34), ("BATTMAN", 0.20), ("INSERT", 0.16)],
        ["INSERT"] = [("COPY", 0.26), ("SCALE", 0.20), ("EXPLODE", 0.16), ("ATTEDIT", 0.14)],
        ["WBLOCK"] = [("INSERT", 0.30), ("OPEN", 0.18), ("QSAVE", 0.14)],

        // 图层流程
        ["LAYER"] = [("LAYISO", 0.20), ("LAYOFF", 0.18), ("LINE", 0.16), ("CIRCLE", 0.14)],
        ["LAYISO"] = [("LINE", 0.20), ("TRIM", 0.18), ("ERASE", 0.16), ("LAYON", 0.14)],

        // 收尾流程
        ["PURGE"] = [("AUDIT", 0.30), ("QSAVE", 0.26), ("PLOT", 0.20)],
        ["AUDIT"] = [("PURGE", 0.30), ("QSAVE", 0.26), ("PLOT", 0.18)],
        ["PLOT"] = [("QSAVE", 0.30), ("EXPORT", 0.22), ("NEW", 0.14)],
        ["ERASE"] = [("U", 0.24), ("COPY", 0.18), ("LINE", 0.16), ("TRIM", 0.14)],
        ["U"] = [("REDO", 0.28), ("ERASE", 0.20), ("TRIM", 0.16)],
        ["MTEXT"] = [("TEXT", 0.22), ("QLEADER", 0.18), ("COPY", 0.14), ("DDEDIT", 0.12)],
        ["TEXT"] = [("MTEXT", 0.24), ("COPY", 0.16), ("DDEDIT", 0.14)],
    };

    /// <summary>用户行为叠加权重（0~1）：0 = 纯先验，1 = 纯个性化。默认 0.35。</summary>
    public double PersonalWeight { get; set; } = 0.35;

    private readonly IBehaviorStore _behavior;

    public TransitionTableRecommender(IBehaviorStore? behavior = null)
    {
        _behavior = behavior ?? new NullBehaviorStore();
    }

    /// <summary>Core 内置的空行为存储（无个性化数据时的默认）。</summary>
    private sealed class NullBehaviorStore : IBehaviorStore
    {
        public IReadOnlyDictionary<string, int> GetUsageCounts()
            => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public void Increment(string commandId)
        {
        }

        public void Save()
        {
        }
    }

    public IReadOnlyList<(string CommandId, double Confidence)> RecommendNext(
        IReadOnlyList<string> recentCommands,
        int maxCount = 5)
    {
        if (recentCommands.Count == 0)
        {
            return Array.Empty<(string, double)>();
        }

        var last = recentCommands[^1];
        if (!Table.TryGetValue(last, out var prior))
        {
            return Array.Empty<(string, double)>();
        }

        var usage = _behavior.GetUsageCounts();
        var scores = new List<(string Id, double Score)>(prior.Count);
        foreach (var (id, p) in prior)
        {
            var personal = usage.TryGetValue(id, out var n) ? Math.Min(n / 50.0, 1.0) : 0.0;
            var blended = (1 - PersonalWeight) * p + PersonalWeight * personal * p * 2;
            scores.Add((id, Math.Min(blended, 0.99)));
        }

        return scores
            .OrderByDescending(s => s.Score)
            .Take(maxCount)
            .Select(s => (s.Id, s.Score))
            .ToList();
    }
}
