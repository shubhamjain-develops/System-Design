using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Accumulates permits at a steady rate up to a capacity, spending them as requests arrive.
/// </summary>
/// <remarks>
/// <para>
/// The usual default for a user-facing API gateway, because gateway traffic is bursty and a
/// burst is normally a client doing something reasonable — opening a page that fires eight
/// parallel requests — rather than abuse. The bucket absorbs that up to
/// <see cref="RateLimitPolicy.EffectiveCapacity"/> while still holding the caller to
/// <see cref="RateLimitPolicy.PermitsPerSecond"/> over any longer span.
/// </para>
/// <para>
/// <strong>Tokens accrue lazily.</strong> Nothing refills on a timer. The bucket is brought up
/// to date on each evaluation from the time elapsed since it was last touched, which means an
/// idle key costs nothing at all and there is no background work proportional to the number of
/// keys. This is what makes the algorithm viable with millions of callers.
/// </para>
/// <para>
/// <strong>An unseen key starts full, not empty.</strong> A new caller has consumed nothing, so
/// starting it empty would reject the first request of every new client — which in practice
/// means every client, since keys expire.
/// </para>
/// <para>
/// <strong>A backwards clock cannot mint permits.</strong> Wall clocks jump when NTP corrects
/// them, and a naive elapsed-time calculation against a backwards jump yields a negative
/// interval that quietly credits the bucket. The elapsed interval is clamped at zero so the
/// worst a clock correction can do is stall refill briefly.
/// </para>
/// </remarks>
public sealed class TokenBucketAlgorithm : IRateLimitAlgorithm
{
    /// <inheritdoc />
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.TokenBucket;

    /// <inheritdoc />
    public AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        double capacity = policy.EffectiveCapacity;
        double ratePerSecond = policy.PermitsPerSecond;

        double tokens;
        if (current.LastUpdatedTicks == 0)
        {
            tokens = capacity;
        }
        else
        {
            // Clamped: a wall clock that moved backwards must not credit the bucket.
            long elapsedTicks = Math.Max(0, now.UtcTicks - current.LastUpdatedTicks);
            double refill = elapsedTicks / (double)TimeSpan.TicksPerSecond * ratePerSecond;
            tokens = Math.Min(capacity, current.Tokens + refill);
        }

        TimeSpan ttl = TimeToLive(capacity, ratePerSecond, policy.Window);

        if (tokens < permits)
        {
            double deficit = permits - tokens;
            long waitTicks = (long)Math.Ceiling(deficit / ratePerSecond * TimeSpan.TicksPerSecond);

            return new AlgorithmOutcome(
                IsAllowed: false,
                NextState: current,
                // The refill just computed need not be written. It is recomputed from
                // LastUpdatedTicks on the next evaluation and reaches the same number, because
                // accrual depends only on elapsed time — so persisting it would buy nothing and
                // cost a write on the rejection path.
                RequiresPersist: false,
                RemainingPermits: (long)Math.Floor(tokens),
                RetryAfter: new TimeSpan(Math.Max(waitTicks, TimeSpan.TicksPerMillisecond)),
                TimeToLive: ttl);
        }

        double remaining = tokens - permits;

        return new AlgorithmOutcome(
            IsAllowed: true,
            NextState: new LimiterState { Tokens = remaining, LastUpdatedTicks = now.UtcTicks },
            RequiresPersist: true,
            RemainingPermits: (long)Math.Floor(remaining),
            RetryAfter: null,
            TimeToLive: ttl);
    }

    /// <summary>
    /// How long stored state must outlive its last write.
    /// </summary>
    /// <remarks>
    /// A correctness bound rather than a memory hint. State that expires while the bucket is
    /// drained resets the caller to a full bucket, which is the bypass this whole component
    /// exists to prevent. The state stops mattering only once the bucket would have refilled
    /// completely, so the TTL is the full refill time, doubled for margin against clock skew,
    /// and never shorter than the policy window.
    /// </remarks>
    private static TimeSpan TimeToLive(double capacity, double ratePerSecond, TimeSpan window)
    {
        double secondsToRefill = capacity / ratePerSecond;
        TimeSpan refill = TimeSpan.FromSeconds(secondsToRefill * 2);
        return refill > window ? refill : window * 2;
    }
}
