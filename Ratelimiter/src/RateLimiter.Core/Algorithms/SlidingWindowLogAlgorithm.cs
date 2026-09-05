using System.Collections.Immutable;
using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Retains the timestamp of every admitted request and counts those inside the trailing window.
/// </summary>
/// <remarks>
/// <para>
/// The only exact algorithm here. There is no boundary artefact and no approximation: at any
/// instant, the number admitted in the preceding window is known precisely, because every one
/// of them is written down. This is what a precision-critical endpoint — payments, password
/// attempts, anything where "roughly N" is not an acceptable answer — should use.
/// </para>
/// <para>
/// The cost is storage proportional to the limit. A limit of 5 keeps five timestamps per key;
/// a limit of 10,000 keeps ten thousand, per key, and that is the reason this is not the
/// default. It is also the algorithm that strains the store abstraction hardest: the state is a
/// list rather than a scalar, so every evaluation reads and rewrites the whole list, where a
/// Redis-native sorted set would append and trim in place. That inefficiency is the price of
/// keeping the store interface algorithm-agnostic, and it is a genuine one.
/// </para>
/// <para>
/// The window is half-open. A request exactly one window old has aged out and no longer counts,
/// which is what makes the limit a true "N per window" rather than "N per window, plus one at
/// each instant of exact overlap".
/// </para>
/// </remarks>
public sealed class SlidingWindowLogAlgorithm : IRateLimitAlgorithm
{
    /// <inheritdoc />
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.SlidingWindowLog;

    /// <inheritdoc />
    public AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        long cutoff = now.UtcTicks - policy.Window.Ticks;
        ImmutableArray<long> retained = Prune(current.Timestamps, cutoff);

        // The state becomes equivalent to absent one full window after the newest entry, so the
        // TTL must outlast that. Doubling gives margin against clock skew between nodes without
        // making the entry meaningfully more expensive to keep.
        TimeSpan ttl = policy.Window * 2;

        if (retained.Length + permits > policy.Limit)
        {
            // The oldest retained request is the one whose expiry frees the next slot. Being
            // exact here is cheap for this algorithm and lets a well-behaved client retry once
            // rather than poll.
            long oldest = retained[0];
            long admissibleAt = oldest + policy.Window.Ticks;
            long waitTicks = Math.Max(admissibleAt - now.UtcTicks, TimeSpan.TicksPerMillisecond);

            return new AlgorithmOutcome(
                IsAllowed: false,
                NextState: current,
                // Pruning alone is not worth a write. The entries are dropped again on the next
                // read for free, and the TTL bounds the memory regardless — so the rejection
                // path stays a single read, which is what a limiter under attack spends most of
                // its time doing.
                RequiresPersist: false,
                RemainingPermits: Math.Max(0, policy.Limit - retained.Length),
                RetryAfter: new TimeSpan(waitTicks),
                TimeToLive: ttl);
        }

        ImmutableArray<long>.Builder builder = ImmutableArray.CreateBuilder<long>(retained.Length + permits);
        builder.AddRange(retained);
        for (int i = 0; i < permits; i++)
        {
            builder.Add(now.UtcTicks);
        }

        return new AlgorithmOutcome(
            IsAllowed: true,
            NextState: new LimiterState { Timestamps = builder.ToImmutable() },
            RequiresPersist: true,
            RemainingPermits: policy.Limit - (retained.Length + permits),
            RetryAfter: null,
            TimeToLive: ttl);
    }

    /// <summary>
    /// Drops timestamps that have aged out of the trailing window.
    /// </summary>
    /// <remarks>
    /// The list is maintained in ascending order because entries are only ever appended at the
    /// current instant, so expired entries form a prefix and pruning is a single scan from the
    /// front rather than a filter over the whole list.
    /// </remarks>
    private static ImmutableArray<long> Prune(ImmutableArray<long> timestamps, long cutoff)
    {
        if (timestamps.IsDefaultOrEmpty)
        {
            return ImmutableArray<long>.Empty;
        }

        int firstLive = 0;
        while (firstLive < timestamps.Length && timestamps[firstLive] <= cutoff)
        {
            firstLive++;
        }

        if (firstLive == 0)
        {
            return timestamps;
        }

        return firstLive == timestamps.Length
            ? ImmutableArray<long>.Empty
            : timestamps[firstLive..];
    }
}
