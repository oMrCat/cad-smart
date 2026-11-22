namespace CadSmart.Core.Matching;

using CadSmart.Core.Domain;
using CadSmart.Core.Abstractions;

/// <summary>
/// 规则匹配引擎：精确 / 别名 / 拼音 / 首字母 / 包含 / 模糊容错 六路命中，加权融合排序。
/// 纯本地计算，无 IO，目标：200+ 命令 &lt;50ms。
/// </summary>
public sealed class MatchEngine
{
    public const int DefaultTopN = 8;

    /// <summary>中文口语常见填充字（在关键词松匹配时剔除）；空格一并剔除实现紧凑匹配。</summary>
    private static readonly char[] FillerChars = ['个', '一', '下', '请', '帮', '我', '想', '要', '画', ' '];

    // 得分常量
    private const double ExactCommand = 100;
    private const double ExactId = 99;
    private const double ChineseExact = 94;
    private const double AliasExact = 95;
    private const double PinyinExact = 92;
    private const double InitialsExact = 90;
    private const double KeywordExact = 88;
    private const double Prefix = 85;
    private const double PrefixPinyin = 82;
    private const double KeywordPrefix = 80;
    private const double ChineseContains = 72;
    private const double Contains = 70;
    private const double KeywordLoose = 62;
    private const double KeywordContains = 58;
    private const double DescContains = 40;
    private const double FuzzyBase = 55;
    private const double FuzzyStep = 6;
    private const double QStartsCommand = 87;

    /// <summary>前缀覆盖率加成（0~5 分）：q 对目标串的覆盖比例越高越加分。</summary>
    private static double CoverageBonus(int qLen, int targetLen)
        => targetLen <= 0 ? 0 : 5.0 * qLen / targetLen;

