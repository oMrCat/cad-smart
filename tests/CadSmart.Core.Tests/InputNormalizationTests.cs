using Xunit;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Data;

namespace CadSmart.Tests;

/// <summary>
/// 中文输入容错测试：全角/半角归一化、拼音方言变体（zh/z、ch/c、sh/s、ng/n）、空格输入。
/// </summary>
public class InputNormalizationTests
{
    private static IReadOnlyList<CadCommand> Repo()
    {
        var asm = typeof(JsonCommandRepository).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("commands.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonCommandRepository.LoadFromStream(stream).GetAll();
    }

    private readonly MatchEngine _engine = new();
    private readonly IReadOnlyList<CadCommand> _commands = Repo();

    private string Top1(string input)
    {
        var items = _engine.Rank(input, _commands);
        return items.Count == 0 ? string.Empty : items[0].Command.Id;
    }

    // ---------- 全角归一化 ----------

    [Theory]
    [InlineData("ＬＩＮＥ", "LINE")]
    [InlineData("ｙｕａｎ", "CIRCLE")]
    [InlineData("Ｌ", "LINE")]
    public void 全角英文_归一化后命中(string input, string expected)
        => Assert.Equal(expected, Top1(input));

    [Fact]
    public void 全角字母与半角结果一致()
    {
        var half = Top1("line");
        var full = Top1("ｌｉｎｅ");
        Assert.Equal(half, full);
    }

    // ---------- 拼音方言变体 ----------

    [Theory]
    [InlineData("zixian", "LINE")]         // zhixian → zixian（zh→z）
    [InlineData("zuantianchong", "HATCH")] // zhuantianchong → zuan（zh→z）
    [InlineData("sanchu", "ERASE")]        // shanchu → sanchu（sh→s）
    [InlineData("xuanzhuan", "ROTATE")]    // 正常对照
    public void 拼音变体_方言快打_命中(string input, string expected)
        => Assert.Equal(expected, Top1(input));

    [Fact]
    public void 首字母缩写带空格_紧凑后命中()
        => Assert.Equal("LINE", Top1("z x"));

    // ---------- 变体生成器 ----------

    [Fact]
    public void 变体生成_含原输入且封顶8个()
    {
        var v = MatchEngine.BuildPinyinVariants("zhangshang");
        Assert.Equal("zhangshang", v[0]);
        Assert.True(v.Count <= 8);
        Assert.Contains("zangshang", v);   // zh→z
        Assert.Contains("zhanshang", v);   // ng→n
        Assert.Contains("zhangsang", v);   // sh→s
        Assert.DoesNotContain(v, x => x.Contains("hh")); // 无 zhh 废变体
    }

    [Fact]
    public void 变体生成_中文输入不产生变体()
    {
        var v = MatchEngine.BuildPinyinVariants("画圆");
        Assert.Single(v);
    }

    // ---------- 回归对照：原匹配不受影响 ----------

    [Theory]
    [InlineData("line", "LINE")]
    [InlineData("zhixian", "LINE")]
    [InlineData("画个圆", "CIRCLE")]
    public void 回归_原有用例不退化(string input, string expected)
        => Assert.Equal(expected, Top1(input));
}
