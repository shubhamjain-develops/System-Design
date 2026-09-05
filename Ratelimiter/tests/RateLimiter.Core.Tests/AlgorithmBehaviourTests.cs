using RateLimiter.Core.Algorithms;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Pins the behaviour that distinguishes each algorithm from the others.
/// </summary>
/// <remarks>
/// The boundary tests prove every algorithm honours the same entitlement. These prove they
/// differ in how they get there — which is the entire reason for having five rather than one,
/// and the thing a rules file is choosing between.
/// </remarks>
public sealed class AlgorithmBehaviourTests
{
    private const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static RateLimitPolicy Policy(RateLimitAlgorithm kind, int? burst = null) => new()
    {
        Algorithm = kind,
        Limit = Limit,
        Window = Window,
        BurstCapacity = burst,
    };

    private sealed class Simulator(RateLimitAlgorithm kind, RateLimitPolicy? policy = null)
    {
        private readonly IRateLimitAlgorithm _algorithm = AlgorithmRegistry.Default.Resolve(kind);
        private readonly RateLimitPolicy _policy = policy ?? Policy(kind);
        private LimiterState _state = LimiterState.Empty;

        public bool Send(DateTimeOffset now, int permits = 1)
        {
            AlgorithmOutcome outcome = _algorithm.Evaluate(_state, _policy, now, permits);

            if (outcome.RequiresPersist)
            {
                _state = outcome.NextState;
            }

            return outcome.IsAllowed;
        }

        public int SendMany(DateTimeOffset now, int count)
        {
            int allowed = 0;
            for (int i = 0; i < count; i++)
            {
                if (Send(now))
                {
                    allowed++;
                }
            }

            return allowed;
        }
    }

    // ---- Fixed window: the boundary flaw, demonstrated rather than described ----

    [Fact]
    public void Fixed_window_admits_double_the_limit_across_a_boundary()
    {
        // The textbook weakness, and the reason it is a poor default. Ten requests inside one
        // millisecond, against a policy of five per ten seconds.
        Simulator sim = new(RateLimitAlgorithm.FixedWindow);
        ManualClock clock = new();
        clock.Advance(Window - TimeSpan.FromMilliseconds(1));

        int beforeBoundary = sim.SendMany(clock.UtcNow, Limit);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        int afterBoundary = sim.SendMany(clock.UtcNow, Limit);

        Assert.Equal(Limit, beforeBoundary);
        Assert.Equal(Limit, afterBoundary);
        Assert.Equal(Limit * 2, beforeBoundary + afterBoundary);
    }

