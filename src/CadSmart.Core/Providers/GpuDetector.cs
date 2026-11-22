namespace CadSmart.Core.Providers;

/// <summary>
/// GPU/推理设备检测结果。
/// </summary>
public sealed record GpuStatus
{
    /// <summary>GPU 名称（无 GPU 时为空）。</summary>
    public string GpuName { get; init; } = string.Empty;

    /// <summary>显存总量（MiB），未知为 0。</summary>
    public int VramTotalMiB { get; init; }

    /// <summary>当前模型体积（MiB，由 GGUF 文件大小推算），未知为 0。</summary>
    public int ModelSizeMiB { get; init; }

    /// <summary>当前推理引擎构建类型（cpu / cuda / vulkan / unknown）。</summary>
    public string EngineVariant { get; init; } = "unknown";

    /// <summary>GPU offload 是否可行（显存充足 + 引擎支持）。</summary>
    public bool CanOffload { get; init; }

    /// <summary>不可行的原因（中文，展示给用户）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>当前生效设备（gpu / cpu）。</summary>
    public string ActiveDevice { get; init; } = "cpu";
}

/// <summary>
/// GPU 推理加速检测与开关（默认 auto：有合适 GPU 即自动启用 offload）。
/// nvidia-smi 查硬件；llama-server 进程信息经 PowerShell 子进程查询（免 WMI 程序集依赖）。
/// </summary>
public static class GpuDetector
{
    /// <summary>用户偏好：auto（默认，自动识别并开启）/ cpu（强制关闭）/ gpu（强制尝试）。</summary>
    public static string PreferredMode { get; set; } = "auto";

    private static GpuStatus? _cached;

    /// <summary>完整检测（结果缓存，模式切换时失效）。</summary>
    public static GpuStatus Detect()
    {
        if (_cached is { } c &&
            c.ActiveDevice == (c.CanOffload ? (PreferredMode is "auto" or "gpu" ? "gpu" : "cpu") : "cpu"))
        {
            return c;
        }

        var (gpuName, vramMiB) = QueryNvidiaGpu();
        var (variant, modelPath) = QueryLlamaServerInfo();
        var modelMiB = QueryModelSize(modelPath);

        bool canOffload;
        string reason;
        var active = "cpu";

        if (string.IsNullOrEmpty(gpuName))
        {
            canOffload = false;
            reason = "未检测到 NVIDIA GPU";
        }
        else if (variant is not ("cuda" or "vulkan"))
        {
            canOffload = false;
            reason = $"当前推理引擎为 {variant} 构建，不支持 GPU offload（可升级 LocModel 引擎获取 CUDA 版）";
        }
        else if (modelMiB > 0 && vramMiB > 0 && vramMiB < modelMiB * 1.2)
        {
            canOffload = false;
            reason = $"显存不足：{gpuName} 仅 {vramMiB} MiB，模型需约 {modelMiB} MiB（含 KV cache）";
        }
        else
        {
            canOffload = true;
            reason = $"{gpuName}（{vramMiB} MiB）满足 offload 条件";
        }

        if (canOffload && PreferredMode is "auto" or "gpu")
        {
            active = "gpu";
        }

        var status = new GpuStatus
        {
            GpuName = gpuName,
            VramTotalMiB = vramMiB,
            ModelSizeMiB = modelMiB,
            EngineVariant = variant,
            CanOffload = canOffload,
            Reason = reason,
            ActiveDevice = active,
        };
        _cached = status;
        return status;
    }

    /// <summary>用户偏好切换（UI 开关调用；auto=自动识别并开启）。</summary>
    public static void SetMode(string mode)
    {
        PreferredMode = mode.Trim().ToLowerInvariant() switch
        {
            "cpu" => "cpu",
            "gpu" => "gpu",
            _ => "auto",
        };
        _cached = null;
    }

    /// <summary>nvidia-smi 查询 GPU 名称与显存（无 GPU/无驱动返回空）。</summary>
    private static (string name, int vramMiB) QueryNvidiaGpu()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
            p?.WaitForExit(3000);

            var parts = output.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var mib))
            {
                return (parts[0].Trim(), mib);
            }
        }
        catch
        {
            // nvidia-smi 不存在（无 N 卡/驱动）
        }

        return (string.Empty, 0);
    }

    /// <summary>
    /// 查询 llama-server 进程信息：引擎构建类型 + 模型路径。
    /// 用 PowerShell 子进程读 Win32_Process 命令行（免 WMI 程序集依赖）。
    /// </summary>
    private static (string variant, string modelPath) QueryLlamaServerInfo()
    {
        try
        {
            var cmdline = RunPowerShell(
                "(Get-CimInstance Win32_Process -Filter \"Name = 'llama-server.exe'\").CommandLine");
            if (string.IsNullOrWhiteSpace(cmdline))
            {
                // CIM 查不到 → 退化：仅从进程 exe 路径判断 variant
                cmdline = RunPowerShell("(Get-Process llama-server -ErrorAction SilentlyContinue).Path");
                if (string.IsNullOrWhiteSpace(cmdline))
                {
                    return ("unknown", string.Empty);
                }
            }

            var variant = DetectVariant(cmdline);
            var modelPath = string.Empty;
            var m = System.Text.RegularExpressions.Regex.Match(
                cmdline, @"(?:-m|--model)\s+(""([^""]+)""|(\S+\.gguf))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                modelPath = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim('"');
            }

            return (variant, modelPath);
        }
        catch
        {
            return ("unknown", string.Empty);
        }
    }

    /// <summary>执行单行 PowerShell 命令并取 stdout（5 秒超时）。</summary>
    private static string RunPowerShell(string script)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
            p?.WaitForExit(5000);
            return output.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>从命令行/路径识别引擎构建类型。</summary>
    private static string DetectVariant(string cmdline)
    {
        if (cmdline.Contains("-cuda", StringComparison.OrdinalIgnoreCase) ||
            cmdline.Contains("-gpu", StringComparison.OrdinalIgnoreCase) ||
            cmdline.Contains("-cublas", StringComparison.OrdinalIgnoreCase))
        {
            return "cuda";
        }

        if (cmdline.Contains("-vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return "vulkan";
        }

        if (cmdline.Contains("-cpu", StringComparison.OrdinalIgnoreCase) ||
            cmdline.Contains("-ngl 0", StringComparison.OrdinalIgnoreCase))
        {
            return "cpu";
        }

        return "unknown";
    }

    /// <summary>查询模型文件体积（MiB）。</summary>
    private static int QueryModelSize(string? modelPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(modelPath) && System.IO.File.Exists(modelPath))
            {
                return (int)(new System.IO.FileInfo(modelPath).Length / (1024 * 1024));
            }
        }
        catch
        {
            // 路径无效
        }

        return 0;
    }
}
