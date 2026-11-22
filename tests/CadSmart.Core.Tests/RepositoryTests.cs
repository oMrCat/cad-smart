using Xunit;
using CadSmart.Core.Domain;
using CadSmart.Data;

namespace CadSmart.Tests;

public class RepositoryTests
{
    private static JsonCommandRepository Load()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonCommandRepository.LoadFromStream(stream);
    }

    [Fact]
    public void 知识库加载_不少于90条命令()
    {
        var repo = Load();
        Assert.True(repo.GetAll().Count >= 90, $"实际 {repo.GetAll().Count} 条");
    }

    [Fact]
    public void 命令Id唯一()
    {
        var repo = Load();
        var ids = repo.GetAll().Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 必填字段齐全()
    {
        var problems = new List<string>();
        foreach (var c in Load().GetAll())
        {
            if (string.IsNullOrWhiteSpace(c.Id)) problems.Add($"{c.Id}: id");
            if (string.IsNullOrWhiteSpace(c.ChineseName)) problems.Add($"{c.Id}: zh");
            if (string.IsNullOrWhiteSpace(c.Category)) problems.Add($"{c.Id}: category");
            if (string.IsNullOrWhiteSpace(c.Description)) problems.Add($"{c.Id}: desc");
            if (string.IsNullOrWhiteSpace(c.Pinyin)) problems.Add($"{c.Id}: pinyin");
            if (string.IsNullOrWhiteSpace(c.PinyinInitials)) problems.Add($"{c.Id}: initials");
        }

        Assert.True(problems.Count == 0, "字段缺失:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void 拼音字段无空格且小写()
    {
        foreach (var c in Load().GetAll())
        {
            Assert.DoesNotContain(' ', c.Pinyin);
            Assert.DoesNotContain(' ', c.PinyinInitials);
            Assert.Equal(c.Pinyin.ToLowerInvariant(), c.Pinyin);
            Assert.Equal(c.PinyinInitials.ToLowerInvariant(), c.PinyinInitials);
        }
    }

    [Fact]
    public void 使用计数_增量生效()
    {
        var repo = Load();
        var before = repo.FindById("LINE")!.UsageCount;
        repo.IncrementUsage("LINE");
        Assert.Equal(before + 1, repo.FindById("LINE")!.UsageCount);
    }
}
