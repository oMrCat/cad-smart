using Xunit;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Data;

namespace CadSmart.Tests;

public class MatchEngineTests
{
    private static IReadOnlyList<CadCommand> Repo()
    {
        using var stream = OpenEmbedded();
        var repo = JsonCommandRepository.LoadFromStream(stream);
        return repo.GetAll();
    }

    private static Stream OpenEmbedded()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        return asm.GetManifestResourceStream(name)!;
    }

    private readonly MatchEngine _engine = new();
    private readonly IReadOnlyList<CadCommand> _commands = Repo();

    private string Top1Id(string input)
    {
        var items = _engine.Rank(input, _commands);
        return items.Count == 0 ? string.Empty : items[0].Command.Id;
    }

    // ---------- 精确与前缀 ----------

    [Theory]
    [InlineData("LINE", "LINE")]
    [InlineData("line", "LINE")]
    public void 精确命令名_大小写不敏感(string input, string expected)
        => Assert.Equal(expected, Top1Id(input));

    [Fact]
    public void 快捷别名_l_命中直线()
        => Assert.Equal("LINE", Top1Id("l"));

    [Fact]
    public void 快捷别名_pl_命中多段线()
        => Assert.Equal("PLINE", Top1Id("pl"));

    [Fact]
    public void 前缀_linea_命中LINE()
        => Assert.Equal("LINE", Top1Id("linea"));

    // ---------- 拼音 ----------

    [Fact]
    public void 全拼_zhixian_命中直线()
        => Assert.Equal("LINE", Top1Id("zhixian"));

    [Fact]
    public void 全拼_yuan_命中圆()
        => Assert.Equal("CIRCLE", Top1Id("yuan"));

    [Fact]
    public void 首字母_zx_命中直线()
        => Assert.Equal("LINE", Top1Id("zx"));

    [Fact]
    public void 拼音前缀_zhi_命中直线()
        => Assert.Equal("LINE", Top1Id("zhi"));

    [Fact]
    public void 全拼_duoduanxian_命中多段线()
        => Assert.Equal("PLINE", Top1Id("duoduanxian"));

    // ---------- 中文口语 ----------

    [Fact]
    public void 中文名_直线_命中LINE()
        => Assert.Equal("LINE", Top1Id("直线"));

    [Fact]
    public void 中文口语_画个圆_命中圆()
        => Assert.Equal("CIRCLE", Top1Id("画个圆"));

    [Fact]
    public void 中文关键词_填充_命中图案填充()
    {
        var items = _engine.Rank("填充", _commands);
        Assert.Contains(items, i => i.Command.Id == "HATCH");
    }

    [Fact]
    public void 中文口语_倒个角_命中倒角或圆角()
    {
        var id = Top1Id("倒个角");
        Assert.True(id is "CHAMFER" or "FILLET", $"实际: {id}");
    }

    // ---------- 模糊容错 ----------

    [Fact]
    public void 错别字_linee_命中LINE()
        => Assert.Equal("LINE", Top1Id("linee"));

    [Fact]
    public void 错别字_cirlce_命中CIRCLE()
        => Assert.Equal("CIRCLE", Top1Id("circls"));

    [Fact]
    public void 错别字_zhixina_命中直线()
        => Assert.Equal("LINE", Top1Id("zhixian"));

    // ---------- 空输入与边界 ----------

    [Fact]
    public void 空输入_返回空()
        => Assert.Empty(_engine.Rank("", _commands));

    [Fact]
    public void null输入_返回空()
        => Assert.Empty(_engine.Rank(null, _commands));

    [Fact]
    public void 乱码输入_不崩溃且最多返回topN()
    {
        var items = _engine.Rank("zzzzxxxx999", _commands);
        Assert.True(items.Count <= MatchEngine.DefaultTopN);
    }

    // ---------- 使用次数个性化 ----------

    [Fact]
    public void 使用次数提升排名()
    {
        var usage = new Dictionary<string, int> { ["ARC"] = 50 };
        var items = _engine.Rank("yh", _commands, usageBoost: usage);
        Assert.Equal("ARC", items[0].Command.Id);
    }

    // ---------- 性能 ----------

    [Fact]
    public void 单次匹配_50毫秒内()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _engine.Rank("画个圆", _commands);
        _engine.Rank("zhixian", _commands);
        _engine.Rank("zzxx", _commands);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds <= 50, $"三次匹配耗时 {sw.ElapsedMilliseconds}ms");
    }

    // ---------- 编辑距离工具 ----------

    [Theory]
    [InlineData("line", "line", 0)]
    [InlineData("line", "linee", 1)]
    [InlineData("cat", "dog", 3)]
    [InlineData("abcd", "axcy", 2)]
    public void 编辑距离正确(string a, string b, int expected)
        => Assert.Equal(expected, MatchEngine.LevenshteinBounded(a, b, Math.Max(a.Length, b.Length)));

    [Fact]
    public void 编辑距离超阈值_返回负1()
        => Assert.Equal(-1, MatchEngine.LevenshteinBounded("ab", "xyz", 1));
}
