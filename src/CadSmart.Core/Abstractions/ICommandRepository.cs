namespace CadSmart.Core.Abstractions;

using CadSmart.Core.Domain;

/// <summary>
/// 命令知识库仓储（只读查询 + 使用计数）。
/// </summary>
public interface ICommandRepository
{
    IReadOnlyList<CadCommand> GetAll();

    CadCommand? FindById(string id);

    void IncrementUsage(string commandId);

    /// <summary>持久化使用计数等变更。</summary>
    void Save();
}
