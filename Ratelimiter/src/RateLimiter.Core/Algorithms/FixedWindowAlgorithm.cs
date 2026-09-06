using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Counts requests in clock-aligned windows, resetting at each boundary.
/// </summary>
/// <remarks>
/// <para>
/// The cheapest algorithm here: one counter and one timestamp per key, one comparison per
/// request. Its weakness is equally well known and is not fixable within the design — a caller
/// that spends a full allowance in the last instant of one window and another in the first
/// instant of the next has achieved twice the intended rate over that span. With a limit of 5
/// per 10 seconds, 10 requests can land inside a single second.
/// </para>
/// <para>
/// <strong>Windows are aligned to the clock, not to the first request.</strong> The
/// alternative — anchoring the window at whenever the key was first seen — is a common
/// implementation and the source of a subtle bug: every write that refreshes the anchor
/// extends the window, so a caller that keeps requesting never sees a reset and stays blocked
/// until it goes fully idle. Alignment removes the anchor entirely, which also means every
/// node in a fleet computes the same boundary from the clock alone with nothing to agree on.
/// </para>
/// <para>
/// The cost of alignment is that all callers reset simultaneously, producing a thundering herd
/// at each boundary. That is a real trade and the reason a sliding algorithm is the better
/// default for anything user-facing.
/// </para>
/// </remarks>
public sealed class FixedWindowAlgorithm : IRateLimitAlgorithm
{
    /// <inheritdoc />
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.FixedWindow;

    /// <inheritdoc />
    public AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        long windowTicks = policy.Window.Ticks;
        long windowStart = now.UtcTicks - (now.UtcTicks % windowTicks);

        // A state from any window but this one carries no information about this one. That
        // covers both the ordinary next-window case and a state left over from long ago.
        long count = current.WindowStartTicks == windowStart ? current.Count : 0;

        TimeSpan ttl = policy.Window * 2;
        long windowEnd = windowStart + windowTicks;

        if (count + permits > policy.Limit)
        {
            return new AlgorithmOutcome(
                IsAllowed: false,
                NextState: current,
                // Nothing changed: the counter was not incremented and the window did not turn
                // over, so a write here would be pure cost on the path that is hottest under
                // attack.
                RequiresPersist: false,
                RemainingPermits: Math.Max(0, policy.Limit - count),
                RetryAfter: new TimeSpan(windowEnd - now.UtcTicks),
                TimeToLive: ttl);
        }

        long nextCount = count + permits;

        return new AlgorithmOutcome(
            IsAllowed: true,
            NextState: new LimiterState { WindowStartTicks = windowStart, Count = nextCount },
            RequiresPersist: true,
            RemainingPermits: policy.Limit - nextCount,
            RetryAfter: null,
            TimeToLive: ttl);
    }
}
