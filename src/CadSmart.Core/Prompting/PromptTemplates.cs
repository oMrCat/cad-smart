namespace CadSmart.Core.Prompting;

using System.Text;
using System.Text.Json;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;

/// <summary>
/// 语义推荐提示词模板库。
/// 设计原则：候选表注入 + 严格 JSON 输出约束 + 禁止编造命令。
/// </summary>
public static class PromptTemplates
{
    /// <summary>
    /// 构建系统提示词：注入候选命令表（限定 LLM 只能从中选择）。
    /// </summary>
    public static string BuildSystemPrompt(IReadOnlyList<CadCommand> scope, int maxRecommendations = 5)
    {
        var list = new List<object>(scope.Count);
        foreach (var c in scope)
        {
            list.Add(new
            {
                id = c.Id,
                zh = c.ChineseName,
                desc = c.Description,
                cat = c.Category,
            });
        }

        var json = JsonSerializer.Serialize(list, JsonOpts);

        var sb = new StringBuilder();
        sb.AppendLine("你是 CAD 命令推荐专家。用户会在 CAD 命令行中输入以下内容（可能是中文口语、拼音、缩写或错别字），你的任务是从候选命令表中选出最匹配的命令。");
        sb.AppendLine();
        sb.AppendLine("候选命令表（JSON 数组，字段 id=命令名, zh=中文名, desc=功能, cat=分类）：");
        sb.AppendLine(json);
        sb.AppendLine();
        sb.AppendLine("硬性规则：");
        sb.AppendLine($"1. 只能推荐候选命令表中存在的 id，禁止编造任何命令。");
        sb.AppendLine($"2. 最多输出 {maxRecommendations} 条，按可能性从高到低排序。");
        sb.AppendLine("3. 输出必须是严格 JSON，格式：{\"recommendations\":[{\"command_id\":\"LINE\",\"confidence\":0.92}]}，confidence 取值 0~1。");
        sb.AppendLine("4. 不要输出任何 JSON 以外的文字、解释或代码块标记。");
        return sb.ToString();
    }

    /// <summary>构建用户消息（即用户在命令行键入的内容）。</summary>
    public static string BuildUserPrompt(string userInput) => $"命令行输入：{userInput}";

    /// <summary>解析 LLM 返回的严格 JSON。</summary>
    public static IReadOnlyList<LlmRecommendation>? ParseRecommendations(string? llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            return null;
        }

        try
        {
            var text = llmOutput.Trim();
            // 容错：剥离可能的 ```json 包裹
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineEnd = text.IndexOf('\n');
                if (firstLineEnd >= 0)
                {
                    text = text[(firstLineEnd + 1)..].Trim();
                }

                if (text.EndsWith("```", StringComparison.Ordinal))
                {
                    text = text[..^3].Trim();
                }
            }

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("recommendations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<LlmRecommendation>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("command_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                double conf = 0.5;
                if (item.TryGetProperty("confidence", out var confEl))
                {
                    if (confEl.ValueKind == JsonValueKind.Number && confEl.TryGetDouble(out var d))
                    {
                        conf = Math.Clamp(d, 0, 1);
                    }
                }

                result.Add(new LlmRecommendation(id.Trim().ToUpperInvariant(), conf));
            }

            return result.Count == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
