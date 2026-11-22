using CadSmart.Core.Abstractions;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Setup;
using CadSmart.Core.Suggestion;
using CadSmart.Data;
using CadSmart.Server;

// 引擎独立服务进程入口（阶段 2 重构 #1 + 路线A 引导）。
// 运行后在本机提供 JSON API，供任何平台的插件薄壳（AutoCAD/ZWCAD/GstarCAD）HTTP 调用。
Console.OutputEncoding = System.Text.Encoding.UTF8;

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

    var local = new LocalLlmProvider(new HttpClient(), new LlmOptions { Model = cloudModel is { Length: > 0 } ? cloudModel : "qwen" }, ports, hosts);
    var probe = await local.ProbeAsync();
    llm = probe ? local : new NullLlmProvider();
    if (probe && local is { OpenHost: not "127.0.0.1" })
    {
        Console.WriteLine($"已接入局域网推理服务器: {local.OpenHost}:{local.OpenPort}");
    }
}

var availability = LlmAvailabilityChecker.Check(llm, cloudConfigured);
Console.WriteLine($"智能模式: {availability.Mode} — {availability.UserMessage}");
if (availability.Mode == "none")
{
    Console.WriteLine("提示: 设置 CADSMART_LLM_ENDPOINT/MODEL/APIKEY 启用云端，或安装本地运行时（首选 LocModel https://locmodel.com/）");
}

var engine = new InProcessSuggestionEngine(
    new SuggestionService(RepoFactory(), behavior, new MatchEngine(), llm));

var server = new MiniHttpServer(engine, port, availability);
server.Start();

Console.WriteLine($"CadSmart 引擎服务已启动: http://127.0.0.1:{port}/");
Console.WriteLine("  POST /suggest {\"input\":\"...\"}   POST /accept {\"commandId\":\"...\"}   GET /health   GET /llm/status");
Console.WriteLine("按 Ctrl+C 或回车退出。");

Console.ReadLine();
behavior.Save();
server.Dispose();
