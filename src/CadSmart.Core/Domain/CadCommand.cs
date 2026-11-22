namespace CadSmart.Core.Domain;

/// <summary>
/// CAD 命令知识条目。
/// </summary>
public sealed class CadCommand
{
    /// <summary>命令标识（英文命令名，如 LINE）。</summary>
    public string Id { get; init; } = "";

    /// <summary>官方命令名（与 Id 一致，便于展示）。</summary>
    public string Name { get; init; } = "";

    /// <summary>中文名称，如“直线”。</summary>
    public string ChineseName { get; init; } = "";

    /// <summary>快捷别名，如 L、PL。</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>中文名全拼（无声调），如 zhixian。</summary>
    public string Pinyin { get; init; } = "";

    /// <summary>中文名拼音首字母，如 zx。</summary>
    public string PinyinInitials { get; init; } = "";

    /// <summary>分类，如 绘图/修改/标注。</summary>
    public string Category { get; init; } = "";

    /// <summary>功能描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>参数操作提示，如“指定第一点 → 下一点”。</summary>
    public string ParamsHint { get; init; } = "";

    /// <summary>中文口语关键词，如“画线”。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>用户使用次数（个性化加权）。</summary>
    public int UsageCount { get; set; }
}
