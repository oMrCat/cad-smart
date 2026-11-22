namespace CadSmart.Core.Abstractions.Platform;

using CadSmart.Core.Domain;

/// <summary>
/// 平台适配 SDK（阶段 3 重构 #2）：一个接口统一 AutoCAD / 中望 / 浩辰等平台的接入。
/// 平台实现此接口 + 平台 UI 实现 ISuggestionUI，即可被 SuggestionHost 驱动，
/// 核心逻辑（引擎/知识库/LLM）与平台完全解耦。
/// </summary>
public interface IPlatformAdapter
{
    /// <summary>平台名称（AutoCAD / ZWCAD / GstarCAD）。</summary>
    string PlatformName { get; }

    /// <summary>命令输入源（命令行捕获）。</summary>
    ICommandSink CommandSink { get; }

    /// <summary>候选建议 UI（浮动候选框）。</summary>
    ISuggestionUI SuggestionUI { get; }

    /// <summary>平台是否受支持/已初始化成功。</summary>
    bool IsAvailable { get; }

    /// <summary>初始化（注册命令行监听等）。成功返回 true。</summary>
    bool Initialize();

    /// <summary>释放资源（移除监听）。</summary>
    void Shutdown();
}
