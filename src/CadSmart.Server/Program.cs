using CadSmart.Core.Abstractions;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Setup;
using CadSmart.Core.Suggestion;
using CadSmart.Data;
using CadSmart.Server;

// 引擎独立服务进程入口（阶段 2 重构 #1 + 路线A 引导 + 局域网推理服务器）。
// 运行后在本机提供 JSON API，供任何平台的插件薄壳（AutoCAD/ZWCAD/GstarCAD）HTTP 调用。
//
// 注意：被托管运行（stdout 管道可能断裂）时 Console.WriteLine 会抛 IOException
// 拖垮进程——所有控制台输出都走 SafeLog 保护，服务核心只依赖 HTTP。
Console.OutputEncoding = System.Text.Encoding.UTF8;

void SafeLog(string message)
{
    try
    {
        Console.WriteLine(message);
    }
    catch
    {
        // 管道断裂/控制台不可用：忽略，服务继续跑
    }
}

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 46200;

var usageFile = Path.Combine(AppContext.BaseDirectory, "usage.json");
var behavior = new JsonFileBehaviorStore(usageFile);

ICommandRepository RepoFactory()
{
    var asm = typeof(JsonCommandRepository).Assembly;
    var name = asm.GetManifestResourceNames()
        .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
    var stream = asm.GetManifestResourceStream(name)!;
    return JsonCommandRepository.LoadFromStream(stream, behavior);
}

// LLM 配置（环境变量可覆盖；未配置 = NullProvider 纯规则 + 引导）
//   云端: CADSMART_LLM_ENDPOINT / CADSMART_LLM_MODEL / CADSMART_LLM_APIKEY
//   局域网推理服务器: CADSMART_LLM_HOST（如 192.168.1.111，空 = 仅本机）+ CADSMART_LLM_PORTS（逗号分隔，空 = 常见端口）
var cloudEndpoint = Environment.GetEnvironmentVariable("CADSMART_LLM_ENDPOINT") ?? string.Empty;
var cloudModel = Environment.GetEnvironmentVariable("CADSMART_LLM_MODEL") ?? string.Empty;
var cloudKey = Environment.GetEnvironmentVariable("CADSMART_LLM_APIKEY") ?? string.Empty;
var lanHost = Environment.GetEnvironmentVariable("CADSMART_LLM_HOST") ?? string.Empty;
var lanPortsRaw = Environment.GetEnvironmentVariable("CADSMART_LLM_PORTS") ?? string.Empty;

ILlmProvider llm;
var cloudConfigured = false;
if (!string.IsNullOrWhiteSpace(cloudEndpoint) && !string.IsNullOrWhiteSpace(cloudModel))
{
    llm = new CloudLlmProvider(new HttpClient(), new LlmOptions
    {
        Endpoint = cloudEndpoint,
        Model = cloudModel,
        ApiKey = cloudKey,
        TimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("CADSMART_LLM_TIMEOUTMS"), out var ct2) ? ct2 : 60000,
    });
    cloudConfigured = true;
}
else
{
    var ports = string.IsNullOrWhiteSpace(lanPortsRaw)
        ? null
        : lanPortsRaw.Split(',').Select(s => int.TryParse(s.Trim(), out var n) ? n : 0).Where(n => n > 0).ToArray();
    var hosts = string.IsNullOrWhiteSpace(lanHost)
        ? null
        : new[] { "127.0.0.1", lanHost.Trim() };

    var local = new LocalLlmProvider(new HttpClient(), new LlmOptions
    {
        Model = cloudModel is { Length: > 0 } ? cloudModel : "qwen",
        TimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("CADSMART_LLM_TIMEOUTMS"), out var ct1) ? ct1 : 120000,
    }, ports, hosts);
    var probe = await local.ProbeAsync();
    llm = probe ? local : new NullLlmProvider();
    if (probe && local is { OpenHost: not "127.0.0.1" })
    {
        SafeLog($"已接入局域网推理服务器: {local.OpenHost}:{local.OpenPort}");
    }
}

var availability = LlmAvailabilityChecker.Check(llm, cloudConfigured);
SafeLog($"智能模式: {availability.Mode} — {availability.UserMessage}");
if (availability.Mode == "none")
{
    SafeLog("提示: 设置 CADSMART_LLM_ENDPOINT/MODEL/APIKEY 启用云端，或安装本地运行时（首选 LocModel https://locmodel.com/）");
}

var engine = new InProcessSuggestionEngine(
    new SuggestionService(RepoFactory(), behavior, new MatchEngine(), llm)
    {
        // 本地 CPU 模型（Qwen3.5-4B）真实推理 10~20 秒；云端通常 1~3 秒。
        // 可用环境变量 CADSMART_LLM_WAITMS 覆盖；UI 可按"首字延迟 vs 语义准确"权衡调整。
        LlmWaitMs = int.TryParse(Environment.GetEnvironmentVariable("CADSMART_LLM_WAITMS"), out var wm) ? wm : 20000,
    });

// --once 模式：处理单条输入后输出结果并退出（CI/沙箱验证专用，规避长驻进程管理）。
//   用法: CadSmart.Server.exe --once --input "删除全部窗户" [--port 47600]
var onceIdx = Array.IndexOf(args, "--once");
if (onceIdx >= 0)
{
    var inputIdx = Array.IndexOf(args, "--input");
    var onceInput = inputIdx >= 0 && inputIdx + 1 < args.Length ? args[inputIdx + 1] : "画个圆";
    var onceResult = await engine.SuggestAsync(onceInput);
    SafeLog($"input: {onceInput}");
    SafeLog($"llmUsed: {onceResult.LlmUsed} degraded: {onceResult.LlmDegraded} ruleMs: {onceResult.RuleLatencyMs} totalMs: {onceResult.TotalLatencyMs}");
    foreach (var item in onceResult.Items.Take(5))
    {
        SafeLog($"  {item.Command.Id,-14} {item.Command.ChineseName,-8} score={item.Score} via={item.MatchedVia} src={item.Source}");
    }

    behavior.Save();
    return 0;
}

var server = new MiniHttpServer(engine, port, availability);
server.Start();

SafeLog($"CadSmart 引擎服务已启动: http://127.0.0.1:{port}/");
SafeLog("  POST /suggest {\"input\":\"...\"}   POST /accept {\"commandId\":\"...\"}   GET /health   GET /llm/status");
SafeLog("按 Ctrl+C 或回车退出。");

// 无控制台/被托管运行（stdin 关闭）时 ReadLine 立即返回 null 导致服务退出——改为无限等待，
// 退出只走 Ctrl+C（Console.CancelKeyPress）或进程被杀。
// 用 Task.Delay(Timeout.Infinite) + finally 保存行为数据。
AppDomain.CurrentDomain.ProcessExit += (_, _) => behavior.Save();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    behavior.Save();
    server.Dispose();
    Environment.Exit(0);
};

await Task.Delay(Timeout.Infinite);
return 0;
