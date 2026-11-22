// ============================================================================
// WPF 候选框原型（阶段 0 "候选框 UI 可行性"验证）：
//   一个模拟 CAD 命令行的输入框 + 跟随输入框的浮动候选列表。
//   真机形态 = 此窗口改为无边框 Topmost 浮层 + 定位到 CAD 命令行上方。
// 验证点：防抖、键盘上下选择、回车执行、Esc 隐藏、个性化计数。
// ============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CadSmart.Simulator.Wpf;

public partial class MainWindow : Window
{
    private readonly JsonFileBehaviorStore _behavior;
    private readonly JsonCommandRepository _repo;
    private readonly SuggestionService _service;
    private SuggestionResult? _current;
    private CancellationTokenSource? _debounce;
    private string _gpuMode = "auto"; // auto / cpu / gpu（与引擎 GpuDetector.PreferredMode 同步）

    public MainWindow()
    {
        InitializeComponent();

        var usageFile = System.IO.Path.Combine(AppContext.BaseDirectory, "usage.json");
        _behavior = new JsonFileBehaviorStore(usageFile);
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        _repo = JsonCommandRepository.LoadFromStream(stream, _behavior);
        _service = new SuggestionService(_repo, _behavior, new MatchEngine(), new NullLlmProvider());

        Loaded += async (_, _) => await RefreshGpuStatusAsync();
        InputBox.Focus();
    }

    /// <summary>
    /// GPU 加速开关：auto → gpu → cpu 循环切换，并同步到引擎服务（POST /gpu/mode）。
    /// 默认 auto = 自动识别并开启。
    /// </summary>
    private async void GpuButton_Click(object sender, RoutedEventArgs e)
    {
        _gpuMode = _gpuMode switch
        {
            "auto" => "gpu",
            "gpu" => "cpu",
            _ => "auto",
        };

        UpdateGpuButtonLabel("切换中…");

        // 本地模式直接调 GpuDetector（同进程引擎）；远程引擎模式走 HTTP（尽力而为）
        GpuDetector.SetMode(_gpuMode);
        var status = GpuDetector.Detect();
        ShowGpuStatus(status);

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var content = new StringContent(
                JsonSerializer.Serialize(new { mode = _gpuMode }),
                Encoding.UTF8, "application/json");
            await http.PostAsync("http://127.0.0.1:46200/gpu/mode", content);
        }
        catch
        {
            // 引擎服务未启动时仅本地生效（WPF 模拟器内置引擎场景）
        }
    }

    private async Task RefreshGpuStatusAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync("http://127.0.0.1:46200/gpu/status");
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var s = new GpuStatus
            {
                GpuName = doc.TryGetProperty("GpuName", out var gn) ? gn.GetString() ?? "" : "",
                EngineVariant = doc.TryGetProperty("EngineVariant", out var ev) ? ev.GetString() ?? "" : "",
                CanOffload = doc.TryGetProperty("CanOffload", out var co) && co.GetBoolean(),
                Reason = doc.TryGetProperty("Reason", out var rs) ? rs.GetString() ?? "" : "",
                ActiveDevice = doc.TryGetProperty("ActiveDevice", out var ad) ? ad.GetString() ?? "cpu" : "cpu",
            };
            ShowGpuStatus(s);
        }
        catch
        {
            // 引擎服务不可达：用本地检测（WPF 模拟器与引擎可能不在同一进程）
            try
            {
                ShowGpuStatus(GpuDetector.Detect());
            }
            catch
            {
                GpuInfoText.Text = "GPU 状态未知（引擎未启动）";
            }
        }
    }

    private void ShowGpuStatus(GpuStatus s)
    {
        UpdateGpuButtonLabel(_gpuMode);
        GpuInfoText.Text = s.GpuName is { Length: > 0 }
            ? $"{s.GpuName} · {s.EngineVariant} 构建 · {(s.ActiveDevice == "gpu" ? "GPU 加速已启用" : "CPU 模式")}{(s.Reason is { Length: > 0 } ? $" — {s.Reason}" : "")}"
            : "无 NVIDIA GPU · CPU 模式";
    }

    private void UpdateGpuButtonLabel(string mode)
    {
        GpuButton.Content = mode switch
        {
            "auto" => "⚡ GPU 加速: 自动",
            "gpu" => "⚡ GPU 加速: 强制开启",
            _ => "⚡ GPU 加速: 已关闭",
        };
        // 有 GPU 且可用时按钮高亮
        var s = GpuDetector.Detect();
        GpuButton.Foreground = s.CanOffload && mode is "auto" or "gpu"
            ? System.Windows.Media.Brushes.LimeGreen
            : System.Windows.Media.Brushes.Gray;
    }

    private async void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        var ct = _debounce.Token;

        try
        {
            var input = InputBox.Text;
            if (string.IsNullOrWhiteSpace(input))
            {
                HideList();
                return;
            }

            await Task.Delay(120, ct);
            var result = await _service.SuggestAsync(input, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _current = result;
            if (result.Items.Count == 0)
            {
                HideList();
                return;
            }

            CandidateList.ItemsSource = result.Items.Take(8).ToList();
            CandidateList.SelectedIndex = 0;
            CandidateList.Visibility = Visibility.Visible;
            StatusText.Text = $"规则 {result.RuleLatencyMs}ms";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when CandidateList.Visibility == Visibility.Visible:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up when CandidateList.Visibility == Visibility.Visible:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter when CandidateList.Visibility == Visibility.Visible
                && CandidateList.SelectedItem is CommandCandidate chosen:
                Execute(chosen);
                e.Handled = true;
                break;
            case Key.Escape:
                HideList();
                e.Handled = true;
                break;
        }
    }

    private void OnCandidateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is CommandCandidate chosen)
        {
            Execute(chosen);
        }
    }

    private void MoveSelection(int delta)
    {
        var next = CandidateList.SelectedIndex + delta;
        if (next >= 0 && next < CandidateList.Items.Count)
        {
            CandidateList.SelectedIndex = next;
            CandidateList.ScrollIntoView(CandidateList.SelectedItem);
        }
    }

    private void Execute(CommandCandidate chosen)
    {
        _service.Accept(chosen.Command.Id);
        _behavior.Save();

        var usage = _behavior.GetUsageCounts().GetValueOrDefault(chosen.Command.Id);
        ExecutedText.Text = $"→ {chosen.Command.Id} {chosen.Command.ChineseName}（第 {usage} 次使用）";
        StatusText.Text = $"已执行 · 规则 {_current?.RuleLatencyMs ?? 0}ms";

        InputBox.Clear();
        HideList();
        ShowContextNext();
        InputBox.Focus();
    }

    /// <summary>上下文感知：执行后无输入状态下弹出"下一个命令"建议。</summary>
    private void ShowContextNext()
    {
        var next = _service.SuggestNext();
        if (next.Items.Count == 0)
        {
            return;
        }

        _current = next;
        CandidateList.ItemsSource = next.Items;
        CandidateList.SelectedIndex = 0;
        CandidateList.Visibility = Visibility.Visible;
        StatusText.Text = $"下一步建议（Esc 关闭）· {next.TotalLatencyMs}ms";
    }

    private void HideList()
    {
        CandidateList.ItemsSource = null;
        CandidateList.Visibility = Visibility.Collapsed;
        _current = null;
    }
}
