namespace CadSmart.Core.Suggestion;

using System.Diagnostics;
using CadSmart.Core.Abstractions;
using CadSmart.Core.Domain;
using CadSmart.Core.Matching;
using CadSmart.Core.Providers;

/// <summary>
/// 双通道建议编排：
/// 1. 规则引擎同步出结果（本地 &lt;50ms，永远可用）；
/// 2. LLM 语义推荐异步补充，融合窗口（默认 300ms）内到达才合并，超时降级。
/// </summary>
public sealed class SuggestionService
{
    private readonly ICommandRepository _repo;
    private readonly IBehaviorStore _behavior;
    private readonly MatchEngine _engine;
    private readonly ILlmProvider? _llm;

    /// <summary>LLM 融合等待窗口（毫秒）。超时即放弃本轮 LLM 结果（降级纯规则）。
    /// 默认 300ms 适配云端/已热身的本地模型；本地 CPU 模型建议调到 20000+（真实推理 10~20s）。</summary>
    public int LlmWaitMs { get; set; } = 300;

    /// <summary>本地供应商（若配置）在建议前自动探测。</summary>
    private readonly LocalLlmProvider? _local;

    /// <summary>最近执行的命令（上下文感知推荐用，容量 20）。</summary>
    private readonly Queue<string> _recentCommands = new();

    /// <summary>上下文推荐器（可注入；默认内置转移表）。</summary>
    private readonly Context.IContextRecommender _context;

    /// <summary>上下文推荐的最大条数。</summary>
    public int ContextMaxCount { get; set; } = 5;

    public SuggestionService(
        ICommandRepository repo,
        IBehaviorStore behavior,
        MatchEngine? engine = null,
        ILlmProvider? llm = null,
        Context.IContextRecommender? contextRecommender = null)
    {
        _repo = repo;
        _behavior = behavior;
        _engine = engine ?? new MatchEngine();
        _llm = llm;
        _local = llm as LocalLlmProvider;
        _context = contextRecommender ?? new Context.TransitionTableRecommender(behavior);
    }

    /// <summary>
    /// 上下文感知推荐：根据最近执行的命令推荐"下一个命令"。
    /// 独立于文本建议（无输入时也可触发，如刚画完圆弹出"偏移？"）。
    /// </summary>
    public SuggestionResult SuggestNext()
    {
        var sw = Stopwatch.StartNew();
        var recs = _context.RecommendNext(_recentCommands.ToList(), ContextMaxCount);
        var items = new List<CommandCandidate>();
        foreach (var (id, conf) in recs)
        {
            var cmd = _repo.FindById(id);
            if (cmd is null)
            {
                continue;
            }

            items.Add(new CommandCandidate
            {
                Command = cmd,
                Score = Math.Round(conf * LlmMaxScore, 2),
                MatchedVia = "context-next",
                Source = "context",
                LlmConfidence = conf,
            });
        }

        return new SuggestionResult
        {
            Items = items,
            RuleLatencyMs = 0,
            TotalLatencyMs = sw.ElapsedMilliseconds,
        };
    }

    public async Task<SuggestionResult> SuggestAsync(string? userInput, CancellationToken ct = default)
    {
        var swTotal = Stopwatch.StartNew();

        var commands = _repo.GetAll();
        _allCommands = commands;
        var usage = _behavior.GetUsageCounts();

        // ---- 通道 1：规则引擎（同步、必达）----
        var swRule = Stopwatch.StartNew();
        var rules = _engine.Rank(userInput, commands, MatchEngine.DefaultTopN, usage);
        swRule.Stop();

        var items = new Dictionary<string, CommandCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules)
        {
            items[r.Command.Id] = r;
        }

        var result = new SuggestionResult
        {
            Items = rules,
            RuleLatencyMs = swRule.ElapsedMilliseconds,
        };

