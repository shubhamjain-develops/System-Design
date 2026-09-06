using RateLimiter.Core.Algorithms;
using RateLimiter.Core.Resilience;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core;

/// <summary>
/// Applies the rule set to a request: selects the rule, builds the key, dispatches to the
/// algorithm, and settles the result against the store.
/// </summary>
/// <remarks>
/// <para>
/// This is the only type that knows about all the others, and keeping that knowledge in one
/// place is what lets each of them stay simple. Rules do not know about storage; algorithms do
/// not know about rules; the store knows about neither.
/// </para>
/// <para>
/// The read-compute-swap loop lives here rather than in the store or the algorithm, which is a
/// deliberate consequence of making algorithms pure. Because the algorithm is a function, losing
/// a race costs only a re-read and a recomputation — there is no partial write to unwind and no
/// lock to have held.
/// </para>
/// </remarks>
public sealed class RateLimitEngine
{
    /// <summary>
    /// The rule name reported when no rule matched a request.
    /// </summary>
    public const string UnmatchedRuleName = "(unmatched)";

    private readonly IRuleSource _rules;
    private readonly IRateLimitStore _store;
    private readonly AlgorithmRegistry _algorithms;
    private readonly IClock _clock;
    private readonly RateLimiterOptions _options;

    /// <summary>
    /// Creates an engine.
    /// </summary>
    /// <param name="rules">Supplies the rule set in force.</param>
    /// <param name="store">Holds limiter state.</param>
    /// <param name="algorithms">Resolves an algorithm from a policy. Defaults to the built-in five.</param>
    /// <param name="clock">Supplies the current instant. Defaults to the system clock.</param>
    /// <param name="options">Tuning. Defaults apply when omitted.</param>
    public RateLimitEngine(
        IRuleSource rules,
        IRateLimitStore store,
        AlgorithmRegistry? algorithms = null,
        IClock? clock = null,
        RateLimiterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(store);

        _rules = rules;
        _store = store;
        _algorithms = algorithms ?? AlgorithmRegistry.Default;
        _clock = clock ?? SystemClock.Instance;
        _options = options ?? new RateLimiterOptions();
    }

