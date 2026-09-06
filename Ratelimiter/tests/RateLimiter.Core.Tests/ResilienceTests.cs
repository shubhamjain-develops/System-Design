using RateLimiter.Core.Algorithms;
using RateLimiter.Core.Resilience;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// A store that fails on demand, standing in for an unreachable cache.
/// </summary>
internal sealed class FaultingStore(IRateLimitStore inner) : IRateLimitStore
{
    public bool ShouldFail { get; set; }

    public int ReadCount { get; private set; }

    public ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ReadCount++;

        if (ShouldFail)
        {
            throw new TimeoutException("the store is unreachable");
        }

        return inner.ReadAsync(key, cancellationToken);
    }

    public ValueTask<bool> TryWriteAsync(
        string key,
        long expectedVersion,
        LimiterState nextState,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            throw new TimeoutException("the store is unreachable");
        }

        return inner.TryWriteAsync(key, expectedVersion, nextState, timeToLive, cancellationToken);
    }
}

/// <summary>
/// Covers what happens when limiter state cannot be reached.
/// </summary>
public sealed class ResilienceTests
{
    private static RateLimitRule Rule() => new()
    {
        Name = "per-client",
        Priority = 1,
        Policy = new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            Limit = 2,
            Window = TimeSpan.FromSeconds(10),
        },
    };

    private static RequestContext Request() => new() { ClientId = "acct-1" };

    private static (RateLimitEngine Engine, FaultingStore Store, ManualClock Clock) Build(
        StoreFailurePolicy policy,
        CircuitBreaker? breaker = null)
    {
        ManualClock clock = new();
        FaultingStore store = new(new InMemoryRateLimitStore(clock));

        RateLimitEngine engine = new(
            new StaticRuleSource(Rule()),
            store,
            clock: clock,
            options: new RateLimiterOptions
            {
                OnStoreFailure = policy,
                CircuitBreaker = breaker,
            });

        return (engine, store, clock);
    }

    [Fact]
    public async Task Fail_open_admits_the_request_when_the_store_is_down()
    {
        (RateLimitEngine engine, FaultingStore store, _) = Build(StoreFailurePolicy.FailOpen);
        store.ShouldFail = true;

        RateLimitDecision decision = await engine.EvaluateAsync(Request());

        Assert.True(decision.IsAllowed);
        Assert.True(decision.StoreFailureOccurred);
    }

    [Fact]
    public async Task Fail_closed_refuses_the_request_when_the_store_is_down()
    {
        (RateLimitEngine engine, FaultingStore store, _) = Build(StoreFailurePolicy.FailClosed);
        store.ShouldFail = true;

        RateLimitDecision decision = await engine.EvaluateAsync(Request());

        Assert.False(decision.IsAllowed);
        Assert.True(decision.StoreFailureOccurred);
    }

    [Fact]
    public async Task Fail_closed_advises_a_retry_delay_so_refused_callers_do_not_stampede()
    {
        (RateLimitEngine engine, FaultingStore store, _) = Build(StoreFailurePolicy.FailClosed);
        store.ShouldFail = true;

        RateLimitDecision decision = await engine.EvaluateAsync(Request());

        Assert.NotNull(decision.RetryAfter);
        Assert.True(decision.RetryAfter.Value > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_fail_open_is_visible_in_the_metrics()
    {
        // The whole point. A limiter that has stopped limiting serves every request
        // successfully and reports nothing wrong — no error rate moves, no latency moves. This
        // counter is the only thing that distinguishes it from one that is working.
        (RateLimitEngine engine, FaultingStore store, _) = Build(StoreFailurePolicy.FailOpen);
        store.ShouldFail = true;

        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());

        Assert.Equal(2, engine.Metrics.StoreFailures);
        Assert.Equal(2, engine.Metrics.Allowed);
    }

    [Fact]
    public async Task Normal_decisions_are_counted_too()
    {
        (RateLimitEngine engine, _, _) = Build(StoreFailurePolicy.FailOpen);

        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());

        Assert.Equal(2, engine.Metrics.Allowed);
        Assert.Equal(1, engine.Metrics.Rejected);
        Assert.Equal(0, engine.Metrics.StoreFailures);
    }

    [Fact]
    public async Task A_programming_error_is_not_swallowed_as_a_store_failure()
    {
        // Failing open on a bug would hide it behind a limiter that appears to be working. An
        // unregistered algorithm is a configuration mistake and must surface as one.
        ManualClock clock = new();
        RateLimitEngine engine = new(
            new StaticRuleSource(Rule() with
            {
                Policy = new RateLimitPolicy
                {
                    Algorithm = RateLimitAlgorithm.TokenBucket,
                    Limit = 1,
                    Window = TimeSpan.FromSeconds(1),
                },
            }),
            new InMemoryRateLimitStore(clock),
            algorithms: new AlgorithmRegistry([new FixedWindowAlgorithm()]),
            clock: clock);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await engine.EvaluateAsync(Request()));
    }

    [Fact]
    public async Task Recovery_restores_normal_enforcement()
    {
        (RateLimitEngine engine, FaultingStore store, _) = Build(StoreFailurePolicy.FailOpen);

        store.ShouldFail = true;
        await engine.EvaluateAsync(Request());

        store.ShouldFail = false;

        // The limit is 2 and nothing was recorded during the outage, so enforcement resumes
        // from a clean slate rather than staying degraded.
        Assert.True((await engine.EvaluateAsync(Request())).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request())).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request())).IsAllowed);
    }

    // ---- Circuit breaker ----

    [Fact]
    public async Task The_breaker_stops_calling_a_failing_store()
    {
        // A timeout still pays the timeout. At a thousand requests per second, a 50ms timeout
        // means the limiter is holding fifty seconds of latency per second — the limiter becomes
        // the outage. The breaker converts that into a one-off cost.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 3, openDuration: TimeSpan.FromSeconds(5), clock: clock);
        (RateLimitEngine engine, FaultingStore store, _) = BuildWithClock(clock, StoreFailurePolicy.FailOpen, breaker);

        store.ShouldFail = true;

        for (int i = 0; i < 3; i++)
        {
            await engine.EvaluateAsync(Request());
        }

        int readsBefore = store.ReadCount;
        Assert.Equal(CircuitState.Open, breaker.State);

        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());

        // The store was not touched again.
        Assert.Equal(readsBefore, store.ReadCount);
        Assert.Equal(2, engine.Metrics.ShortCircuited);
    }

    [Fact]
    public async Task The_breaker_admits_one_trial_call_after_the_open_period()
    {
        // Letting every request through the instant the timer expires would slam a recovering
        // store with full production load, which is a common way to knock it back over.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(5), clock: clock);
        (RateLimitEngine engine, FaultingStore store, _) = BuildWithClock(clock, StoreFailurePolicy.FailOpen, breaker);

        store.ShouldFail = true;
        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());
        Assert.Equal(CircuitState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(5));
        store.ShouldFail = false;

        int readsBefore = store.ReadCount;
        await engine.EvaluateAsync(Request());

        Assert.True(store.ReadCount > readsBefore);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task A_failed_trial_reopens_the_breaker_without_a_fresh_run_of_failures()
    {
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(5), clock: clock);
        (RateLimitEngine engine, FaultingStore store, _) = BuildWithClock(clock, StoreFailurePolicy.FailOpen, breaker);

        store.ShouldFail = true;
        await engine.EvaluateAsync(Request());
        await engine.EvaluateAsync(Request());

        clock.Advance(TimeSpan.FromSeconds(5));
        await engine.EvaluateAsync(Request());

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void The_breaker_counts_consecutive_failures_not_cumulative_ones()
    {
        // A store failing one call in a thousand is not broken. A breaker counting cumulative
        // failures would eventually trip on any long-running process regardless of health.
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 3, clock: clock);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Closed, breaker.State);

        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void A_closed_breaker_lets_everything_through()
    {
        CircuitBreaker breaker = new(clock: new ManualClock());

        Assert.True(breaker.TryEnter());
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void An_open_breaker_permits_only_one_concurrent_trial()
    {
        ManualClock clock = new();
        CircuitBreaker breaker = new(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(5), clock: clock);

        breaker.RecordFailure();
        Assert.False(breaker.TryEnter());

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void The_breaker_rejects_a_non_positive_threshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(failureThreshold: 0));
    }

    private static (RateLimitEngine Engine, FaultingStore Store, ManualClock Clock) BuildWithClock(
        ManualClock clock,
        StoreFailurePolicy policy,
        CircuitBreaker? breaker)
    {
        FaultingStore store = new(new InMemoryRateLimitStore(clock));

        RateLimitEngine engine = new(
            new StaticRuleSource(Rule()),
            store,
            clock: clock,
            options: new RateLimiterOptions
            {
                OnStoreFailure = policy,
                CircuitBreaker = breaker,
            });

        return (engine, store, clock);
    }
}
