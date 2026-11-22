namespace CadSmart.Core.Domain;

/// <summary>
/// 一条候选建议（命令 + 命中方式 + 来源）。
/// </summary>
public sealed class CommandCandidate
{
    public required CadCommand Command { get; init; }

    /// <summary>综合得分（0~100+）。</summary>
    public double Score { get; set; }

    /// <summary>规则命中方式：exact-command / alias / pinyin / initials / fuzzy / keyword 等。</summary>
    public string MatchedVia { get; set; } = "unknown";

    /// <summary>来源：rule / llm / both。</summary>
    public string Source { get; set; } = "rule";

    /// <summary>LLM 置信度（0~1，若参与）。</summary>
    public double LlmConfidence { get; set; }
}
