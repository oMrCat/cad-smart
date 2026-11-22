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
    /// <summary>
    /// 构建系统提示词：双节结构——(1) 全库轻量目录（id+zh，保证语义召回不漏），
    /// (2) 粗筛重点候选的完整信息（desc/params，供精确挑选）。
    /// scope = 规则粗筛结果（有完整信息）；catalog = 全库轻量目录。
    /// </summary>
    public static string BuildSystemPrompt(IReadOnlyList<CadCommand> scope, int maxRecommendations = 5, IReadOnlyList<CadCommand>? catalog = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 CAD 命令推荐专家。用户会在 CAD 命令行中输入以下内容（可能是中文口语、拼音、缩写或错别字），你的任务是从候选命令表中选出最匹配的命令。");
        sb.AppendLine();

        if (catalog is { Count: > 0 })
        {
            // 压缩格式（id=zh 空格分隔）：~750 tokens vs JSON 数组 ~3000 tokens，
            // 本地 CPU 模型推理时间从 15~35s 降到 3~8s，中文召回不受损。
            var compact = string.Join(" ", catalog.Select(c => $"{c.Id}={c.ChineseName}"));
            sb.AppendLine("【全库命令目录】（格式: 命令名=中文名，所有可选命令都在这里，必须从中选择）：");
            sb.AppendLine(compact);
            sb.AppendLine();
        }

        sb.AppendLine("【重点候选详情】（含功能描述，最可能匹配的命令）：");
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

        sb.AppendLine(JsonSerializer.Serialize(list, JsonOpts));
        sb.AppendLine();
        sb.AppendLine("硬性规则：");
        sb.AppendLine($"1. 只能推荐上面目录中存在的 id，禁止编造任何命令。");
        sb.AppendLine($"2. 最多输出 {maxRecommendations} 条，按可能性从高到低排序。");
        sb.AppendLine("3. 输出必须是严格 JSON，格式：{\"recommendations\":[{\"command_id\":\"LINE\",\"confidence\":0.92}]}，confidence 取值 0~1。");
        sb.AppendLine("4. 不要输出任何 JSON 以外的文字、解释或代码块标记。");
        sb.AppendLine("5. 若重点候选详情都不匹配，可从全库目录中另选——不要输出空数组。");
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

                // 小模型常见污染：把中文名/别名拼进 id（"ERASE/删除"、"ERASE 删除"、"ERASE(E)"）。
                // 提取首个 ASCII 字母数字段作为规范 id；提取失败再判空。
                var normalized = NormalizeCommandId(id!);
                if (string.IsNullOrEmpty(normalized)) continue;

                double conf = 0.5;
                if (item.TryGetProperty("confidence", out var confEl))
                {
                    if (confEl.ValueKind == JsonValueKind.Number && confEl.TryGetDouble(out var d))
                    {
                        conf = Math.Clamp(d, 0, 1);
                    }
                }

                result.Add(new LlmRecommendation(normalized, conf));
            }

            return result.Count == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从污染的 command_id 提取规范 ASCII 命令名（如 "ERASE/删除" → "ERASE"）。</summary>
    public static string NormalizeCommandId(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if ((char.IsAscii(ch) && char.IsLetterOrDigit(ch)) || ch == '_' || ch == '-')
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                break; // 首段结束后遇到非 ASCII 字符即截断
            }
        }

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
