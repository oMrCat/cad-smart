// ============================================================================
// 浩辰 CAD 适配器骨架（阶段 3，GRX 二次开发）。
//
// 真机开发步骤（需安装浩辰 CAD + GRX SDK）：
//   1. 引用 GRX 库（gcadapi：gcdb/gced/gcap 等）或浩辰 .NET 托管程序集
//      （与 AutoCAD .NET API 高度兼容，Autodesk.* → Gcad.* 对应关系）。
//   2. 接线使用共享的 SuggestionHost；本骨架职责仅为平台事件桥接。
// ============================================================================

namespace CadSmart.Adapter.GstarCAD;

using CadSmart.Core.Abstractions.Platform;
using CadSmart.Core.Domain;

/// <summary>
/// 浩辰 CAD 平台适配器骨架（与 ZwcadAdapter 同构）。
/// </summary>
public sealed class GstarcadAdapter : IPlatformAdapter
{
    private readonly CommandLineSink _sink = new();
    private readonly SuggestionPanel _panel = new();

    public string PlatformName => "GstarCAD";

    public ICommandSink CommandSink => _sink;

    public ISuggestionUI SuggestionUI => _panel;

    public bool IsAvailable { get; private set; }

    public bool Initialize()
    {
        // TODO(真机): 注册 Gcad.Application 文档事件 + 命令行监听
        IsAvailable = true;
        return IsAvailable;
    }

    public void Shutdown()
    {
        IsAvailable = false;
    }

    public void SimulateInput(string text) => _sink.SimulateInput(text);

    public void SimulateAccept(int index) => _panel.SimulateAccept(index);
}

/// <summary>命令行输入源骨架。</summary>
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

/// <summary>候选框 UI 骨架（真机替换为浩辰窗口浮层）。</summary>
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
