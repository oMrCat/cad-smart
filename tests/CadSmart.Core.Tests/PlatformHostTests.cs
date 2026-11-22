using Xunit;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Abstractions.Platform;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Core.Suggestion.Platform;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 平台适配 SDK（阶段3重构#2）接线测试：SuggestionHost 驱动模拟平台完成完整闭环。
/// </summary>
public class PlatformHostTests
{
    /// <summary>内存模拟平台（代替 CAD 进程）。</summary>
    private sealed class FakePlatform : IPlatformAdapter
    {
        public readonly FakeSink Sink = new();
        public readonly FakeUI Ui = new();

        public string PlatformName => "Fake";
        public ICommandSink CommandSink => Sink;
        public ISuggestionUI SuggestionUI => Ui;
        public bool IsAvailable { get; private set; }
        public bool Initialize() { IsAvailable = true; return true; }
        public void Shutdown() => IsAvailable = false;
    }

    private sealed class FakeSink : ICommandSink
    {
        public string Current = string.Empty;
        public List<string> Executed = new();

        public string GetCurrentInput() => Current;

        public void ExecuteCommand(string commandId) => Executed.Add(commandId);

        public void SimulateInput(string text) => Current = text;

        public void FireInputChanged() => InputChanged?.Invoke(this, Current);

        public void FireCommit() => InputCommitted?.Invoke(this, EventArgs.Empty);

        public event EventHandler<string>? InputChanged;

        public event EventHandler? InputCommitted;
    }

    private sealed class FakeUI : ISuggestionUI
    {
        public IReadOnlyList<CommandCandidate>? Shown;
        public int HideCount;

        public void ShowSuggestions(IReadOnlyList<CommandCandidate> items) => Shown = items;

        public void Hide() => HideCount++;

        public void FireAccept(int index) => SuggestionAccepted?.Invoke(this, index);

        public event EventHandler<int>? SuggestionAccepted;
    }

    private static (SuggestionHost Host, FakePlatform Platform, InProcessSuggestionEngine Engine, JsonFileBehaviorStore Behavior) NewHost()
    {
        var usageFile = Path.Combine(Path.GetTempPath(), $"cadsmart-host-{Guid.NewGuid():N}.json");
        var behavior = new JsonFileBehaviorStore(usageFile);
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream, behavior);
        var engine = new InProcessSuggestionEngine(
            new SuggestionService(repo, behavior, new MatchEngine(), new NullLlmProvider()));
        var platform = new FakePlatform();
        var host = new SuggestionHost(platform, engine, debounceMs: 30);
        return (host, platform, engine, behavior);
    }

    [Fact]
    public async Task 接线闭环_输入变化到展示到选中到执行()
    {
        var (host, platform, _, _) = NewHost();
        host.Start();

        platform.Sink.SimulateInput("画个圆");
        platform.Sink.FireInputChanged();

        // 等防抖 + 建议
        for (var i = 0; i < 30 && platform.Ui.Shown is null; i++)
        {
            await Task.Delay(30);
        }

        Assert.NotNull(platform.Ui.Shown);
        Assert.Equal("CIRCLE", platform.Ui.Shown![0].Command.Id);

        // 用户按 1 选中 → 引擎 Accept + 平台执行
        platform.Ui.FireAccept(1);
        Assert.Contains("CIRCLE", platform.Sink.Executed);

        host.Dispose();
    }

    [Fact]
    public async Task 防抖_快速连续输入只出最后结果()
    {
        var (host, platform, _, _) = NewHost();
        host.Start();

        foreach (var ch in new[] { "z", "zh", "zhi", "zhix" })
        {
            platform.Sink.SimulateInput(ch);
            platform.Sink.FireInputChanged();
            await Task.Delay(5); // 远小于防抖窗口
        }

        await Task.Delay(200);
        Assert.NotNull(platform.Ui.Shown);
        Assert.Equal("LINE", platform.Ui.Shown![0].Command.Id); // zhix → 直线前缀

        host.Dispose();
    }

    [Fact]
    public async Task 空输入_隐藏候选框()
    {
        var (host, platform, _, _) = NewHost();
        host.Start();

        platform.Sink.SimulateInput("yuan");
        platform.Sink.FireInputChanged();
        await Task.Delay(120);
        Assert.NotNull(platform.Ui.Shown);

        platform.Sink.SimulateInput("");
        platform.Sink.FireInputChanged();
        await Task.Delay(80);
        Assert.True(platform.Ui.HideCount >= 1);

        host.Dispose();
    }

    [Fact]
    public async Task 提交输入_隐藏候选框()
    {
        var (host, platform, _, _) = NewHost();
        host.Start();

        platform.Sink.SimulateInput("yuan");
        platform.Sink.FireInputChanged();
        await Task.Delay(120);
        Assert.NotNull(platform.Ui.Shown);

        // 用户直接回车提交 → 候选框隐藏（平台原生行为不拦截）
        platform.Sink.FireCommit();
        await Task.Delay(30);
        Assert.True(platform.Ui.HideCount >= 1);

        host.Dispose();
    }

    [Fact]
    public async Task 选中执行后_行为计数增加()
    {
        var usageFile = Path.Combine(Path.GetTempPath(), $"cadsmart-host-{Guid.NewGuid():N}.json");
        var behavior = new JsonFileBehaviorStore(usageFile);
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        var repo = JsonCommandRepository.LoadFromStream(stream, behavior);
        var engine = new InProcessSuggestionEngine(
            new SuggestionService(repo, behavior, new MatchEngine(), new NullLlmProvider()));
        var platform = new FakePlatform();
        var host = new SuggestionHost(platform, engine, debounceMs: 30);
        host.Start();

        platform.Sink.SimulateInput("y");
        platform.Sink.FireInputChanged();
        for (var i = 0; i < 30 && platform.Ui.Shown is null; i++)
        {
            await Task.Delay(30);
        }

        platform.Ui.FireAccept(1);
        for (var i = 0; i < 10 && behavior.GetUsageCounts().GetValueOrDefault("CIRCLE") < 1; i++)
        {
            await Task.Delay(30);
        }

        Assert.Equal(1, behavior.GetUsageCounts().GetValueOrDefault("CIRCLE"));
        host.Dispose();
    }
}
