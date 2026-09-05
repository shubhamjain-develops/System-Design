namespace RateLimiter.Core.Time;

/// <summary>
/// An <see cref="IClock"/> whose time only moves when it is told to.
/// </summary>
/// <remarks>
/// <para>
/// This ships in the main library rather than in the test project for two reasons. Tests are
/// the obvious consumer, but the console demo is the other: it compresses a ten-second window
/// into a fraction of that so a burst can be watched, which is only possible if the demo owns
/// the clock. A type used by both a test and a shipped executable belongs in the library.
/// </para>
/// <para>
/// It is deliberately safe to share across threads. Concurrency tests need many tasks reading
/// the same clock while one advances it, and a clock that tore under that load would produce
/// failures that looked like limiter bugs.
/// </para>
/// </remarks>
public sealed class ManualClock : IClock
{
    private long _ticks;

    /// <summary>
    /// Creates a clock fixed at the given instant.
    /// </summary>
    /// <param name="start">The instant the clock initially reports.</param>
    public ManualClock(DateTimeOffset start)
    {
        _ticks = start.UtcTicks;
    }

    /// <summary>
    /// Creates a clock fixed at a stable, arbitrary instant (2020-01-01T00:00:00Z).
    /// </summary>
    /// <remarks>
    /// A fixed default rather than "now" keeps tests reproducible: a test that happens to run
    /// as a real window boundary passes is a test that fails once a day for no reason.
    /// </remarks>
    public ManualClock()
        : this(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>
    /// Moves the clock forward by <paramref name="delta"/>.
    /// </summary>
    /// <param name="delta">How far to advance. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="delta"/> is negative.
    /// </exception>
    /// <remarks>
    /// Advancing is restricted to forward motion so that a test cannot accidentally express
    /// something the production clock would never do. Rewinding, which is a real NTP
    /// behaviour worth testing, is available explicitly via <see cref="SetUtcNow"/> — the
    /// point being that a backwards clock should appear in a test only when the author meant
    /// it to.
    /// </remarks>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        Interlocked.Add(ref _ticks, delta.Ticks);
    }

    /// <summary>
    /// Sets the clock to an absolute instant, forwards or backwards.
    /// </summary>
    /// <param name="value">The instant the clock should report.</param>
    public void SetUtcNow(DateTimeOffset value)
    {
        Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }
}
