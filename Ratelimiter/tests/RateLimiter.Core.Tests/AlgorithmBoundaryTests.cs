using RateLimiter.Core.Algorithms;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Drives every algorithm through the same boundary, because the limit-plus-first request is
/// the bug that actually happens.
/// </summary>
/// <remarks>
/// The original design notes for this project recorded exactly this defect found by hand: a
/// comparison that left a gap at <c>count == limit</c> and let unlimited requests through. These
/// tests exist so that no implementation here can regress into it, and they are written as a
/// theory across all five kinds so a sixth algorithm cannot be added without facing them.
/// </remarks>
public sealed class AlgorithmBoundaryTests
{
    public static TheoryData<RateLimitAlgorithm> AllKinds =>
    [
        RateLimitAlgorithm.FixedWindow,
        RateLimitAlgorithm.SlidingWindowLog,
        RateLimitAlgorithm.SlidingWindowCounter,
        RateLimitAlgorithm.TokenBucket,
        RateLimitAlgorithm.LeakyBucket,
    ];

    /// <summary>
    /// Replays requests against a pure algorithm, carrying state forward the way the engine will.
    /// </summary>
    private sealed class Simulator(IRateLimitAlgorithm algorithm, RateLimitPolicy policy)
    {
        private LimiterState _state = LimiterState.Empty;

        public AlgorithmOutcome Send(DateTimeOffset now, int permits = 1)
        {
            AlgorithmOutcome outcome = algorithm.Evaluate(_state, policy, now, permits);

            if (outcome.RequiresPersist)
            {
                _state = outcome.NextState;
            }

            return outcome;
        }
    }

    private static RateLimitPolicy Policy(RateLimitAlgorithm kind, int limit = 5) => new()
    {
        Algorithm = kind,
        Limit = limit,
        Window = TimeSpan.FromSeconds(10),
    };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Admits_exactly_the_limit_then_refuses_the_next(RateLimitAlgorithm kind)
    {
        // The uniformity claim made by RateLimitPolicy, checked: "5 per 10 seconds" means the
        // same entitlement under every algorithm, even though each shapes traffic differently
        // on the way there.
        RateLimitPolicy policy = Policy(kind);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        for (int i = 1; i <= policy.Limit; i++)
        {
            Assert.True(sim.Send(clock.UtcNow).IsAllowed, $"request {i} of {policy.Limit} should be admitted");
        }

        Assert.False(sim.Send(clock.UtcNow).IsAllowed);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Remaining_permits_count_down_to_zero(RateLimitAlgorithm kind)
    {
        RateLimitPolicy policy = Policy(kind);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        for (int i = 1; i <= policy.Limit; i++)
        {
            AlgorithmOutcome outcome = sim.Send(clock.UtcNow);
            Assert.Equal(policy.Limit - i, outcome.RemainingPermits);
        }

        Assert.Equal(0, sim.Send(clock.UtcNow).RemainingPermits);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Rejection_advises_a_positive_retry_delay(RateLimitAlgorithm kind)
    {
        // A rejected caller with no guidance retries immediately, so the moment a limit is hit
        // the rejected traffic becomes the dominant traffic. Every algorithm must say when.
        RateLimitPolicy policy = Policy(kind);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        for (int i = 0; i < policy.Limit; i++)
        {
            sim.Send(clock.UtcNow);
        }

        AlgorithmOutcome rejected = sim.Send(clock.UtcNow);

        Assert.False(rejected.IsAllowed);
        Assert.NotNull(rejected.RetryAfter);
        Assert.True(rejected.RetryAfter.Value > TimeSpan.Zero, $"retryAfter was {rejected.RetryAfter}");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Allowing_a_request_never_reports_a_retry_delay(RateLimitAlgorithm kind)
    {
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), Policy(kind));

        Assert.Null(sim.Send(new ManualClock().UtcNow).RetryAfter);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void A_limit_of_one_admits_one(RateLimitAlgorithm kind)
    {
        // The tightest boundary there is, and the one an off-by-one is most likely to break.
        RateLimitPolicy policy = Policy(kind, limit: 1);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        Assert.True(sim.Send(clock.UtcNow).IsAllowed);
        Assert.False(sim.Send(clock.UtcNow).IsAllowed);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void A_multi_permit_request_that_exceeds_the_remainder_is_refused_whole(RateLimitAlgorithm kind)
    {
        // Partial admission would be worse than refusal: the caller believes it was served and
        // the limiter has spent permits on a request that did not complete.
        RateLimitPolicy policy = Policy(kind);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        Assert.True(sim.Send(clock.UtcNow, permits: 3).IsAllowed);
        Assert.False(sim.Send(clock.UtcNow, permits: 3).IsAllowed);
        Assert.True(sim.Send(clock.UtcNow, permits: 2).IsAllowed);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void A_full_window_of_silence_restores_the_whole_allowance(RateLimitAlgorithm kind)
    {
        RateLimitPolicy policy = Policy(kind);
        Simulator sim = new(AlgorithmRegistry.Default.Resolve(kind), policy);
        ManualClock clock = new();

        for (int i = 0; i < policy.Limit; i++)
        {
            sim.Send(clock.UtcNow);
        }

        Assert.False(sim.Send(clock.UtcNow).IsAllowed);

        // Two windows, so this holds for the sliding algorithms too, which still remember the
        // previous window after only one has passed.
        clock.Advance(TimeSpan.FromSeconds(20));

        for (int i = 1; i <= policy.Limit; i++)
        {
            Assert.True(sim.Send(clock.UtcNow).IsAllowed, $"request {i} after idling should be admitted");
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Zero_or_negative_permits_are_rejected_as_a_programming_error(RateLimitAlgorithm kind)
    {
        IRateLimitAlgorithm algorithm = AlgorithmRegistry.Default.Resolve(kind);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => algorithm.Evaluate(LimiterState.Empty, Policy(kind), new ManualClock().UtcNow, 0));
    }
}
