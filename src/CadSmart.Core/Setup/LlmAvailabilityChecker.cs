namespace CadSmart.Core.Setup;

using CadSmart.Core.Abstractions;
using CadSmart.Core.Providers;

/// <summary>
/// LLM 可用性状态。
/// </summary>
public sealed record LlmAvailability(
    string Mode,
    string? LocalRuntimeName,
    int? LocalPort,
    bool CloudConfigured,
    string UserMessage,
    IReadOnlyList<SetupOption> SetupOptions)
{
    /// <summary>local = 本地运行时就绪；cloud = 云端已配置；none = 全无（纯规则）。</summary>
    public bool IsEnhanced => Mode != "none";
}

/// <summary>
/// 一个可选的本地运行时安装引导项。
/// </summary>
public sealed record SetupOption(
    string Name,
    string Description,
    string DownloadUrl,
    string ModelSuggestion,
    string Port,
    bool IsRecommended);

/// <summary>
/// 本地智能可用性检查 + 安装引导（路线 A：探测 + 引导安装）。
/// 用途：设置页/状态栏显示当前智能模式；检测不到本地运行时时引导用户安装。
/// </summary>
public static class LlmAvailabilityChecker
{
    /// <summary>内置引导清单：LocModel 优先（中文用户），Ollama / LM Studio 备选。</summary>
    public static IReadOnlyList<SetupOption> SetupOptions { get; } = new SetupOption[]
    {
        new(
            Name: "LocModel",
            Description: "中文界面本地大模型工作室：一键安装推理引擎、自动识别硬件、导入 GGUF，数据不出本机",
            DownloadUrl: "https://locmodel.com/",
            ModelSuggestion: "Qwen2.5-1.5B/3B-Instruct（中文好、显存占用小）",
            Port: "以 LocModel 实际 API 端口为准，可在插件设置中手动填写",
            IsRecommended: true),
        new(
            Name: "Ollama",
            Description: "国际通用本地模型运行时（命令行为主），OpenAI 兼容 API",
            DownloadUrl: "https://ollama.com/download",
            ModelSuggestion: "ollama pull qwen2.5:1.5b",
            Port: "11434（默认，无需配置）",
            IsRecommended: false),
        new(
            Name: "LM Studio",
            Description: "图形化本地模型管理器，OpenAI 兼容服务可一键开启",
            DownloadUrl: "https://lmstudio.ai/",
            ModelSuggestion: "Qwen2.5-1.5B-Instruct GGUF",
            Port: "1234（需在 LM Studio 中开启 Local Server）",
            IsRecommended: false),
    };

    /// <summary>
    /// 检查当前智能增强模式：本地运行时探测 → 云端配置检查 → 兜底纯规则。
    /// </summary>
    public static LlmAvailability Check(ILlmProvider? provider, bool cloudConfigured, int? localPort = null)
    {
        // 1. 本地运行时（LocalLlmProvider 已探测成功）
        if (provider is LocalLlmProvider { IsAvailable: true } local)
        {
            return new LlmAvailability(
                Mode: "local",
                LocalRuntimeName: DetectRuntimeName(localPort ?? GuessPort(local)),
                LocalPort: localPort ?? GuessPort(local),
                CloudConfigured: cloudConfigured,
                UserMessage: "本地智能已就绪（模型运行在本机，数据不出电脑）",
                SetupOptions: SetupOptions);
        }

        // 2. 云端
        if (provider is CloudLlmProvider { IsAvailable: true } || cloudConfigured)
        {
            return new LlmAvailability(
                Mode: "cloud",
                LocalRuntimeName: null,
                LocalPort: null,
                CloudConfigured: true,
                UserMessage: "云端智能已就绪（输入内容将发送到大模型服务）",
                SetupOptions: SetupOptions);
        }

        // 3. Mock（开发模式）
        if (provider is MockLlmProvider)
        {
            return new LlmAvailability(
                Mode: "mock",
                LocalRuntimeName: null,
                LocalPort: null,
                CloudConfigured: false,
                UserMessage: "开发模式（Mock 语义供应商）",
                SetupOptions: SetupOptions);
        }

        // 4. 全无 → 纯规则 + 引导安装
        return new LlmAvailability(
            Mode: "none",
            LocalRuntimeName: null,
            LocalPort: null,
            CloudConfigured: false,
            UserMessage: "当前为纯规则模式（离线可用）。安装本地模型运行时可解锁语义推荐",
            SetupOptions: SetupOptions);
    }

    private static int GuessPort(LocalLlmProvider local)
    {
        // LocalLlmProvider 探测成功后会把 Endpoint 写入 Options，从中解析端口
        try
        {
            var field = typeof(OpenAiCompatibleProviderBase)
                .GetField("Options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var opts = (LlmOptions)field.GetValue(local)!;
            if (Uri.TryCreate(opts.Endpoint, UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string DetectRuntimeName(int port) => port switch
    {
        11434 => "Ollama",
        1234 => "LM Studio",
        > 0 => $"本地运行时(:{port})",
        _ => "本地运行时",
    };
}
