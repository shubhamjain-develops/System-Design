namespace RateLimiter.Core.Time;

/// <summary>
/// The production <see cref="IClock"/>, reading the machine's wall clock.
/// </summary>
/// <remarks>
/// A caveat worth knowing before this is trusted across a fleet: the wall clock can jump,
/// both forwards and backwards, when NTP corrects it. Every algorithm in this library is
/// therefore written to tolerate a non-monotonic clock — a backwards jump must not grant
/// extra permits. <see cref="System.Diagnostics.Stopwatch"/> would be monotonic but is not
/// usable here, because limiter state is shared between processes and a monotonic tick count
/// is meaningful only within one process.
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>
    /// A shared instance. The type is stateless, so one instance is sufficient and avoids
    /// allocating a clock per limiter.
    /// </summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
