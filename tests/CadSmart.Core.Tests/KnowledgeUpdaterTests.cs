using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 知识库在线更新器测试：哈希校验、原子替换、失败回退、重载。
/// </summary>
public class KnowledgeUpdaterTests : IDisposable
{
    private readonly string _dir;

    public KnowledgeUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cadsmart-kb-{Guid.NewGuid():N}");
        KnowledgeUpdater.KnowledgeDir = _dir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static (string Json, string Sha) MakeCommandsJson(int versionMarker)
    {
        var json = $$"""
            [
              {"id":"TEST{{versionMarker}}","zh":"测试命令{{versionMarker}}","pinyin":"ceshi{{versionMarker}}","initials":"cs","category":"测试","desc":"更新器验证 v{{versionMarker}}"}
            ]
            """;
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return (json, sha);
    }

    private static KnowledgeUpdater.Manifest NewManifest(string json, int version, string source)
        => new(version, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), source,
            DateTime.UtcNow);

    [Fact]
    public async Task 本地文件更新_哈希匹配_成功落盘()
    {
        var (json, sha) = MakeCommandsJson(1);
        var srcFile = Path.Combine(_dir, "src.json");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(srcFile, json);

        var result = await KnowledgeUpdater.UpdateFromAsync(srcFile, NewManifest(json, 1, srcFile));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.NewVersion);
        Assert.True(File.Exists(KnowledgeUpdater.CommandsPath));
        Assert.True(File.Exists(KnowledgeUpdater.ManifestPath));
        Assert.Equal(1, KnowledgeUpdater.ReadLocalManifest()!.Version);
    }

    [Fact]
    public async Task 哈希不匹配_拒绝更新()
    {
        var (json, _) = MakeCommandsJson(2);
        var srcFile = Path.Combine(_dir, "src2.json");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(srcFile, json + "extra"); // 内容被篡改

        var manifest = new KnowledgeUpdater.Manifest(2, "AAAA1111", srcFile, DateTime.UtcNow);
        var result = await KnowledgeUpdater.UpdateFromAsync(srcFile, manifest);

        Assert.False(result.Success);
        Assert.Contains("哈希不匹配", result.Message);
        Assert.False(File.Exists(KnowledgeUpdater.CommandsPath)); // 未落盘
    }

    [Fact]
    public async Task 非法JSON_拒绝更新()
    {
        var bad = "这不是JSON";
        var srcFile = Path.Combine(_dir, "src3.json");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(srcFile, bad);

        var result = await KnowledgeUpdater.UpdateFromAsync(srcFile, NewManifest(bad, 3, srcFile));

        Assert.False(result.Success);
        Assert.False(File.Exists(KnowledgeUpdater.CommandsPath));
    }

    [Fact]
    public async Task 二次更新_覆盖旧版本()
    {
        Directory.CreateDirectory(_dir);

        var (json1, _) = MakeCommandsJson(1);
        var f1 = Path.Combine(_dir, "v1.json");
        await File.WriteAllTextAsync(f1, json1);
        await KnowledgeUpdater.UpdateFromAsync(f1, NewManifest(json1, 1, f1));

        var (json2, _) = MakeCommandsJson(2);
        var f2 = Path.Combine(_dir, "v2.json");
        await File.WriteAllTextAsync(f2, json2);
        var r2 = await KnowledgeUpdater.UpdateFromAsync(f2, NewManifest(json2, 2, f2));

        Assert.True(r2.Success);
        Assert.Equal(2, KnowledgeUpdater.ReadLocalManifest()!.Version);

        // 重载后的仓储包含新命令
        Assert.True(KnowledgeUpdater.TryLoadRepository(out var repo));
        Assert.NotNull(repo.FindById("TEST2"));
        Assert.Null(repo.FindById("TEST1")); // 旧版内容已被原子替换
    }

    [Fact]
    public async Task 无本地清单_ReadLocalManifest返回null()
    {
        Assert.Null(KnowledgeUpdater.ReadLocalManifest());
        await Task.CompletedTask;
    }
}
