using RateLimiter.Core.Resilience;

namespace RateLimiter.Core;

/// <summary>
/// Tuning for <see cref="RateLimitEngine"/>.
/// </summary>
public sealed record RateLimiterOptions
{
    private readonly int _maxWriteAttempts = 8;

    /// <summary>
    /// How many times the engine will re-read and retry after losing a compare-and-swap race
    /// before giving up on the write.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
    /// <remarks>
    /// <para>
    /// A bound is required, not optional. An unbounded retry loop on a contended key turns a
    /// rate limiter — the component whose job is to shed load — into the thing amplifying it,
    /// and it does so precisely when the system is already in trouble.
    /// </para>
    /// <para>
    /// Eight is chosen so that losing every attempt is genuinely improbable rather than merely
    /// unlikely: each retry re-reads, so the loop only fails when a different writer wins eight
    /// consecutive races on one key. Exhausting it is treated as a store failure, because at
    /// that point the engine cannot say what the caller has consumed — which is the same
    /// position an unreachable store leaves it in.
    /// </para>
    /// </remarks>
    public int MaxWriteAttempts
    {
        get => _maxWriteAttempts;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxWriteAttempts = value;
        }
    }

    /// <summary>
    /// What to do when limiter state cannot be reached.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="StoreFailurePolicy.FailOpen"/>, which is right for a public
    /// gateway and wrong for anything metered, authenticating, or billed. Worth setting
    /// deliberately rather than inheriting.
    /// </remarks>
    public StoreFailurePolicy OnStoreFailure { get; init; } = StoreFailurePolicy.FailOpen;

    /// <summary>
    /// Guards calls to the store, short-circuiting once it starts failing.
    /// </summary>
    /// <remarks>
    /// Supplying one is optional but recommended. Without it, every request during a store
    /// outage pays the full cost of discovering the store is down — which at any real volume
    /// turns the limiter into the outage.
    /// </remarks>
    public CircuitBreaker? CircuitBreaker { get; init; }

    /// <summary>
    /// The retry delay advised when a request is refused because the store is unreachable and
    /// the policy is <see cref="StoreFailurePolicy.FailClosed"/>.
    /// </summary>
    /// <remarks>
    /// Not a computed value, because nothing has been computed: the caller is not waiting for a
    /// permit to accrue but for the limiter to recover, and the engine has no idea when that
    /// will be. A short fixed delay is the honest answer, and having one at all is what stops
    /// every refused caller retrying instantly and turning a store outage into a stampede.
    /// </remarks>
    public TimeSpan FailClosedRetryAfter { get; init; } = TimeSpan.FromSeconds(1);
}
