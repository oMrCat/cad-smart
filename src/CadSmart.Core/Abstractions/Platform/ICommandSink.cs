namespace CadSmart.Core.Abstractions.Platform;

using CadSmart.Core.Domain;

/// <summary>
/// 平台命令输入源（由 CAD 适配器实现：捕获命令行输入）。
/// 核心引擎只依赖此抽象，不依赖任何 CAD API。
/// </summary>
public interface ICommandSink
{
    /// <summary>当前命令行输入内容（无输入为空串）。</summary>
    string GetCurrentInput();

    /// <summary>用户确认执行某个命令（按候选编号回车）。</summary>
    void ExecuteCommand(string commandId);

    /// <summary>命令行输入发生变化。</summary>
    event EventHandler<string>? InputChanged;

    /// <summary>用户提交（回车）当前输入。</summary>
    event EventHandler? InputCommitted;
}

/// <summary>
/// 候选建议 UI（由平台实现：跟随命令行的浮动候选框）。
/// </summary>
public interface ISuggestionUI
{
    /// <summary>显示候选列表（已按得分排序）。</summary>
    void ShowSuggestions(IReadOnlyList<CommandCandidate> items);

    /// <summary>隐藏候选框（无候选时）。</summary>
    void Hide();

    /// <summary>用户选中某条候选（从 1 开始的序号）。</summary>
    event EventHandler<int>? SuggestionAccepted;
}