    /// <summary>
    /// 对输入进行匹配并返回排序后的候选列表。
    /// </summary>
    public IReadOnlyList<CommandCandidate> Rank(
        string? input,
        IReadOnlyList<CadCommand> commands,
        int topN = DefaultTopN,
        IReadOnlyDictionary<string, int>? usageBoost = null)
    {
        var q = Normalize(input);
        if (q.Length == 0)
        {
            return Array.Empty<CommandCandidate>();
        }

        var looseQ = StripFillers(q);
        var pinyinVariants = BuildPinyinVariants(q);
        var looseVariants = pinyinVariants.Count > 1
            ? pinyinVariants.Select(StripFillers).Distinct().ToList()
            : _singleLooseList;

        var results = new List<CommandCandidate>(commands.Count);

        foreach (var cmd in commands)
        {
            var best = (Score: 0.0, Via: string.Empty);
            foreach (var v in pinyinVariants)
            {
                var lv = looseVariants.Count == 1 ? looseVariants[0] : StripFillers(v);
                var sc = ScoreCommand(cmd, v, lv);
                if (sc.Score > best.Score)
                {
                    best = sc;
                }
            }

            if (best.Score <= 0)
            {
                continue;
            }

            if (usageBoost is not null && usageBoost.TryGetValue(cmd.Id, out var usage) && usage > 0)
            {
                best.Score += Math.Min(usage, 50) * 0.1; // 个性化加权，最高 +5
                best.Via += "+used";
            }

            results.Add(new CommandCandidate
            {
                Command = cmd,
                Score = Math.Round(best.Score, 2),
                MatchedVia = best.Via,
            });
        }

        results.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = b.Command.UsageCount.CompareTo(a.Command.UsageCount);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Command.Id, b.Command.Id);
        });

        return results.Count <= topN ? results : results.Take(topN).ToList();
    }

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input.Trim())
        {
            // 全角 → 半角（中文输入法场景：ＬＩＮＥ、ｙｕａｎ、１２）
            if (ch >= 0xFF01 && ch <= 0xFF5E)
            {
                sb.Append((char)(ch - 0xFEE0));
                continue;
            }

            if (ch == 0x3000) // 全角空格
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static readonly List<string> _singleLooseList = new(1);

    /// <summary>
    /// 生成拼音变体（zh↔z、ch↔c、sh↔s、ng→n/g），覆盖方言口音与快打习惯。
    /// 规则：单字母 z/s/c 后面已是 h 时不扩展（避免 zhh 废变体）；变体封顶 8 个；原输入始终在首位。
    /// </summary>
    public static List<string> BuildPinyinVariants(string q)
    {
        var variants = new List<string> { q };
        if (q.Length < 2 || q.Any(c => c > 0x7F))
        {
            return variants; // 短输入或含中文：无变体意义
        }

        var pairs = new (string From, string To)[]
        {
            ("zh", "z"), ("ch", "c"), ("sh", "s"),
            ("z", "zh"), ("c", "ch"), ("s", "sh"),
            ("ng", "n"), ("ng", "g"),
        };

        var queue = new List<string> { q };
        for (var i = 0; i < queue.Count && variants.Count < 8; i++)
        {
            var cur = queue[i];
            foreach (var (from, to) in pairs)
            {
                if (variants.Count >= 8)
                {
                    break;
                }

                var idx = cur.IndexOf(from, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                // 防废变体：z/s/c → zh/sh/ch 时，后面已经是 h 则跳过
                if (from.Length == 1 && to.Length == 2
                    && idx + 1 < cur.Length && cur[idx + 1] == 'h')
                {
                    continue;
                }

                var candidate = cur[..idx] + to + cur[(idx + from.Length)..];
                if (!variants.Contains(candidate))
                {
                    variants.Add(candidate);
                    queue.Add(candidate);
                }
            }
        }

        return variants;
    }

    private static string StripFillers(string q)
    {
        var sb = new System.Text.StringBuilder(q.Length);
        foreach (var ch in q)
        {
            if (Array.IndexOf(FillerChars, ch) < 0)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static (double Score, string Via) ScoreCommand(CadCommand cmd, string q, string looseQ)
    {
        double best = 0;
        var via = string.Empty;

        void Consider(double score, string name)
        {
            if (score > best)
            {
                best = score;
                via = name;
            }
        }

        // ---- 精确类 ----
        // q 已转小写；命令名/别名统一用 IgnoreCase 比较
        if (string.Equals(cmd.Name, q, StringComparison.OrdinalIgnoreCase)) Consider(ExactCommand, "exact-command");
        if (string.Equals(cmd.Id, q, StringComparison.OrdinalIgnoreCase)) Consider(ExactId, "exact-id");
        if (cmd.Aliases.Contains(q, StringComparer.OrdinalIgnoreCase)) Consider(AliasExact, "alias");
        if (cmd.Pinyin.Length > 0 && string.Equals(cmd.Pinyin, q, StringComparison.Ordinal)) Consider(PinyinExact, "pinyin");
        if (cmd.PinyinInitials.Length > 0 && string.Equals(cmd.PinyinInitials, q, StringComparison.Ordinal)) Consider(InitialsExact, "initials");
        if (cmd.ChineseName.Length > 0 && cmd.ChineseName == q) Consider(ChineseExact, "chinese-exact");

        // ---- 前缀类 ----
        // 前缀覆盖率加成：短目标的 q 覆盖率高更可能是用户想找的（zhi→zhixian 优先于 zhijingbiaozhu）
        if (cmd.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            cmd.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            Consider(Prefix + CoverageBonus(q.Length, cmd.Name.Length), "prefix");
        }

        // 输入以完整命令名开头（多打了字符）→ 强信号
        if (q.StartsWith(cmd.Name, StringComparison.OrdinalIgnoreCase) && q.Length > cmd.Name.Length)
        {
            Consider(QStartsCommand, "q-extends-command");
        }

        if (q.StartsWith(cmd.ChineseName, StringComparison.Ordinal) && q.Length > cmd.ChineseName.Length)
        {
            Consider(QStartsCommand - 1, "q-extends-chinese");
        }

        foreach (var a in cmd.Aliases)
        {
            if (a.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                Consider(Prefix - 1 + CoverageBonus(q.Length, a.Length), "prefix-alias");
            }
        }

        if (cmd.Pinyin.StartsWith(q, StringComparison.Ordinal))
        {
            Consider(PrefixPinyin + CoverageBonus(q.Length, cmd.Pinyin.Length), "prefix-pinyin");
        }

        if (cmd.PinyinInitials.StartsWith(q, StringComparison.Ordinal))
        {
            Consider(PrefixPinyin - 1 + CoverageBonus(q.Length, cmd.PinyinInitials.Length), "prefix-initials");
        }

        // ---- 关键词 ----
        foreach (var kw in cmd.Keywords)
        {
            var k = kw.ToLowerInvariant();
            if (k == q) Consider(KeywordExact, "keyword");
            else if (k.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(k, StringComparison.Ordinal))
            {
                Consider(KeywordPrefix, "keyword-prefix");
            }
            else if (q.Contains(k, StringComparison.Ordinal)) Consider(KeywordContains, "keyword-contains");
        }

        // ---- 包含类 ----
        if (cmd.ChineseName.Contains(q, StringComparison.Ordinal) ||
            (looseQ.Length >= 2 && cmd.ChineseName.Contains(looseQ, StringComparison.Ordinal)))
        {
            Consider(ChineseContains, "chinese-contains");
        }

        if (cmd.Pinyin.Contains(q, StringComparison.Ordinal)) Consider(Contains, "contains-pinyin");
        if (cmd.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) Consider(Contains, "contains-command");

        if (cmd.Description.Contains(q, StringComparison.Ordinal) ||
            (looseQ.Length >= 2 && cmd.Description.Contains(looseQ, StringComparison.Ordinal)))
        {
            Consider(DescContains, "desc-contains");
        }

        // ---- 模糊容错（仅对较长的输入，且未命中更强规则时兜底）----
        // 统一在小写空间比较：q 已是小写，命令名/别名先转小写
        if (q.Length >= 3)
        {
            var maxDist = q.Length <= 4 ? 1 : 2;

            var nameLower = cmd.Name.ToLowerInvariant();
            var dName = LevenshteinBounded(nameLower, q, maxDist);
            if (dName >= 0) Consider(FuzzyBase - dName * FuzzyStep, "fuzzy-name");

            var dPinyin = cmd.Pinyin.Length > 0 ? LevenshteinBounded(cmd.Pinyin, q, maxDist) : -1;
            if (dPinyin >= 0) Consider(FuzzyBase - dPinyin * FuzzyStep, "fuzzy-pinyin");

            var dInit = cmd.PinyinInitials.Length > 0 ? LevenshteinBounded(cmd.PinyinInitials, q, maxDist) : -1;
            if (dInit >= 0) Consider(FuzzyBase - dInit * FuzzyStep, "fuzzy-initials");

            foreach (var a in cmd.Aliases)
            {
                var dAlias = LevenshteinBounded(a.ToLowerInvariant(), q, maxDist);
                if (dAlias >= 0) Consider(FuzzyBase - dAlias * FuzzyStep + 2, "fuzzy-alias");
            }
        }

        return (best, via);
    }

    /// <summary>
    /// 带阈值上界的编辑距离；超过阈值返回 -1（提前放弃，保证性能）。
    /// </summary>
    public static int LevenshteinBounded(string a, string b, int maxDist)
    {
        if (Math.Abs(a.Length - b.Length) > maxDist)
        {
            return -1;
        }

        if (a == b)
        {
            return 0;
        }

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
                if (curr[j] < rowMin)
                {
                    rowMin = curr[j];
                }
            }

            if (rowMin > maxDist)
            {
                return -1; // 整行已超阈值，提前放弃
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length] <= maxDist ? prev[b.Length] : -1;
    }
}
