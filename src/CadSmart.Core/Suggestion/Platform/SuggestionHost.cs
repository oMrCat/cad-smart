namespace CadSmart.Core.Suggestion.Platform;

using CadSmart.Core.Abstractions;
using CadSmart.Core.Abstractions.Platform;
using CadSmart.Core.Domain;

/// <summary>
/// 建议宿主（阶段 3 重构 #2 的核心接线器）：
/// 把任意平台的 IPlatformAdapter（输入源 + UI）与 ISuggestionEngine 接起来。
/// 输入变化 → 防抖 → 引擎建议 → UI 展示；UI 选中 → 引擎 Accept → 平台执行命令。
/// 本类不含任何平台代码——AutoCAD/中望/浩辰共享同一份接线逻辑。
/// </summary>
public sealed class SuggestionHost : IDisposable
{
    private readonly IPlatformAdapter _adapter;
    private readonly ISuggestionEngine _engine;
    private readonly int _debounceMs;
    private CancellationTokenSource? _pending;
    private bool _initialized;

    public SuggestionHost(IPlatformAdapter adapter, ISuggestionEngine engine, int debounceMs = 120)
    {
        _adapter = adapter;
        _engine = engine;
        _debounceMs = debounceMs;
    }

    /// <summary>最近一次建议结果（供平台层在提交时取用）。</summary>
    public SuggestionResult? LastResult { get; private set; }

    /// <summary>接线启动：订阅平台事件。</summary>
    public void Start()
    {
        if (_initialized || !_adapter.Initialize())
        {
            return;
        }

        _initialized = true;
        _adapter.CommandSink.InputChanged += OnInputChanged;
        _adapter.CommandSink.InputCommitted += OnCommitted;
        _adapter.SuggestionUI.SuggestionAccepted += OnAccepted;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _adapter.CommandSink.InputChanged -= OnInputChanged;
        _adapter.CommandSink.InputCommitted -= OnCommitted;
        _adapter.SuggestionUI.SuggestionAccepted -= OnAccepted;
        _adapter.Shutdown();
        _pending?.Cancel();
        _initialized = false;
    }

    private void OnInputChanged(object? sender, string input)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();

        var ct = _pending.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(input))
                {
                    Hide();
                    return;
                }

                await Task.Delay(_debounceMs, ct).ConfigureAwait(false);

                var result = await _engine.SuggestAsync(input, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                LastResult = result;
                if (result.Items.Count == 0)
                {
                    Hide();
                }
                else
                {
                    _adapter.SuggestionUI.ShowSuggestions(result.Items);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                Hide(); // 引擎异常不拖垮 CAD 主程序
            }
        }, ct);
    }

    private void OnCommitted(object? sender, EventArgs e)
    {
        // 提交时若已有候选且用户未显式选择，保持平台原生命令行为（不拦截）
        Hide();
    }

    private void OnAccepted(object? sender, int index)
    {
        var items = LastResult?.Items;
        if (items is null || index < 1 || index > items.Count)
        {
            return;
        }

        var chosen = items[index - 1];
        _engine.Accept(chosen.Command.Id);
        _adapter.CommandSink.ExecuteCommand(chosen.Command.Id);
        Hide();
    }

    private void Hide()
    {
        try
        {
            _adapter.SuggestionUI.Hide();
        }
        catch
        {
        }
    }
}
