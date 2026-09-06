using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Models a queue that drains at a constant rate, admitting a request only while the queue has
/// room.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This one does not fit the interface as neatly as the other four, and that is worth
/// knowing rather than hiding.</strong> A leaky bucket is a traffic <em>shaper</em>: its
/// natural behaviour is to hold a request until the queue drains enough to send it, producing a
/// perfectly smooth output rate no matter how ragged the input. Expressed through an
/// allow/reject interface it becomes "reject when the queue is full", which keeps the shaping
/// property — the admitted rate is still constant — but discards the queuing that gives the
/// algorithm its name.
/// </para>
/// <para>
/// The consequence is that this implementation never delays anyone; it only refuses. That is
/// the right call for a synchronous API gateway, where holding a request open consumes a
/// connection and a thread and turns a rate limit into a latency problem. It would be the wrong
/// call for the contexts leaky bucket is classically used in — network bandwidth shaping, video
/// streaming — where the queue is the entire point.
/// </para>
/// <para>
/// <strong>How it differs from the token bucket in practice.</strong> The two are near-duals:
/// this tracks how full the queue is where a token bucket tracks how much allowance remains,
/// and both accrue lazily. The difference that matters is what happens to an idle caller. A
/// token bucket lets one save up to a full bucket and spend it in an instant; a leaky bucket
/// never accumulates credit, so an idle caller gains nothing by waiting and can never burst
/// above the drain rate. That is precisely why it is chosen when smoothness matters more than
/// responsiveness.
/// </para>
/// </remarks>
public sealed class LeakyBucketAlgorithm : IRateLimitAlgorithm
{
    /// <inheritdoc />
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.LeakyBucket;

    /// <inheritdoc />
    public AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        double capacity = policy.EffectiveCapacity;
        double leakPerSecond = policy.PermitsPerSecond;

        // Tokens holds queue depth for this algorithm: how much is waiting, not how much is
        // available. An unseen key has an empty queue, which is the permissive starting point,
        // matching the token bucket's full bucket.
        double depth;
        if (current.LastUpdatedTicks == 0)
        {
            depth = 0;
        }
        else
        {
            long elapsedTicks = Math.Max(0, now.UtcTicks - current.LastUpdatedTicks);
            double drained = elapsedTicks / (double)TimeSpan.TicksPerSecond * leakPerSecond;
            depth = Math.Max(0, current.Tokens - drained);
        }

        TimeSpan ttl = TimeToLive(capacity, leakPerSecond, policy.Window);

        if (depth + permits > capacity)
        {
            double overflow = depth + permits - capacity;
            long waitTicks = (long)Math.Ceiling(overflow / leakPerSecond * TimeSpan.TicksPerSecond);

            return new AlgorithmOutcome(
                IsAllowed: false,
                NextState: current,
                RequiresPersist: false,
                RemainingPermits: (long)Math.Floor(capacity - depth),
                RetryAfter: new TimeSpan(Math.Max(waitTicks, TimeSpan.TicksPerMillisecond)),
                TimeToLive: ttl);
        }

        double nextDepth = depth + permits;

        return new AlgorithmOutcome(
            IsAllowed: true,
            NextState: new LimiterState { Tokens = nextDepth, LastUpdatedTicks = now.UtcTicks },
            RequiresPersist: true,
            RemainingPermits: (long)Math.Floor(capacity - nextDepth),
            RetryAfter: null,
            TimeToLive: ttl);
    }

    /// <summary>
    /// How long stored state must outlive its last write: long enough for a full queue to have
    /// drained completely, after which the state says nothing an absent key does not.
    /// </summary>
    private static TimeSpan TimeToLive(double capacity, double leakPerSecond, TimeSpan window)
    {
        TimeSpan drain = TimeSpan.FromSeconds(capacity / leakPerSecond * 2);
        return drain > window ? drain : window * 2;
    }
}
