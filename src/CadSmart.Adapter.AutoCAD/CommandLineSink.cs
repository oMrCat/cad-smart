// ============================================================================
// AutoCAD 适配器骨架（阶段 0 占位实现）
//
// 设计约定（见 docs/架构演进与重构路线图.md §2）：
//   本适配器是"薄壳"，只做两件事：
//     1. 捕获命令行输入 → 转发为 InputChanged 事件
//     2. 接收候选 UI 事件 → 调用核心服务 → 回显/执行
//   所有智能逻辑都在 CadSmart.Core，本文件不写业务。
//
// 真机开发步骤（需要安装 AutoCAD 后执行，当前环境无 CAD，故以占位类保编译）：
//   1. csproj 中解除 AutoCAD 引用注释（acmgd.dll / acdbmgd.dll / accoremgd.dll，
//      位于 <AutoCAD安装目录>，或将 TargetFramework 改为 AutoCAD 对应版本，
//      例如 AutoCAD 2024/2025 → net8.0-windows，2021~2023 → net48）。
//   2. 将 CommandLineSink 中的 TODO 换成真实 API：
//        - 输入捕获：DocumentManager + Editor 的命令事件
//          （Autodesk.AutoCAD.ApplicationServices.Application.
//           DocumentManager.DocumentCreated / Document.CommandWillStart 等），
//          或注册自定义输入监听（阶段0 POC 的核心验证点）。
//        - 命令执行：doc.SendStringToExecute(commandId + "\n", true)
//      详见 #if HAS_AUTOCAD 区块内的示例代码骨架。
//   3. 注册 NETLOAD 入口命令（Autodesk.AutoCAD.Runtime.CommandMethod）。
// ============================================================================

#if HAS_AUTOCAD
// 真机编译时启用（在 csproj 中定义 <DefineConstants>HAS_AUTOCAD</DefineConstants>）：
// using Autodesk.AutoCAD.ApplicationServices;
// using Autodesk.AutoCAD.EditorInput;
// using Autodesk.AutoCAD.Runtime;
#endif

namespace CadSmart.Adapter.AutoCAD;

using CadSmart.Core.Abstractions.Platform;

/// <summary>
/// 命令行输入源占位实现。
/// 无 CAD 环境：手动触发（供模拟器/UI 联调）；
/// 有 CAD 环境：替换内部实现为真实命令行监听。
/// </summary>
public sealed class CommandLineSink : ICommandSink
{
    private string _currentInput = string.Empty;

    public string GetCurrentInput() => _currentInput;

    public void ExecuteCommand(string commandId)
    {
        // TODO(真机): doc.SendStringToExecute(commandId + "\n", true);
        CommandExecuted?.Invoke(this, commandId);
    }

    /// <summary>模拟命令行输入变化（无 CAD 环境联调用）。</summary>
    public void SimulateInput(string text)
    {
        _currentInput = text;
        InputChanged?.Invoke(this, text);
    }

    /// <summary>模拟用户回车提交。</summary>
    public void SimulateCommit()
    {
        InputCommitted?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<string>? InputChanged;

    public event EventHandler? InputCommitted;

    public event EventHandler<string>? CommandExecuted;
}

#if HAS_AUTOCAD
// ---------------------------------------------------------------------------
// 真机实现参考（阶段 0 POC 时取消注释并调整）：
//
// public sealed class RealCommandLineSink : ICommandSink
// {
//     private readonly Document _doc;
//
//     public RealCommandLineSink(Document doc)
//     {
//         _doc = doc;
//         // POC 验证点：命令行输入拦截方式
//         // 方案 A：Editor 未公开的输入事件（需验证 API 深度）——路线图最高风险项
//         // 方案 B：注册键盘钩子 + Editor 消息转发（退化方案，见路线图 §6）
//         // 方案 C：不接管原生命令行，提供浮动提示条跟随输入（兜底方案）
//     }
//
//     public void ExecuteCommand(string commandId)
//         => _doc.SendStringToExecute(commandId + "\n", true);
//
//     public event EventHandler<string>? InputChanged;
//     public event EventHandler? InputCommitted;
// }
//
// public sealed class PluginEntry
// {
//     [CommandMethod("CADSMART")]
//     public void Start()
//     {
//         // NETLOAD 后输入 CADSMART 启动候选框浮窗（WPF）
//     }
// }
// ---------------------------------------------------------------------------
#endif