    /// <summary>
    /// Decides whether a request may proceed.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="permits">How many permits it consumes. Must be positive.</param>
    /// <param name="cancellationToken">Cancels the store operations.</param>
    /// <returns>The decision, including the rule and algorithm that produced it.</returns>
    public async ValueTask<RateLimitDecision> EvaluateAsync(
        RequestContext context,
        int permits = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        RuleSet ruleSet = _rules.Current;

        if (!ruleSet.TryMatch(context, out RateLimitRule? rule))
        {
            // Nothing to enforce. Reported explicitly rather than silently allowed, so that a
            // rule set that matches nothing is visible in the output instead of looking like a
            // limiter that is working.
            return Record(new RateLimitDecision
            {
                IsAllowed = true,
                RuleName = UnmatchedRuleName,
                Algorithm = default,
                LimiterKey = string.Empty,
                RemainingPermits = long.MaxValue,
            });
        }

        string key = LimiterKey.Build(rule, context);
        IRateLimitAlgorithm algorithm = _algorithms.Resolve(rule.Policy.Algorithm);

        // Ask the breaker before touching the store. When it is open the store is not called at
        // all, so a request during an outage costs nothing rather than paying a timeout — which
        // at any real volume is the difference between a degraded limiter and a broken service.
        CircuitBreaker? breaker = _options.CircuitBreaker;

        if (breaker is not null && !breaker.TryEnter())
        {
            Metrics.RecordShortCircuit();
            return FailureDecision(rule, key);
        }

        // Once entered, the breaker is owed an outcome. While half-open it hands out exactly one
        // trial slot, and a path that left without reporting success or failure would hold that
        // slot forever — every later request then short-circuits, which under the default
        // fail-open policy means the limiter stops limiting permanently and silently. The finally
        // below returns the slot even on a cancellation or an exception this method declines to
        // absorb.
        bool outcomeRecorded = false;

        try
        {
            for (int attempt = 0; attempt < _options.MaxWriteAttempts; attempt++)
            {
                StoreEntry? entry = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);

                LimiterState state = entry?.State ?? LimiterState.Empty;
                long version = entry?.Version ?? 0;

                AlgorithmOutcome outcome = algorithm.Evaluate(state, rule.Policy, _clock.UtcNow, permits);

                if (!outcome.RequiresPersist)
                {
                    // Nothing to write, so nothing to race over. This is the rejection path for
                    // every algorithm, which is what keeps a limiter cheap while under attack.
                    breaker?.RecordSuccess();
                    outcomeRecorded = true;
                    return Record(Decide(rule, key, outcome, storeFailure: false));
                }

                bool written = await _store
                    .TryWriteAsync(key, version, outcome.NextState, outcome.TimeToLive, cancellationToken)
                    .ConfigureAwait(false);

                if (written)
                {
                    breaker?.RecordSuccess();
                    outcomeRecorded = true;
                    return Record(Decide(rule, key, outcome, storeFailure: false));
                }

                // Lost the race. The algorithm is a pure function, so there is nothing to undo:
                // re-read and recompute against whatever the winner wrote.
            }

            // Every write attempt lost its race. The engine cannot say what this caller has
            // consumed, which is the same position an unreachable store leaves it in, so it is
            // routed through the same policy. It is counted separately because the remedy is
            // different: contention calls for server-side evaluation, not for fixing a store.
            Metrics.RecordWriteContentionExhausted();

            // Contention is evidence the store is alive and busy, not that it is failing, so this
            // deliberately does not count toward the breaker.
            breaker?.RecordSuccess();
            outcomeRecorded = true;

            return FailureDecision(rule, key);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up, not the store. Cancellation is not evidence of ill health in
            // either direction, so it must neither trip the breaker nor count as a recovery —
            // but the trial slot still has to go back, which the finally does.
            throw;
        }
        catch (Exception ex) when (ex is not (ArgumentException or KeyNotFoundException))
        {
            // The store is unreachable or misbehaving. Programming errors — an unregistered
            // algorithm, a bad argument — are deliberately excluded: those are bugs, and
            // silently failing open on a bug would hide it behind a limiter that appears to work.
            breaker?.RecordFailure();
            outcomeRecorded = true;
            return FailureDecision(rule, key);
        }
        finally
        {
            if (!outcomeRecorded)
            {
                breaker?.AbandonTrial();
            }
        }
    }

    /// <summary>
    /// Counters describing what this engine has done.
    /// </summary>
    public RateLimiterMetrics Metrics { get; } = new();

    private RateLimitDecision FailureDecision(RateLimitRule rule, string key)
    {
        Metrics.RecordStoreFailure();

        bool allowed = _options.OnStoreFailure == StoreFailurePolicy.FailOpen;

        return Record(new RateLimitDecision
        {
            IsAllowed = allowed,
            RuleName = rule.Name,
            Algorithm = rule.Policy.Algorithm,
            LimiterKey = key,
            RemainingPermits = 0,

            // Not a computed value: the caller has not been told to wait for a permit, but for
            // the limiter to recover.
            RetryAfter = allowed ? null : _options.FailClosedRetryAfter,
            StoreFailureOccurred = true,
        });
    }

    private RateLimitDecision Record(RateLimitDecision decision)
    {
        if (decision.IsAllowed)
        {
            Metrics.RecordAllowed();
        }
        else
        {
            Metrics.RecordRejected();
        }

        return decision;
    }

    private static RateLimitDecision Decide(
        RateLimitRule rule,
        string key,
        AlgorithmOutcome outcome,
        bool storeFailure) => new()
        {
            IsAllowed = outcome.IsAllowed,
            RuleName = rule.Name,
            Algorithm = rule.Policy.Algorithm,
            LimiterKey = key,
            RemainingPermits = outcome.RemainingPermits,
            RetryAfter = outcome.RetryAfter,
            StoreFailureOccurred = storeFailure,
        };
}
