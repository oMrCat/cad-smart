// ============================================================================
// 中望 CAD 适配器骨架（阶段 3，ZRX 二次开发）。
//
// 真机开发步骤（需安装中望 CAD + ZRX SDK）：
//   1. 引用 ZRX 头文件与库（zcad.sdk：zaApp/zcdb/zcEd 等，或使用 .NET 引擎 zwsoft 的
//      ZdDb/ZdAp 托管程序集——与 AutoCAD .NET API 高度兼容）。
//   2. 中望 .NET API 与 AutoCAD ObjectARX .NET 托管 API 命名空间几乎一一对应
//      （Autodesk.* → ZwSoft.ZwCAD.*），因此本骨架与 Adapter.AutoCAD 结构完全一致。
//   3. 核心接线使用共享的 SuggestionHost（见 Core/Suggestion/Platform）。
// ============================================================================

namespace CadSmart.Adapter.ZWCAD;

using CadSmart.Core.Abstractions.Platform;
using CadSmart.Core.Domain;

/// <summary>
/// 中望 CAD 平台适配器骨架。
/// 无中望环境：以"手动驱动"模式参与联调（SimulateInput）；
/// 有中望环境：把 TODO 处换成 ZdEd.Editor 输入监听与 ZdAp.Document.SendStringToExecute。
/// </summary>
public sealed class ZwcadAdapter : IPlatformAdapter
{
    private readonly CommandLineSink _sink = new();
    private readonly SuggestionPanel _panel = new();

    public string PlatformName => "ZWCAD";

    public ICommandSink CommandSink => _sink;

    public ISuggestionUI SuggestionUI => _panel;

    public bool IsAvailable { get; private set; }

    public bool Initialize()
    {
        // TODO(真机): 注册 ZdAp.Application.DocumentManager 文档事件 + 命令行监听
        IsAvailable = true;
        return IsAvailable;
    }

    public void Shutdown()
    {
        // TODO(真机): 移除事件订阅
        IsAvailable = false;
    }

    /// <summary>联调辅助：模拟命令行输入。</summary>
    public void SimulateInput(string text) => _sink.SimulateInput(text);

    /// <summary>联调辅助：模拟用户选中候选。</summary>
    public void SimulateAccept(int index) => _panel.SimulateAccept(index);
}

/// <summary>命令行输入源骨架（与 AutoCAD 版同构）。</summary>
public sealed class CommandLineSink : ICommandSink
{
    private string _currentInput = string.Empty;

    public string GetCurrentInput() => _currentInput;

    public void ExecuteCommand(string commandId)
    {
        // TODO(真机): doc.SendStringToExecute(commandId + "\n", true);
        CommandExecuted?.Invoke(this, commandId);
    }

    public void SimulateInput(string text)
    {
        _currentInput = text;
        InputChanged?.Invoke(this, text);
    }

    public void SimulateCommit() => InputCommitted?.Invoke(this, EventArgs.Empty);

    public event EventHandler<string>? InputChanged;

    public event EventHandler? InputCommitted;

    public event EventHandler<string>? CommandExecuted;
}

/// <summary>候选框 UI 骨架（真机替换为 ZdAp Windows 浮动窗口）。</summary>
public sealed class SuggestionPanel : ISuggestionUI
{
    private IReadOnlyList<CommandCandidate>? _items;

    public void ShowSuggestions(IReadOnlyList<CommandCandidate> items)
    {
        _items = items;
        LastShownCount = items.Count;
    }

    public void Hide() => _items = null;

    public int LastShownCount { get; private set; }

    public void SimulateAccept(int index) => SuggestionAccepted?.Invoke(this, index);

    public event EventHandler<int>? SuggestionAccepted;
}
