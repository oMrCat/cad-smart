using CadSmart.Core.Abstractions;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;
using CadSmart.Core.Suggestion;
using CadSmart.Data;
using CadSmart.Server;

// 引擎独立服务进程入口（阶段 2 重构 #1）。
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

var engine = new InProcessSuggestionEngine(
    new SuggestionService(RepoFactory(), behavior, new MatchEngine(), new NullLlmProvider()));

var server = new MiniHttpServer(engine, port);
server.Start();

Console.WriteLine($"CadSmart 引擎服务已启动: http://127.0.0.1:{port}/");
Console.WriteLine("  POST /suggest {\"input\":\"...\"}   POST /accept {\"commandId\":\"...\"}   GET /health");
Console.WriteLine("按 Ctrl+C 或回车退出。");

Console.ReadLine();
repo_Save();

void repo_Save()
{
    behavior.Save();
    server.Dispose();
}
