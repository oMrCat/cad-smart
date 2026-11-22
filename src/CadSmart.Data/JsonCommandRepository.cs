namespace CadSmart.Data;

using System.Text.Json;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;

/// <summary>
/// JSON 命令知识库仓储。支持内嵌资源或外部文件加载。
/// 使用计数增量写入外部 usage 文件，Save() 时落盘。
/// </summary>
public sealed class JsonCommandRepository : ICommandRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, CadCommand> _commands;
    private readonly IBehaviorStore _behavior;

    public JsonCommandRepository(IEnumerable<CadCommand> commands, IBehaviorStore? behavior = null)
    {
        _behavior = behavior ?? new InMemoryBehaviorStore();
        _commands = new Dictionary<string, CadCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commands)
        {
            if (_commands.ContainsKey(c.Id))
            {
                continue; // 重复 Id 跳过，保证唯一
            }

            _commands[c.Id] = c;
        }
    }

    public static JsonCommandRepository LoadFromStream(Stream stream, IBehaviorStore? behavior = null)
    {
        var list = JsonSerializer.Deserialize<List<CadCommandDto>>(stream, JsonOpts)
            ?? throw new InvalidDataException("命令知识库 JSON 为空或格式错误。");
        return new JsonCommandRepository(list.Select(ToDomain), behavior);
    }

    public static JsonCommandRepository LoadFromEmbeddedResource(IReadOnlyList<string> aliasesIgnore = default!)
        => LoadFromStream(OpenEmbedded());

    private static Stream OpenEmbedded()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到内嵌 commands.json。");
        return asm.GetManifestResourceStream(name)!;
    }

    public IReadOnlyList<CadCommand> GetAll() => _commands.Values.ToList();

    public CadCommand? FindById(string id)
        => _commands.TryGetValue(id, out var c) ? c : null;

    public void IncrementUsage(string commandId)
    {
        if (_commands.TryGetValue(commandId, out var c))
        {
            c.UsageCount++;
        }

        _behavior.Increment(commandId);
    }

    public void Save()
    {
        _behavior.Save();
    }

    private static CadCommand ToDomain(CadCommandDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name ?? dto.Id,
        ChineseName = dto.Zh ?? string.Empty,
        Aliases = dto.Aliases ?? Array.Empty<string>(),
        Pinyin = dto.Pinyin ?? string.Empty,
        PinyinInitials = dto.Initials ?? string.Empty,
        Category = dto.Category ?? "未分类",
        Description = dto.Desc ?? string.Empty,
        ParamsHint = dto.Params ?? string.Empty,
        Keywords = dto.Keywords ?? Array.Empty<string>(),
    };

    private sealed class CadCommandDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Zh { get; set; }
        public string[]? Aliases { get; set; }
        public string? Pinyin { get; set; }
        public string? Initials { get; set; }
        public string? Category { get; set; }
        public string? Desc { get; set; }
        public string? Params { get; set; }
        public string[]? Keywords { get; set; }
    }
}

/// <summary>内存行为存储（无持久化，测试用）。</summary>
public sealed class InMemoryBehaviorStore : IBehaviorStore
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, int> GetUsageCounts()
    {
        lock (_lock)
        {
            return new Dictionary<string, int>(_counts, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Increment(string commandId)
    {
        lock (_lock)
        {
            _counts[commandId] = (_counts.TryGetValue(commandId, out var n) ? n : 0) + 1;
        }
    }

    public void Save()
    {
        // 内存版无操作
    }
}
