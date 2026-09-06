namespace RateLimiter.Core;

/// <summary>
/// Identifies which admission-control strategy a <see cref="RateLimitPolicy"/> uses.
/// </summary>
/// <remarks>
/// <para>
/// This is an enum rather than a <see cref="Type"/> or a string so that a rule loaded from
/// JSON can name an algorithm without the configuration file being able to load arbitrary
/// code. Configuration that can name a type is configuration that can execute code, which is
/// a larger security surface than a rate limiter needs.
/// </para>
/// <para>
/// The cost of that choice is that adding a sixth algorithm means editing this enum rather
/// than dropping in a plugin assembly. For a fixed, well-understood set of five strategies
/// that is the right trade: the set changes about once a decade.
/// </para>
/// </remarks>
public enum RateLimitAlgorithm
{
    /// <summary>
    /// Counts requests in fixed clock-aligned windows, resetting the counter at each boundary.
    /// Cheapest to run and easiest to reason about; its known flaw is that a client can send
    /// a full allowance immediately before a boundary and another immediately after, briefly
    /// achieving twice the intended rate.
    /// </summary>
    FixedWindow = 0,

    /// <summary>
    /// Retains the timestamp of every accepted request and admits a new one only when the
    /// count within the trailing window is below the limit. Exact, with no boundary artefact,
    /// at the cost of storing up to <see cref="RateLimitPolicy.Limit"/> timestamps per key.
    /// </summary>
    SlidingWindowLog = 1,

    /// <summary>
    /// Approximates the sliding window by weighting the previous window's count by how far
    /// the current window has progressed. Uses two counters instead of a timestamp list, so
    /// it costs the same as <see cref="FixedWindow"/> while removing most of the boundary
    /// artefact. It remains an approximation: it assumes the previous window's traffic was
    /// uniformly distributed, which bursty traffic is not.
    /// </summary>
    SlidingWindowCounter = 2,

    /// <summary>
    /// Accumulates tokens at a steady rate up to a capacity, spending one per request.
    /// Tolerates bursts up to the capacity, which is why it is the usual default for
    /// user-facing API gateways where bursty traffic is normal rather than abusive.
    /// </summary>
    TokenBucket = 3,

    /// <summary>
    /// Models a queue drained at a constant rate, admitting a request only when the queue has
    /// room. Produces a perfectly smooth output rate.
    /// </summary>
    /// <remarks>
    /// Unlike the other four, this algorithm is a traffic <em>shaper</em> rather than a pure
    /// admission controller: its natural behaviour is to delay a request, not to reject it.
    /// Expressed through this library's allow/reject interface it becomes "reject when the
    /// virtual queue is full", which preserves the shaping property but discards the delay.
    /// That semantic difference is real and is documented in the architecture decision record
    /// rather than papered over.
    /// </remarks>
    LeakyBucket = 4,
}
