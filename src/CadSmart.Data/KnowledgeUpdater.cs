namespace CadSmart.Data;

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CadSmart.Core.Abstractions;

/// <summary>
/// 知识库在线更新器（阶段 3）：
/// 版本清单（manifest.json：版本号 + commands.json 的 SHA256 + 下载地址）→
/// 下载 → 校验哈希 → 原子替换（写临时文件再 File.Move）→ 重载仓储。
/// 源支持本地路径或 HTTP(S) URL，失败保持旧库不变。
/// </summary>
public static class KnowledgeUpdater
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Manifest(int Version, string Sha256, string Source, DateTime PublishedAt);

    public sealed record UpdateResult(bool Success, string Message, int? NewVersion = null);

    /// <summary>当前知识库目录（含 commands.json 与 manifest.json）。</summary>
    private readonly static string DefaultDir = Path.Combine(
        AppContext.BaseDirectory, "knowledge");

    public static string KnowledgeDir { get; set; } = DefaultDir;

    public static string ManifestPath => Path.Combine(KnowledgeDir, "manifest.json");

    public static string CommandsPath => Path.Combine(KnowledgeDir, "commands.json");

    /// <summary>读取本地当前版本（无清单返回 null）。</summary>
    public static Manifest? ReadLocalManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOpts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从源（本地路径或 URL）拉取新版 commands.json 与 manifest 并安装。
    /// expectedRemoteManifest 用于校验远端清单（调用方可先自行拉取远端清单比较版本）。
    /// </summary>
    public static async Task<UpdateResult> UpdateFromAsync(
        string commandsSource,
        Manifest remoteManifest,
        CancellationToken ct = default)
    {
        try
        {
            // 1. 拉取内容
            var bytes = await FetchAsync(commandsSource, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return new UpdateResult(false, "下载内容为空或不可达");
            }

            // 2. SHA256 校验
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, remoteManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateResult(false, $"哈希不匹配：期望 {remoteManifest.Sha256} 实际 {hash}");
            }

            // 3. JSON 合法性预检（能反序列化才允许替换）
            try
            {
                using var stream = new MemoryStream(bytes);
                var probe = await JsonSerializer.DeserializeAsync<List<JsonElement>>(stream, cancellationToken: ct).ConfigureAwait(false);
                if (probe is null || probe.Count == 0)
                {
                    return new UpdateResult(false, "JSON 内容不是非空数组");
                }
            }
            catch (JsonException ex)
            {
                return new UpdateResult(false, $"JSON 校验失败：{ex.Message}");
            }

            // 4. 原子替换：临时文件 → File.Move 覆盖
            Directory.CreateDirectory(KnowledgeDir);
            var tmpCommands = CommandsPath + ".tmp";
            var tmpManifest = ManifestPath + ".tmp";
            await File.WriteAllBytesAsync(tmpCommands, bytes, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                tmpManifest, JsonSerializer.Serialize(remoteManifest, JsonOpts), ct).ConfigureAwait(false);

            File.Move(tmpCommands, CommandsPath, overwrite: true);
            File.Move(tmpManifest, ManifestPath, overwrite: true);

            return new UpdateResult(true, $"已更新到 v{remoteManifest.Version}", remoteManifest.Version);
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult(false, "更新被取消");
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, $"更新失败：{ex.Message}");
        }
    }

    /// <summary>从更新后的本地知识库重载仓储（验证可用才返回，失败保持调用方原仓储）。</summary>
    public static bool TryLoadRepository(out JsonCommandRepository repo, IBehaviorStore? behavior = null)
    {
        try
        {
            using var stream = File.OpenRead(CommandsPath);
            repo = JsonCommandRepository.LoadFromStream(stream, behavior);
            return repo.GetAll().Count > 0;
        }
        catch (Exception)
        {
            repo = null!;
            return false;
        }
    }

    private static async Task<byte[]?> FetchAsync(string source, CancellationToken ct)
    {
        if (File.Exists(source))
        {
            return await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var http = new HttpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await http.GetAsync(uri, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }

        return null;
    }
}
