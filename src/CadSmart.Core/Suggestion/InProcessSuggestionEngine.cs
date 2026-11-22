namespace CadSmart.Core.Suggestion;

using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;

/// <summary>
/// 进程内引擎：SuggestionService 的 ISuggestionEngine 包装（默认形态）。
/// </summary>
public sealed class InProcessSuggestionEngine : ISuggestionEngine
{
    private readonly SuggestionService _service;

    public InProcessSuggestionEngine(SuggestionService service)
    {
        _service = service;
    }

    public Task<SuggestionResult> SuggestAsync(string? userInput, CancellationToken ct = default)
        => _service.SuggestAsync(userInput, ct);

    public void Accept(string commandId) => _service.Accept(commandId);
}