    [Fact]
    public void Fixed_window_boundaries_are_clock_aligned_not_anchored_to_first_use()
    {
        // Anchoring to first use is the variant that produces the window-reset bug, where a
        // caller that keeps trying never sees a reset. Here a key first seen mid-window still
        // resets at the shared boundary rather than ten seconds after it arrived.
        Simulator sim = new(RateLimitAlgorithm.FixedWindow);
        ManualClock clock = new();
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));
        Assert.False(sim.Send(clock.UtcNow));

        // One second later the aligned window turns over, even though this key is only one
        // second old.
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(sim.Send(clock.UtcNow));
    }

    // ---- Sliding window log: exact, and closes the boundary hole ----

    [Fact]
    public void Sliding_window_log_closes_the_boundary_hole_that_fixed_window_leaves()
    {
        // Identical traffic to the fixed-window test above, with the opposite outcome. This is
        // the trade being bought with per-request storage.
        Simulator sim = new(RateLimitAlgorithm.SlidingWindowLog);
        ManualClock clock = new();
        clock.Advance(Window - TimeSpan.FromMilliseconds(1));

        int beforeBoundary = sim.SendMany(clock.UtcNow, Limit);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        int afterBoundary = sim.SendMany(clock.UtcNow, Limit);

        Assert.Equal(Limit, beforeBoundary);
        Assert.Equal(0, afterBoundary);
    }

    [Fact]
    public void Sliding_window_log_frees_exactly_one_slot_as_each_request_ages_out()
    {
        // Exactness, stated as a property: the allowance returns request by request, at the
        // moment each one turns a full window old — not in a lump at a boundary.
        Simulator sim = new(RateLimitAlgorithm.SlidingWindowLog);
        ManualClock clock = new();

        // Five requests, one second apart.
        for (int i = 0; i < Limit; i++)
        {
            Assert.True(sim.Send(clock.UtcNow));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // t = 5s, all five still inside the ten-second window.
        Assert.False(sim.Send(clock.UtcNow));

        // t = 10s + 1 tick: the first request has just aged out, freeing exactly one slot.
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(1));
        Assert.True(sim.Send(clock.UtcNow));
        Assert.False(sim.Send(clock.UtcNow));
    }

    [Fact]
    public void Sliding_window_log_treats_an_exactly_one_window_old_request_as_expired()
    {
        // The half-open boundary. Counting it would make the policy "N per window plus one at
        // each instant of exact overlap", which is not what the limit says.
        Simulator sim = new(RateLimitAlgorithm.SlidingWindowLog);
        ManualClock clock = new();

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));

        clock.Advance(Window);

        Assert.True(sim.Send(clock.UtcNow));
    }

    // ---- Sliding window counter: decays rather than resetting ----

    [Fact]
    public void Sliding_window_counter_still_blocks_immediately_after_a_boundary()
    {
        // Where the fixed window admits a second full allowance, the counter carries the whole
        // previous window forward at the instant the new one opens.
        Simulator sim = new(RateLimitAlgorithm.SlidingWindowCounter);
        ManualClock clock = new();
        clock.Advance(Window - TimeSpan.FromMilliseconds(1));

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(sim.Send(clock.UtcNow));
    }

    [Fact]
    public void Sliding_window_counter_admits_as_the_previous_window_decays()
    {
        // With five carried in and no current-window traffic, one permit frees once the
        // previous window's weight has fallen by one: at 20% through the new window, i.e. two
        // seconds in.
        Simulator sim = new(RateLimitAlgorithm.SlidingWindowCounter);
        ManualClock clock = new();

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));

        clock.Advance(Window);
        Assert.False(sim.Send(clock.UtcNow));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(sim.Send(clock.UtcNow));
    }

    // ---- Token bucket: lazy accrual, capped ----

    [Fact]
    public void Token_bucket_starts_full_for_an_unseen_caller()
    {
        // Starting empty would reject the first request of every new client, and since keys
        // expire, that is eventually every client.
        Simulator sim = new(RateLimitAlgorithm.TokenBucket);

        Assert.Equal(Limit, sim.SendMany(new ManualClock().UtcNow, Limit));
    }

    [Fact]
    public void Token_bucket_accrues_fractionally_over_time()
    {
        // Five per ten seconds is half a permit per second. After two seconds exactly one
        // permit has accrued — no more, and not zero, which is what integer truncation would
        // wrongly produce.
        Simulator sim = new(RateLimitAlgorithm.TokenBucket);
        ManualClock clock = new();
        sim.SendMany(clock.UtcNow, Limit);

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(sim.Send(clock.UtcNow));
        Assert.False(sim.Send(clock.UtcNow));
    }

    [Fact]
    public void Token_bucket_never_accrues_past_capacity()
    {
        // Without the cap, an idle caller would bank an unbounded allowance and could spend a
        // week of quota in one second.
        Simulator sim = new(RateLimitAlgorithm.TokenBucket);
        ManualClock clock = new();
        sim.SendMany(clock.UtcNow, Limit);

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));
        Assert.False(sim.Send(clock.UtcNow));
    }

    [Fact]
    public void Token_bucket_allows_a_burst_above_the_window_allowance_when_configured()
    {
        // The one degree of freedom a window algorithm does not have: sustain five per ten
        // seconds, but tolerate ten at once.
        Simulator sim = new(
            RateLimitAlgorithm.TokenBucket,
            Policy(RateLimitAlgorithm.TokenBucket, burst: 10));

        Assert.Equal(10, sim.SendMany(new ManualClock().UtcNow, 12));
    }

    [Fact]
    public void Token_bucket_does_not_mint_permits_when_the_clock_jumps_backwards()
    {
        // NTP corrections move wall clocks backwards. An unclamped elapsed-time calculation
        // would produce a negative interval and quietly credit the bucket, which is a bypass.
        Simulator sim = new(RateLimitAlgorithm.TokenBucket);
        ManualClock clock = new();
        DateTimeOffset start = clock.UtcNow;

        sim.SendMany(clock.UtcNow, 2);

        clock.SetUtcNow(start - TimeSpan.FromSeconds(30));

        // Three permits remain and no more should have appeared.
        Assert.Equal(3, sim.SendMany(clock.UtcNow, 4));
    }

    // ---- Leaky bucket: drains at a constant rate ----

    [Fact]
    public void Leaky_bucket_admits_again_only_as_the_queue_drains()
    {
        Simulator sim = new(RateLimitAlgorithm.LeakyBucket);
        ManualClock clock = new();

        Assert.Equal(Limit, sim.SendMany(clock.UtcNow, Limit));
        Assert.False(sim.Send(clock.UtcNow));

        // Draining at half a unit per second, two seconds frees exactly one slot.
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(sim.Send(clock.UtcNow));
        Assert.False(sim.Send(clock.UtcNow));
    }

    [Fact]
    public void Leaky_bucket_and_token_bucket_are_indistinguishable_through_this_interface()
    {
        // A real finding, recorded as a test rather than glossed over.
        //
        // Under an allow/reject interface the two algorithms are duals: tracking remaining
        // capacity and tracking queue depth are the same number counted from opposite ends, and
        // both accrue lazily at the same rate. Every published difference between them — the
        // smooth output rate, the absence of bursting — comes from the leaky bucket QUEUING the
        // requests it cannot serve yet. Strip the queue out, as an API gateway must because
        // holding a request consumes a connection, and nothing observable is left to tell them
        // apart.
        //
        // The honest conclusion is that leaky bucket earns its place here as a teaching
        // artefact and for parity with the design notes, not because it decides anything the
        // token bucket would decide differently.
        Simulator token = new(RateLimitAlgorithm.TokenBucket);
        Simulator leaky = new(RateLimitAlgorithm.LeakyBucket);
        ManualClock clock = new();

        int[] burstSizes = [3, 4, 1, 6, 2];
        TimeSpan[] gaps =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(7),
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(21),
            TimeSpan.FromSeconds(3),
        ];

        for (int i = 0; i < burstSizes.Length; i++)
        {
            clock.Advance(gaps[i]);

            for (int j = 0; j < burstSizes[i]; j++)
            {
                Assert.Equal(token.Send(clock.UtcNow), leaky.Send(clock.UtcNow));
            }
        }
    }

    // ---- Dispatch actually dispatches ----

    [Fact]
    public void The_five_algorithms_do_not_all_agree_on_the_same_traffic()
    {
        // Supports the claim that switching the algorithm in a rules file changes behaviour.
        // If every algorithm produced the same sequence, dispatch would be untestable and a
        // misconfigured registry would be invisible.
        Dictionary<RateLimitAlgorithm, string> traces = [];

        foreach (RateLimitAlgorithm kind in Enum.GetValues<RateLimitAlgorithm>())
        {
            ManualClock local = new();
            Simulator sim = new(kind);
            List<char> trace = [];

            // Saturate just before an aligned window boundary, then step across it. The offset
            // matters: advancing by exactly one full window makes the fixed window and the exact
            // log agree, because both correctly consider a request one whole window old to have
            // expired. Their disagreement is only visible when the boundary falls between two
            // closely spaced requests, which is precisely the case the fixed window mishandles.
            local.Advance(Window - TimeSpan.FromMilliseconds(1));

            for (int i = 0; i < 6; i++)
            {
                trace.Add(sim.Send(local.UtcNow) ? 'A' : 'R');
            }

            local.Advance(TimeSpan.FromMilliseconds(1));

            for (int i = 0; i < 6; i++)
            {
                trace.Add(sim.Send(local.UtcNow) ? 'A' : 'R');
            }

            traces[kind] = new string([.. trace]);
        }

        Assert.True(
            traces.Values.Distinct().Count() > 1,
            $"expected the algorithms to diverge, got: {string.Join(", ", traces.Select(t => $"{t.Key}={t.Value}"))}");

        // The specific divergence that matters most: at a boundary the fixed window forgets and
        // the exact sliding window does not.
        Assert.NotEqual(
            traces[RateLimitAlgorithm.FixedWindow],
            traces[RateLimitAlgorithm.SlidingWindowLog]);
    }
}
