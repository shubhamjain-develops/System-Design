using RateLimiter.Core.Algorithms;
using RateLimiter.Core.Resilience;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Regression tests for defects found by security review of the composed branch.
/// </summary>
/// <remarks>
/// Both were found by asking the question the mechanical floor checks do not: not "is there a
/// secret in the diff" but "what does an attacker-influenced value do to this code path". Both
/// are availability defects in a component whose failure mode is that it silently stops limiting.
/// </remarks>
public sealed class SecurityReviewTests
{
    // ---- Finding 1: integer overflow in the sliding window log ----

    [Fact]
    public void A_huge_permit_count_is_refused_rather_than_overflowing()
    {
        // The permits argument is supplied by the host, which may derive it from request content
        // — a batch endpoint charging one permit per item is the obvious case, and then the value
        // is attacker-influenced.
        //
        // SlidingWindowLog compared `retained.Length + permits > policy.Limit` in INT arithmetic.
        // At permits near int.MaxValue that sum overflows negative, the comparison reads as
        // "under the limit", and the request is admitted onto a path that then allocates a
        // builder with negative capacity and throws. The engine deliberately does not absorb
        // ArgumentException, so it propagates as an unhandled exception per request.
        //
        // Every other algorithm was safe: their comparisons promote to long or double.
        RateLimitPolicy policy = new()
        {
            Algorithm = RateLimitAlgorithm.SlidingWindowLog,
            Limit = 5,
            Window = TimeSpan.FromSeconds(10),
        };

        IRateLimitAlgorithm algorithm = AlgorithmRegistry.Default.Resolve(RateLimitAlgorithm.SlidingWindowLog);

        AlgorithmOutcome outcome = algorithm.Evaluate(
            LimiterState.Empty, policy, new ManualClock().UtcNow, int.MaxValue);

        Assert.False(outcome.IsAllowed);
    }

