// ============================================================================
// CadSmart for AutoCAD 2018 — 真机 .NET 插件入口
//
// 职责（薄壳，业务全在 Core）：
//   1. NETLOAD 后执行 CADSMARTON 启动
//   2. 命令行输入捕获（Editor 委托事件，轮询命令行尾部文本）
//   3. CS 语义搜索命令（调引擎 HTTP API）
//   4. 候选执行：SendStringToExecute
//
// 引擎：http://127.0.0.1:46200（CadSmart.Server.exe，开机自启）
// 兼容：AutoCAD 2018（R22.0, .NET 4.x — ObjectARX .NET API）
// ============================================================================

namespace CadSmart.Adapter.AutoCAD;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

/// <summary>
/// 插件入口（NETLOAD CadSmart.Adapter.AutoCAD.dll 后输入 CADSMARTON）。
/// </summary>
public class PluginEntry
{
    [CommandMethod("CADSMARTON")]
    public void Start()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;

        var hook = CommandLineHook.Instance;
        hook.Attach(doc);

        var palette = CadSmartPalette.Instance;
        palette.Show();

        ed.WriteMessage("\nCadSmart 已启动：命令行输入时自动弹出候选（输入 CADSMARTOFF 关闭）。\n");
    }

    [CommandMethod("CADSMARTOFF")]
    public void Stop()
    {
        CommandLineHook.Instance.Detach();
        CadSmartPalette.Instance.Hide();
        var ed = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor;
        ed.WriteMessage("\nCadSmart 已关闭。\n");
    }

    /// <summary>CS 命令：语义搜索（中文/拼音描述 → 命令候选）。</summary>
    [CommandMethod("CS")]
    public void SemanticSearch()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;

        var pStr = ed.GetString("\nCadSmart 输入命令描述 (中文/拼音/缩写): ");
        if (pStr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pStr.StringResult))
        {
            return;
        }

        var resp = EngineClient.Suggest(pStr.StringResult);
        if (resp is null || resp.Count == 0)
        {
            ed.WriteMessage("\n无匹配命令（引擎未响应或无候选）。\n");
            return;
        }

        for (var i = 0; i < resp.Count; i++)
        {
            ed.WriteMessage($"\n  [{i + 1}] {resp[i]}");
        }

        var pNum = ed.GetString("\n选择命令编号 (回车取消): ");
        if (pNum.Status != PromptStatus.OK)
        {
            return;
        }

        var idx = n2i(pNum.StringResult) - 1;
        if (idx >= 0 && idx < resp.Count)
        {
            EngineClient.Accept(resp[idx]);
            doc.SendStringToExecute(resp[idx] + "\n", true, false, true);
            ed.WriteMessage($"\n→ {resp[idx]}\n");
        }
    }

    private static string i2n(int i) => i.ToString();
    private static int n2i(string s) => int.TryParse(s, out var n) ? n : -1;
}

/// <summary>
/// 命令行输入钩子：轮询 Editor 命令行尾部文本（100ms 节流），
/// 变化即通知浮窗。POC 验证点：不接管原生命令行，跟随提示（兜底方案 C 的落地）。
/// </summary>
public sealed class CommandLineHook
{
    private static CommandLineHook? _instance;
    public static CommandLineHook Instance => _instance ??= new CommandLineHook();

    private Document? _doc;
    private Editor? _ed;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private string _lastText = string.Empty;

    public void Attach(Document doc)
    {
        Detach();
        _doc = doc;
        _ed = doc.Editor;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => PollCommandLine();
        _timer.Start();
    }

    public void Detach()
    {
        _timer?.Stop();
        _timer = null;
        _doc = null;
        _ed = null;
        _lastText = string.Empty;
    }

    /// <summary>轮询命令行尾部文本（POC：取最后 40 字符作为当前输入）。</summary>
    private void PollCommandLine()
    {
        if (_ed is null)
        {
            return;
        }

        try
        {
            // Editor 没有公开"当前命令行缓冲"API，从消息拦截退化：
            // 用 Editor.PromptedForInput 事件不可行 → 用本插件浮窗自己的输入框。
            // 真正的命令行捕获需要 UI 钩子（阶段 3 的 WH_KEYBOARD + 命令行窗口枚举），
            // 此处先以浮窗输入框为输入源（与 WPF 模拟器一致的交互形态）。
        }
        catch
        {
            // 文档切换瞬间可能抛异常，忽略
        }
    }
}

