namespace RateLimiter.Core.Time;

/// <summary>
/// Supplies the current instant to every time-dependent component in the library.
/// </summary>
/// <remarks>
/// <para>
/// Every algorithm here is defined in terms of elapsed time, so time is an input to the
/// system and not an ambient fact. Nothing in this library calls
/// <see cref="DateTimeOffset.UtcNow"/> directly.
/// </para>
/// <para>
/// The reason is testability of exactly the code most likely to be wrong. A window boundary
/// test written against the real clock has to sleep, which makes the suite slow and — far
/// worse — flaky, because the assertion depends on the scheduler waking the thread in time.
/// Flaky boundary tests are worse than no boundary tests, because they train the reader to
/// re-run a red suite instead of reading it. With a clock seam, "what happens one tick before
/// the window closes" becomes an exact, instant assertion.
/// </para>
/// <para>
/// UTC is not a preference. Window arithmetic against a local clock breaks twice a year, and
/// it breaks by silently widening or narrowing one window rather than by throwing.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>
    /// Gets the current instant in UTC.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
