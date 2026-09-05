using RateLimiter.Core.Algorithms;
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
            return new RateLimitDecision
            {
                IsAllowed = true,
                RuleName = UnmatchedRuleName,
                Algorithm = default,
                LimiterKey = string.Empty,
                RemainingPermits = long.MaxValue,
            };
        }

        string key = LimiterKey.Build(rule, context);
        IRateLimitAlgorithm algorithm = _algorithms.Resolve(rule.Policy.Algorithm);

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
                return Decide(rule, key, outcome, storeFailure: false);
            }

            bool written = await _store
                .TryWriteAsync(key, version, outcome.NextState, outcome.TimeToLive, cancellationToken)
                .ConfigureAwait(false);

            if (written)
            {
                return Decide(rule, key, outcome, storeFailure: false);
            }

            // Lost the race. The algorithm is a pure function, so there is nothing to undo:
            // re-read and recompute against whatever the winner wrote.
        }

        // Every attempt lost. The engine cannot say what this caller has consumed, which is the
        // same position an unreachable store would leave it in, so it is reported as a store
        // failure. Slice 8 makes the response to that configurable; until then the engine
        // favours availability, which is the right default for a gateway but must be visible.
        return new RateLimitDecision
        {
            IsAllowed = true,
            RuleName = rule.Name,
            Algorithm = rule.Policy.Algorithm,
            LimiterKey = key,
            RemainingPermits = 0,
            StoreFailureOccurred = true,
        };
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