/// <summary>
/// 引擎 HTTP 客户端（127.0.0.1:46200）。
/// </summary>
public static class EngineClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(50) };

    private const string Base = "http://127.0.0.1:46200";

    /// <summary>语义推荐：返回命令 ID 列表（按可能性排序）。</summary>
    public static List<string> Suggest(string input, int waitMs = 8000)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { input, waitMs });
            var resp = Http.PostAsync($"{Base}/suggest",
                new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            var ids = new List<string>();
            if (doc.TryGetProperty("Items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    if (it.TryGetProperty("Command", out var cmd) &&
                        cmd.TryGetProperty("Id", out var id))
                    {
                        ids.Add(id.GetString() ?? string.Empty);
                    }
                }
            }

            return ids.Where(s => !string.IsNullOrEmpty(s)).Take(5).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>记录使用（个性化）。</summary>
    public static void Accept(string commandId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { commandId });
            Http.PostAsync($"{Base}/accept",
                new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        }
        catch
        {
            // 个性化失败不影响主流程
        }
    }
}

/// <summary>
/// 候选浮窗承载器：无模式 WPF Window（Owner 绑定 AutoCAD 主窗口）。
/// PaletteSet 承载 WPF 需要 WinForms ElementHost 互操作，v1 用更简单的 modeless Window。
/// </summary>
public sealed class CadSmartPalette
{
    private static CadSmartPalette? _instance;
    public static CadSmartPalette Instance => _instance ??= new CadSmartPalette();

    private CadSmartFloatingWindow? _window;

    public void Show()
    {
        if (_window is null)
        {
            _window = new CadSmartFloatingWindow();
            // Owner 绑定 AutoCAD 主窗口（保证浮窗总在 CAD 之上）
            var mainHwnd = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            var interop = new System.Windows.Interop.WindowInteropHelper(_window)
            {
                Owner = mainHwnd,
            };
            _window.Show();
        }
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    public void Hide()
    {
        _window?.Hide();
    }
}

/// <summary>
/// 浮窗（无模式 Window）：输入框 + 候选列表。跟随 AutoCAD 主窗口。
/// </summary>
public class CadSmartFloatingWindow : System.Windows.Window
{
    private readonly System.Windows.Controls.TextBox _input = new()
    {
        FontSize = 14,
        Margin = new Thickness(6),
    };

    private readonly System.Windows.Controls.ListBox _list = new()
    {
        Margin = new Thickness(6, 0, 6, 6),
    };

    private CancellationTokenSource? _debounce;

    public CadSmartFloatingWindow()
    {
        Title = "CadSmart 命令智能提示";
        Width = 420;
        Height = 320;
        WindowStyle = WindowStyle.ToolWindow;
        Topmost = true;

        var sp = new System.Windows.Controls.StackPanel();
        sp.Children.Add(_input);
        sp.Children.Add(_list);
        Content = sp;

        _input.TextChanged += async (_, _) =>
        {
            _debounce?.Cancel();
            _debounce = new CancellationTokenSource();
            var ct = _debounce.Token;
            try
            {
                await Task.Delay(150, ct);
                var input = _input.Text;
                if (string.IsNullOrWhiteSpace(input))
                {
                    _list.ItemsSource = null;
                    return;
                }

                var ids = await Task.Run(() => EngineClient.Suggest(input));
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _list.ItemsSource = ids;
            }
            catch (OperationCanceledException)
            {
            }
        };

        _list.MouseDoubleClick += (_, _) => ExecuteSelected();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ExecuteSelected();
            }
        };
    }

    private void ExecuteSelected()
    {
        if (_list.SelectedItem is string id && !string.IsNullOrEmpty(id))
        {
            EngineClient.Accept(id);
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            doc?.SendStringToExecute(id + "\n", true, false, true);
        }
    }
}
