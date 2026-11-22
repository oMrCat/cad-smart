using Xunit;
using CadSmart.Core.Prompting;

namespace CadSmart.Tests;

public class PromptTemplateTests
{
    [Fact]
    public void 解析_严格JSON输出()
    {
        var json = """{"recommendations":[{"command_id":"LINE","confidence":0.92},{"command_id":"CIRCLE","confidence":0.5}]}""";
        var recs = PromptTemplates.ParseRecommendations(json);
        Assert.NotNull(recs);
        Assert.Equal(2, recs!.Count);
        Assert.Equal("LINE", recs[0].CommandId);
        Assert.Equal(0.92, recs[0].Confidence, 3);
    }

    [Fact]
    public void 解析_带代码块包裹的JSON()
    {
        var json = "```json\n{\"recommendations\":[{\"command_id\":\"ARC\",\"confidence\":0.8}]}\n```";
        var recs = PromptTemplates.ParseRecommendations(json);
        Assert.NotNull(recs);
        Assert.Equal("ARC", recs![0].CommandId);
    }

    [Fact]
    public void 解析_非法JSON返回null()
    {
        Assert.Null(PromptTemplates.ParseRecommendations("这不是JSON"));
        Assert.Null(PromptTemplates.ParseRecommendations(null));
        Assert.Null(PromptTemplates.ParseRecommendations(""));
    }

    [Fact]
    public void 解析_confidence越界_被钳制()
    {
        var json = """{"recommendations":[{"command_id":"LINE","confidence":9.9}]}""";
        var recs = PromptTemplates.ParseRecommendations(json)!;
        Assert.Equal(1.0, recs[0].Confidence, 3);
    }

    [Fact]
    public void 系统提示词_包含候选表与禁编造规则()
    {
        var cmd = new CadSmart.Core.Domain.CadCommand
        {
            Id = "LINE", Name = "LINE", ChineseName = "直线", Category = "绘图",
            Description = "创建直线段",
        };
        var prompt = PromptTemplates.BuildSystemPrompt([cmd]);
        Assert.Contains("LINE", prompt);
        Assert.Contains("直线", prompt);
        Assert.Contains("禁止编造", prompt);
        Assert.Contains("recommendations", prompt);
    }
}
