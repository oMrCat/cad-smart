namespace CadSmart.Core.Abstractions;

/// <summary>
/// 用户行为存储（使用频次，用于个性化加权）。
/// </summary>
public interface IBehaviorStore
{
    IReadOnlyDictionary<string, int> GetUsageCounts();

    void Increment(string commandId);

    void Save();
}
