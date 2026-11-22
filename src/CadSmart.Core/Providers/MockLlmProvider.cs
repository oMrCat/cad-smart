namespace CadSmart.Core.Providers;

using System.Collections.Concurrent;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;

/// <summary>
/// Mock 供应商：用于无网络环境开发与测试。
/// 按“输入 → 推荐”字典返回结果，可模拟网络延迟。
/// </summary>
public sealed class MockLlmProvider : ILlmProvider
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<LlmRecommendation>> _map;
    private readonly int _delayMs;

    public MockLlmProvider(IReadOnlyDictionary<string, IReadOnlyList<LlmRecommendation>>? map = null, int delayMs = 120)
    {
        _map = new ConcurrentDictionary<string, IReadOnlyList<LlmRecommendation>>(
            map ?? new Dictionary<string, IReadOnlyList<LlmRecommendation>>(),
            StringComparer.Ordinal);
        _delayMs = delayMs;
    }

    /// <summary>模拟网络延迟（默认 120ms）。测试超时降级时可设为 2000。</summary>
    public int DelayMs
    {
        get => _delayMs;
        init => _delayMs = value;
    }

    public string Name => "mock";

    public bool IsAvailable => true;

    public void Set(string input, params LlmRecommendation[] recommendations)
        => _map[input.Trim()] = recommendations;

    public async Task<IReadOnlyList<LlmRecommendation>?> RecommendAsync(
        string userInput, IReadOnlyList<CadCommand> candidateScope, CancellationToken cancellationToken = default)
    {
        if (_delayMs > 0)
        {
            await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
        }

        var key = userInput.Trim();
        return _map.TryGetValue(key, out var recs) && recs.Count > 0 ? recs : null;
    }
}
