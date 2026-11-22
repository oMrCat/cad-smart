namespace CadSmart.Data;

using System.Text.Json;
using CadSmart.Core.Abstractions;

/// <summary>
/// JSON 文件行为存储（使用计数持久化，文件按需创建）。
/// </summary>
public sealed class JsonFileBehaviorStore : IBehaviorStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Dictionary<string, int> _counts;
    private readonly object _lock = new();

    public JsonFileBehaviorStore(string filePath)
    {
        _filePath = filePath;
        _counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (loaded is not null)
                {
                    foreach (var kv in loaded)
                    {
                        _counts[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception)
            {
                // 行为文件损坏不致命：忽略，从零开始
            }
        }
    }

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
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_counts, JsonOpts);
        }

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_filePath, json);
        }
        catch (Exception)
        {
            // 写盘失败不致命：下次 Save 重试
        }
    }
}