        // ---- 通道 2：LLM（异步、可降级）----
        // 两阶段策略：规则粗筛 Top-N 作为候选范围交给 LLM 精排（本地 CPU 模型处理 6000 token
        // 的全库表要 90+ 秒，30 条粗筛表 <1k token 只需数秒）。粗筛不足时再补足到下限。
        // 注意：本地供应商必须先 ProbeAsync 再判 IsAvailable（探测前 _openPort=0 恒为 false）。
        if (_llm is not null && !string.IsNullOrWhiteSpace(userInput))
        {
            if (_local is not null)
            {
                await _local.ProbeAsync(ct).ConfigureAwait(false);
            }

            if (_llm.IsAvailable)
            {
                // LLM 候选范围：规则命中 + 高使用频率命令补足（保证个性化常用药在列）
                const int LlmScopeSize = 30;
                var scopeList = new List<CadCommand>();
                var scopeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rules.Take(LlmScopeSize))
                {
                    scopeList.Add(r.Command);
                    scopeIds.Add(r.Command.Id);
                }

                if (scopeList.Count < LlmScopeSize)
                {
                    var freq = commands
                        .Where(c => !scopeIds.Contains(c.Id))
                        .OrderByDescending(c => usage.GetValueOrDefault(c.Id))
                        .ThenBy(c => c.Id, StringComparer.Ordinal);
                    foreach (var c in freq.Take(LlmScopeSize - scopeList.Count))
                    {
                        scopeList.Add(c);
                    }
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(Math.Max(LlmWaitMs, 1));

                var llmTask = _llm.RecommendAsync(userInput!, scopeList, linked.Token, commands);

                // 吞掉被放弃任务的异常/取消，避免 UnobservedTaskException
                _ = llmTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var completed = await Task.WhenAny(llmTask, Task.Delay(LlmWaitMs, linked.Token)).ConfigureAwait(false);
                if (completed == llmTask && llmTask.IsCompletedSuccessfully)
                {
                    var recs = llmTask.Result;
                    if (recs is { Count: > 0 })
                    {
                        result.LlmUsed = true;
                        MergeLlm(items, recs);
                    }
                }
                else
                {
                    result.LlmDegraded = true; // 超时：本轮用纯规则结果
                }
            }
        }

        var merged = items.Values.ToList();
        merged.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Command.Id, b.Command.Id);
        });

        result = new SuggestionResult
        {
            Items = merged,
            RuleLatencyMs = result.RuleLatencyMs,
            TotalLatencyMs = swTotal.ElapsedMilliseconds,
            LlmUsed = result.LlmUsed,
            LlmDegraded = result.LlmDegraded,
        };

        return result;
    }

    /// <summary>用户确认执行 → 记录行为与最近序列（个性化 + 上下文）。仓储内部会同步更新行为存储，此处不重复计数。</summary>
    public void Accept(string commandId)
    {
        _repo.IncrementUsage(commandId);
        lock (_recentCommands)
        {
            _recentCommands.Enqueue(commandId.ToUpperInvariant());
            while (_recentCommands.Count > 20)
            {
                _recentCommands.Dequeue();
            }
        }
    }

    private void MergeLlm(
        Dictionary<string, CommandCandidate> items,
        IReadOnlyList<LlmRecommendation> recs)
    {
        foreach (var rec in recs)
        {
            var llmScore = Math.Clamp(rec.Confidence, 0, 1) * LlmMaxScore;

            if (!items.TryGetValue(rec.CommandId, out var candidate))
            {
                // LLM 独有发现：只要命令存在于知识库就纳入（防编造 = 校验知识库），
                // 这是语义通道对规则通道的真正补盲。
                var cmd = _allCommands.FirstOrDefault(c =>
                    c.Id.Equals(rec.CommandId, StringComparison.OrdinalIgnoreCase));
                if (cmd is null)
                {
                    continue; // 知识库里没有 → 视为编造，丢弃
                }

                items[cmd.Id] = new CommandCandidate
                {
                    Command = cmd,
                    Score = Math.Round(llmScore, 2),
                    MatchedVia = "llm",
                    Source = "llm",
                    LlmConfidence = rec.Confidence,
                };
                continue;
            }

            if (llmScore > candidate.Score)
            {
                candidate.Score = Math.Round(llmScore, 2);
                candidate.LlmConfidence = rec.Confidence;
                candidate.Source = candidate.MatchedVia == "unknown" ? "llm" : "both";
            }
            else
            {
                candidate.LlmConfidence = rec.Confidence;
                candidate.Source = "both";
            }
        }
    }

    /// <summary>LLM 置信度 1.0 对应的分数上限（略低于规则精确命中 100，避免高置信幻觉压过确定匹配）。</summary>
    private const double LlmMaxScore = 98;

    private IReadOnlyList<CadCommand> _allCommands = Array.Empty<CadCommand>();
}