    [Theory]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindowLog)]
    [InlineData(RateLimitAlgorithm.SlidingWindowCounter)]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.LeakyBucket)]
    public void No_algorithm_overflows_on_an_extreme_permit_count(RateLimitAlgorithm kind)
    {
        // Asserted across all five so a sixth algorithm cannot reintroduce the class of defect.
        RateLimitPolicy policy = new()
        {
            Algorithm = kind,
            Limit = 5,
            Window = TimeSpan.FromSeconds(10),
        };

        IRateLimitAlgorithm algorithm = AlgorithmRegistry.Default.Resolve(kind);

        foreach (int permits in new[] { int.MaxValue, int.MaxValue - 1, 1_000_000_000 })
        {
            AlgorithmOutcome outcome = algorithm.Evaluate(
                LimiterState.Empty, policy, new ManualClock().UtcNow, permits);

            Assert.False(outcome.IsAllowed, $"{kind} admitted {permits} permits against a limit of 5");
        }
    }

    [Fact]
    public async Task An_extreme_permit_count_does_not_throw_through_the_engine()
    {
        ManualClock clock = new();
        RateLimitEngine engine = new(
            new StaticRuleSource(new RateLimitRule
            {
                Name = "log",
                Priority = 1,
                Policy = new RateLimitPolicy
                {
                    Algorithm = RateLimitAlgorithm.SlidingWindowLog,
                    Limit = 5,
                    Window = TimeSpan.FromSeconds(10),
                },
            }),
            new InMemoryRateLimitStore(clock),
            clock: clock);

        RateLimitDecision decision = await engine.EvaluateAsync(
            new RequestContext { ClientId = "acct-1" }, int.MaxValue);

        Assert.False(decision.IsAllowed);
    }

    // ---- Finding 2: a cancelled request could wedge the circuit breaker ----

    [Fact]
    public void An_abandoned_trial_does_not_wedge_the_breaker_permanently()
    {
        // The breaker permits exactly one trial request while half-open, and that slot was
        // released only by RecordSuccess or RecordFailure. Any path leaving without calling
        // either — a cancellation, or an exception type the engine deliberately does not absorb
        // — left the slot held forever.
        //
        // The consequence is worse than it first looks. A wedged breaker refuses every
        // subsequent TryEnter, so every request routes through the failure policy, and under the
        // default FailOpen that means the limiter stops limiting PERMANENTLY, silently, from one
        // cancelled request.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(5), clock: clock);

        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(5));

        // A trial is taken and then abandoned without an outcome.
        Assert.True(breaker.TryEnter());
        breaker.AbandonTrial();

        // The breaker must still be usable: another trial can be taken.
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void Abandoning_a_trial_does_not_count_as_success_or_failure()
    {
        // A cancelled request is not evidence about the store's health in either direction.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(5), clock: clock);

        breaker.RecordFailure();
        breaker.AbandonTrial();

        // One failure recorded, threshold is two, so the breaker must still be closed — the
        // abandon neither tripped it nor reset the count.
        Assert.Equal(CircuitState.Closed, breaker.State);

        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task A_request_cancelled_during_the_half_open_trial_does_not_disable_the_limiter()
    {
        // The end-to-end form of finding 2, and the ordering matters. Cancelling while the
        // breaker is CLOSED proves nothing, because no trial slot is outstanding — an earlier
        // version of this test made exactly that mistake and passed against the unfixed code.
        //
        // The breaker has to be half-open, so the cancelled request is the one holding the single
        // trial slot. Before the fix that slot was never returned, every later request
        // short-circuited, and under fail-open the limiter admitted everything forever.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(5), clock: clock);
        FaultyStore store = new(new InMemoryRateLimitStore(clock));

        RateLimitEngine engine = new(
            new StaticRuleSource(new RateLimitRule
            {
                Name = "r",
                Priority = 1,
                Policy = new RateLimitPolicy
                {
                    Algorithm = RateLimitAlgorithm.FixedWindow,
                    Limit = 2,
                    Window = TimeSpan.FromSeconds(10),
                },
            }),
            store,
            clock: clock,
            options: new RateLimiterOptions { CircuitBreaker = breaker });

        RequestContext request = new() { ClientId = "acct-1" };

        // Trip the breaker.
        store.Mode = FaultMode.Throw;
        await engine.EvaluateAsync(request);
        Assert.Equal(CircuitState.Open, breaker.State);

        // Wait it out, then let the trial request be cancelled.
        clock.Advance(TimeSpan.FromSeconds(5));
        store.Mode = FaultMode.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await engine.EvaluateAsync(request));

        // The store recovers. Enforcement must resume — not every request falling through the
        // failure policy forever.
        store.Mode = FaultMode.Healthy;

        RateLimitDecision first = await engine.EvaluateAsync(request);
        Assert.True(first.IsAllowed);
        Assert.False(first.StoreFailureOccurred);

        Assert.True((await engine.EvaluateAsync(request)).IsAllowed);

        // And the limit is genuinely being enforced again, not merely appearing to allow.
        RateLimitDecision third = await engine.EvaluateAsync(request);
        Assert.False(third.IsAllowed);
        Assert.False(third.StoreFailureOccurred);
    }

    private enum FaultMode
    {
        Healthy,
        Throw,
        Cancel,
    }

    private sealed class FaultyStore(IRateLimitStore inner) : IRateLimitStore
    {
        public FaultMode Mode { get; set; } = FaultMode.Healthy;

        public ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Intercept();
            return inner.ReadAsync(key, cancellationToken);
        }

        public ValueTask<bool> TryWriteAsync(
            string key,
            long expectedVersion,
            LimiterState nextState,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default)
        {
            Intercept();
            return inner.TryWriteAsync(key, expectedVersion, nextState, timeToLive, cancellationToken);
        }

        private void Intercept()
        {
            if (Mode == FaultMode.Throw)
            {
                throw new TimeoutException("the store is unreachable");
            }

            if (Mode == FaultMode.Cancel)
            {
                throw new OperationCanceledException();
            }
        }
    }
}
