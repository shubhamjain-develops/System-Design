using RateLimiter.Core.Storage;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// What an algorithm decided, and the state that decision implies.
/// </summary>
/// <param name="IsAllowed">Whether the request may proceed.</param>
/// <param name="NextState">
/// The state that should be stored if <paramref name="RequiresPersist"/> is set.
/// </param>
/// <param name="RequiresPersist">
/// Whether <paramref name="NextState"/> differs from the state that was read in a way worth a
/// write. Rejections frequently do not need one, and skipping the write is what keeps a
/// rejection cheaper than an admission — which matters most at exactly the moment a limiter is
/// under attack and rejecting nearly everything.
/// </param>
/// <param name="RemainingPermits">Permits still available under this policy.</param>
/// <param name="RetryAfter">
/// How long until the next permit becomes available, or <see langword="null"/> when allowed.
/// </param>
/// <param name="TimeToLive">
/// How long the stored state must survive. This is computed per algorithm rather than fixed,
/// because it is a correctness bound and not merely a memory hint: see
/// <see cref="IRateLimitAlgorithm"/>.
/// </param>
public readonly record struct AlgorithmOutcome(
    bool IsAllowed,
    LimiterState NextState,
    bool RequiresPersist,
    long RemainingPermits,
    TimeSpan? RetryAfter,
    TimeSpan TimeToLive);

/// <summary>
/// One admission-control strategy, expressed as a pure state transition.
/// </summary>
/// <remarks>
/// <para>
/// An implementation holds no state of its own. It receives the state read from the store,
/// the policy, the current instant and the permits requested, and returns the state that
/// should replace it. Implementations must be safe to share across threads, which is trivially
/// satisfied by holding no fields.
/// </para>
/// <para>
/// <strong>Why purity rather than a self-contained limiter object.</strong> The obvious design
/// gives each algorithm its own dictionary of buckets. It is less code and reads more
/// naturally. It was rejected because it fails silently and late: it works perfectly on one
/// node and begins admitting N times the limit the moment a second node starts, with no error
/// anywhere. Pushing state out to the store makes the single-node assumption impossible to
/// make by accident.
/// </para>
/// <para>
/// The second benefit is the one that matters day to day. Boundary conditions are where rate
/// limiters break — the classic bug is admitting the limit-plus-first request — and a pure
/// function lets those be asserted directly, with no store, no clock, no concurrency and no
/// setup. A test for "the sixth request in a window of five is refused" is three lines.
/// </para>
/// <para>
/// <strong>On the time to live.</strong> Each implementation computes its own, and it is a
/// correctness bound rather than a memory hint. Dropping state early is not free: a token
/// bucket whose state expires while half-drained hands its owner a full bucket, which is
/// precisely the bypass a limiter exists to prevent. The rule each implementation follows is
/// that the TTL must outlast the point at which the stored state has become equivalent to no
/// state at all — a window fully elapsed, or a bucket fully refilled.
/// </para>
/// </remarks>
public interface IRateLimitAlgorithm
{
    /// <summary>
    /// Which algorithm this implements, used to dispatch from a policy.
    /// </summary>
    RateLimitAlgorithm Kind { get; }

    /// <summary>
    /// Decides one request.
    /// </summary>
    /// <param name="current">
    /// The state read from the store, or <see cref="LimiterState.Empty"/> when the key is
    /// absent. Implementations must treat empty as "this caller has consumed nothing", which
    /// for the bucket algorithms means a full bucket rather than an empty one.
    /// </param>
    /// <param name="policy">The limits in force.</param>
    /// <param name="now">The current instant, supplied by the caller's clock.</param>
    /// <param name="permits">How many permits the request consumes. Must be positive.</param>
    /// <returns>The decision and the state it implies.</returns>
    AlgorithmOutcome Evaluate(LimiterState current, RateLimitPolicy policy, DateTimeOffset now, int permits);
}
