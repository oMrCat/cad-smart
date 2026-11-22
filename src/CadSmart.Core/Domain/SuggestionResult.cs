namespace CadSmart.Core.Domain;

/// <summary>
/// 一次建议请求的完整结果。
/// </summary>
public sealed class SuggestionResult
{
    public IReadOnlyList<CommandCandidate> Items { get; init; } = Array.Empty<CommandCandidate>();

    /// <summary>规则引擎耗时（毫秒）。</summary>
    public long RuleLatencyMs { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public long TotalLatencyMs { get; set; }

    /// <summary>本轮 LLM 是否给出了结果。</summary>
    public bool LlmUsed { get; set; }

    /// <summary>LLM 是否超时被放弃（降级为纯规则）。</summary>
    public bool LlmDegraded { get; set; }
}
