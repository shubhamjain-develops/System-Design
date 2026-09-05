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
}
