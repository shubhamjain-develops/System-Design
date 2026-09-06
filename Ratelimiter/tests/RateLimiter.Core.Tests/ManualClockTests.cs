using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the test clock itself.
/// </summary>
/// <remarks>
/// Testing a test double looks circular, but every window-boundary assertion in this suite is
/// only as trustworthy as this class. A clock that drifted or tore under concurrency would
/// produce failures that read exactly like limiter bugs, and the investigation would start in
/// the wrong file.
/// </remarks>
public sealed class ManualClockTests
{
    [Fact]
    public void Default_start_is_fixed_rather_than_now()
    {
        // A clock defaulting to the real "now" makes every window test depend on when it runs,
        // so a suite that is green all day fails once at a real window boundary.
        ManualClock clock = new();

        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), clock.UtcNow);
    }

    [Fact]
    public void Does_not_move_on_its_own()
    {
        ManualClock clock = new();
        DateTimeOffset first = clock.UtcNow;
        DateTimeOffset second = clock.UtcNow;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Advance_moves_time_forward_by_exactly_the_delta()
    {
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(start.AddSeconds(10), clock.UtcNow);
    }

    [Fact]
    public void Advance_accumulates()
    {
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;

        clock.Advance(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(start.AddSeconds(7), clock.UtcNow);
    }

    [Fact]
    public void Advance_by_zero_is_allowed()
    {
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;

        clock.Advance(TimeSpan.Zero);

        Assert.Equal(start, clock.UtcNow);
    }

    [Fact]
    public void Advance_rejects_a_negative_delta()
    {
        // Rewinding is a real clock behaviour worth testing, but it should appear in a test
        // only when the author asked for it by name via SetUtcNow.
        ManualClock clock = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void SetUtcNow_can_move_time_backwards()
    {
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;
        clock.Advance(TimeSpan.FromMinutes(5));

        clock.SetUtcNow(start);

        Assert.Equal(start, clock.UtcNow);
    }

    [Fact]
    public void Reports_utc()
    {
        ManualClock clock = new(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.FromHours(5)));

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public async Task Advancing_while_many_readers_run_never_tears()
    {
        // Interlocked reads matter here: a torn 64-bit read would surface as a timestamp from
        // neither before nor after the advance, and the resulting limiter failure would look
        // like an algorithm bug rather than a clock bug.
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;
        const int advances = 1_000;

        using CancellationTokenSource cts = new();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                DateTimeOffset observed = clock.UtcNow;
                Assert.InRange(observed, start, start.AddTicks(advances));
            }
        })).ToArray();

        for (int i = 0; i < advances; i++)
        {
            clock.Advance(TimeSpan.FromTicks(1));
        }

        await cts.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Equal(start.AddTicks(advances), clock.UtcNow);
    }
}
