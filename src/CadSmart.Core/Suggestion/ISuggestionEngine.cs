namespace CadSmart.Core.Suggestion;

using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;

/// <summary>
/// 建议引擎抽象（阶段 2 重构 #1 的接缝）：
/// 进程内实现（InProcessSuggestionEngine）与远程服务客户端（RemoteSuggestionEngine，Data 项目）共享此契约，
/// 插件层完全无感切换。
/// </summary>
public interface ISuggestionEngine
{
    Task<SuggestionResult> SuggestAsync(string? userInput, CancellationToken ct = default);

    /// <summary>上下文感知推荐：根据最近执行序列推荐下一个命令。</summary>
    SuggestionResult SuggestNext();

    void Accept(string commandId);
}
