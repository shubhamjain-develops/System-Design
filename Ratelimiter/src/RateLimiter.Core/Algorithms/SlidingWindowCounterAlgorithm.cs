using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Approximates a sliding window using the current window's count plus a weighted share of the
/// previous window's.
/// </summary>
/// <remarks>
/// <para>
/// The estimate is <c>previous × (1 − elapsed fraction) + current</c>. Ten seconds into a
/// sixty-second window, 5/6 of the previous window is still counted; fifty seconds in, only
/// 1/6 remains. The boundary artefact that makes a fixed window admit double therefore decays
/// smoothly instead of vanishing at once.
/// </para>
/// <para>
/// This is the algorithm most large gateways actually run, and the reason is cost rather than
/// accuracy: it needs two integers per key where an exact sliding window needs a timestamp
/// list, and it answers in constant time and constant memory regardless of the limit.
/// </para>
/// <para>
/// <strong>It is an approximation, and the assumption it makes is worth stating.</strong> The
/// weighting assumes the previous window's traffic was spread evenly across it. Real traffic is
/// bursty, so when a caller's previous-window requests were all clustered at its very start,
/// this over-counts them and rejects a caller that an exact window would admit; clustered at
/// the end, it under-counts and admits one it would refuse. The error is bounded by the
/// previous window's count and shrinks as the current window advances — acceptable for
/// general traffic, and the reason a payments endpoint should use the exact log instead.
/// </para>
/// </remarks>
public sealed class SlidingWindowCounterAlgorithm : IRateLimitAlgorithm
{
    /// <inheritdoc />
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.SlidingWindowCounter;

    /// <inheritdoc />
    public AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        long windowTicks = policy.Window.Ticks;
        long windowStart = now.UtcTicks - (now.UtcTicks % windowTicks);

        (long previousCount, long currentCount) = Roll(current, windowStart, windowTicks);

        // How far through the current window we are, in [0, 1).
        double elapsedFraction = (double)(now.UtcTicks - windowStart) / windowTicks;
        double weighted = (previousCount * (1.0 - elapsedFraction)) + currentCount;

        // Two windows must survive, because the next window will read this one as its previous.
        TimeSpan ttl = policy.Window * 2;

        if (weighted + permits > policy.Limit)
        {
            return new AlgorithmOutcome(
                IsAllowed: false,
                NextState: current,
                RequiresPersist: false,
                RemainingPermits: Math.Max(0, (long)Math.Floor(policy.Limit - weighted)),
                RetryAfter: TimeUntilAdmissible(
                    previousCount, currentCount, policy, permits, now, windowStart, windowTicks),
                TimeToLive: ttl);
        }

        LimiterState nextState = new()
        {
            WindowStartTicks = windowStart,
            Count = currentCount + permits,
            PreviousCount = previousCount,
        };

        return new AlgorithmOutcome(
            IsAllowed: true,
            NextState: nextState,
            RequiresPersist: true,
            RemainingPermits: Math.Max(0, (long)Math.Floor(policy.Limit - (weighted + permits))),
            RetryAfter: null,
            TimeToLive: ttl);
    }

    /// <summary>
    /// Maps stored counts onto the window in force now.
    /// </summary>
    private static (long Previous, long Current) Roll(LimiterState state, long windowStart, long windowTicks)
    {
        if (state.WindowStartTicks == windowStart)
        {
            return (state.PreviousCount, state.Count);
        }

        if (state.WindowStartTicks == windowStart - windowTicks)
        {
            // Exactly one window has passed: what was current becomes previous.
            return (state.Count, 0);
        }

        // Two or more windows of silence. Anything older cannot influence this window, and
        // carrying it forward would penalise a caller for traffic that has fully aged out.
        return (0, 0);
    }

    /// <summary>
    /// Computes when the weighted estimate will have decayed enough to admit the request.
    /// </summary>
    /// <remarks>
    /// The estimate falls only as the previous window's contribution decays, so this solves for
    /// the fraction at which it drops to the limit rather than guessing. When the current
    /// window's own count already exhausts the limit, no amount of decay helps and the answer
    /// is the next boundary.
    /// </remarks>
    private static TimeSpan TimeUntilAdmissible(
        long previousCount,
        long currentCount,
        RateLimitPolicy policy,
        int permits,
        DateTimeOffset now,
        long windowStart,
        long windowTicks)
    {
        long windowEnd = windowStart + windowTicks;
        double headroom = policy.Limit - permits - currentCount;

        if (headroom < 0 || previousCount == 0)
        {
            return new TimeSpan(windowEnd - now.UtcTicks);
        }

        // Need previous × (1 − f) ≤ headroom, so f ≥ 1 − headroom / previous.
        double requiredFraction = 1.0 - (headroom / previousCount);

        if (requiredFraction >= 1.0)
        {
            return new TimeSpan(windowEnd - now.UtcTicks);
        }

        long admissibleAt = windowStart + (long)Math.Ceiling(requiredFraction * windowTicks);
        long waitTicks = admissibleAt - now.UtcTicks;

        // Never advise a non-positive wait: the caller is being rejected now, so telling it to
        // retry immediately would turn a rejection into a hot retry loop.
        return waitTicks > 0 ? new TimeSpan(waitTicks) : new TimeSpan(windowEnd - now.UtcTicks);
    }
}
