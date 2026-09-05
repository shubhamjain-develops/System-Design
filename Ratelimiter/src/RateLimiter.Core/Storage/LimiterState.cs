using System.Collections.Immutable;

namespace RateLimiter.Core.Storage;

/// <summary>
/// The accounting state one limiter key holds between requests.
/// </summary>
/// <remarks>
/// <para>
/// This is a flat union: every algorithm reads and writes the two or three fields it needs and
/// leaves the rest at their defaults. A fixed window uses <see cref="WindowStartTicks"/> and
/// <see cref="Count"/>; a token bucket uses <see cref="Tokens"/> and
/// <see cref="LastUpdatedTicks"/>; only the sliding window log uses
/// <see cref="Timestamps"/>.
/// </para>
/// <para>
/// The obvious alternative is an abstract base with a derived state type per algorithm, which
/// is tidier and would stop a token bucket from ever reading a window counter. It was
/// rejected on the boundary this type has to cross: state is written to a shared store, so a
/// polymorphic hierarchy makes every backend responsible for discriminated serialisation, and
/// a backend that guesses the wrong subtype fails at read time in production rather than at
/// compile time here. A flat record serialises the same way everywhere.
/// </para>
/// <para>
/// The cost is honest and small: each entry carries roughly forty bytes of fields it does not
/// use, and nothing but a test stops an algorithm reading a field it did not write. The first
/// is irrelevant next to the per-key dictionary overhead that dominates either way. The second
/// is why each algorithm's state handling is tested in isolation.
/// </para>
/// <para>
/// Time is stored as UTC tick counts rather than <see cref="DateTimeOffset"/> because these
/// values are compared and subtracted on every request and are serialised to a shared store,
/// where an integer has one unambiguous representation and a formatted timestamp has several.
/// </para>
/// </remarks>
public sealed record LimiterState
{
    /// <summary>
    /// An empty state, used as the starting point for a key that has no stored entry.
    /// </summary>
    public static readonly LimiterState Empty = new();

    /// <summary>
    /// UTC ticks at which the current counting window began.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="RateLimitAlgorithm.FixedWindow"/> and
    /// <see cref="RateLimitAlgorithm.SlidingWindowCounter"/>.
    /// </remarks>
    public long WindowStartTicks { get; init; }

    /// <summary>
    /// Requests admitted in the current window.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Requests admitted in the window immediately before the current one.
    /// </summary>
    /// <remarks>
    /// Read only by <see cref="RateLimitAlgorithm.SlidingWindowCounter"/>, which weights it by
    /// how far the current window has progressed. This one retained number is the entire
    /// difference between a fixed window and its sliding approximation.
    /// </remarks>
    public long PreviousCount { get; init; }

    /// <summary>
    /// Permits currently available in the bucket.
    /// </summary>
    /// <remarks>
    /// Fractional by necessity. A bucket refilling at 0.5 permits per second has earned half a
    /// permit after one second, and truncating that to zero on every evaluation would mean a
    /// caller polling twice a second never accrues anything at all.
    /// </remarks>
    public double Tokens { get; init; }

    /// <summary>
    /// UTC ticks at which <see cref="Tokens"/> was last brought up to date.
    /// </summary>
    public long LastUpdatedTicks { get; init; }

    /// <summary>
    /// UTC ticks of each admitted request still inside the trailing window, oldest first.
    /// </summary>
    /// <remarks>
    /// Only <see cref="RateLimitAlgorithm.SlidingWindowLog"/> populates this, and it is the
    /// reason that algorithm is exact where the others approximate. It is also the reason it
    /// is the expensive one: the list grows to <see cref="RateLimitPolicy.Limit"/> entries per
    /// key, so it is bounded but not cheap, and the bound is enforced on every evaluation
    /// rather than trusted.
    /// </remarks>
    public ImmutableArray<long> Timestamps { get; init; } = ImmutableArray<long>.Empty;
}
