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

namespace CadSmart.Simulator.Wpf;

public partial class MainWindow : Window
{
    private readonly JsonFileBehaviorStore _behavior;
    private readonly JsonCommandRepository _repo;
    private readonly SuggestionService _service;
    private SuggestionResult? _current;
    private CancellationTokenSource? _debounce;

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

        InputBox.Focus();
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
