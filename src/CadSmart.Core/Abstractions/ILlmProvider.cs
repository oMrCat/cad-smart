namespace CadSmart.Core.Abstractions;

using CadSmart.Core.Domain;

/// <summary>
/// 一条 LLM 推荐结果。
/// </summary>
public sealed record LlmRecommendation(string CommandId, double Confidence);

/// <summary>
/// LLM 供应商统一接口（可插拔：Cloud / Local / Mock / Null）。
/// 实现必须是纯异步 HTTP 或本地计算，不得阻塞调用方。
/// </summary>
public interface ILlmProvider
{
    /// <summary>供应商名称（cloud / local / mock / null）。</summary>
    string Name { get; }

    /// <summary>当前是否可用（探测/配置判定）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 根据用户输入与候选命令范围给出推荐。
    /// 返回 null 表示本 provider 无结果（交由规则兜底）。
    /// </summary>
    Task<IReadOnlyList<LlmRecommendation>?> RecommendAsync(
        string userInput,
        IReadOnlyList<CadCommand> candidateScope,
        CancellationToken cancellationToken = default);
}
