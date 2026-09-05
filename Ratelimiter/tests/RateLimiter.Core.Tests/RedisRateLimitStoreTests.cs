using RateLimiter.Core;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;
using RateLimiter.Redis;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the Redis adapter's protocol and serialisation against a fake connection.
/// </summary>
/// <remarks>
/// The Lua is not executed here and these tests do not claim it is correct. What they establish
/// is that the adapter satisfies the same store contract as the in-memory implementation, which
/// is what makes the two interchangeable.
/// </remarks>
public sealed class RedisRateLimitStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static LimiterState StateWithCount(long count) => new() { Count = count };

    [Fact]
    public async Task Reading_an_unknown_key_returns_null()
    {
        RedisRateLimitStore store = new(new FakeRedisConnection());

        Assert.Null(await store.ReadAsync("absent"));
    }

    [Fact]
    public async Task A_first_write_expects_version_zero_and_stores_version_one()
    {
        RedisRateLimitStore store = new(new FakeRedisConnection());

        Assert.True(await store.TryWriteAsync("k", 0, StateWithCount(3), Ttl));

        StoreEntry? entry = await store.ReadAsync("k");
        Assert.NotNull(entry);
        Assert.Equal(1, entry.Value.Version);
        Assert.Equal(3, entry.Value.State.Count);
    }

    [Fact]
    public async Task A_stale_version_is_refused()
    {
        RedisRateLimitStore store = new(new FakeRedisConnection());
        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);
        await store.TryWriteAsync("k", 1, StateWithCount(2), Ttl);

        Assert.False(await store.TryWriteAsync("k", 1, StateWithCount(99), Ttl));
    }

    [Fact]
    public async Task The_compare_and_swap_script_is_what_gets_sent()
    {
        // The thin thread joining what is tested here to what would run against a real server.
        // If the adapter ever sent a different script, everything else in this file would still
        // pass and production would behave differently.
        FakeRedisConnection connection = new();
        RedisRateLimitStore store = new(connection);

        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);

        Assert.Equal(RedisScripts.CompareAndSwap, connection.LastScript);
    }

    [Fact]
    public async Task Keys_are_prefixed_so_a_shared_redis_cannot_collide()
    {
        FakeRedisConnection connection = new();
        RedisRateLimitStore store = new(connection, keyPrefix: "myapp:");

        await store.TryWriteAsync("client-1", 0, StateWithCount(1), Ttl);

        Assert.NotNull(connection.GetRaw("myapp:client-1"));
    }

    [Fact]
    public async Task Ttl_is_sent_in_milliseconds_and_rounded_up()
    {
        // Rounding down could truncate a sub-millisecond TTL to zero, which Redis rejects, and
        // any rounding down shortens a bound the algorithms treat as a correctness guarantee.
        FakeRedisConnection connection = new();
        RedisRateLimitStore store = new(connection);

        await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.FromTicks(1));

        Assert.Equal(1, connection.LastTtlMilliseconds);
    }

    [Fact]
    public async Task The_stored_value_carries_the_version_as_a_readable_prefix()
    {
        // The script compares the version with a substring rather than parsing JSON in Lua, so
        // the layout is load-bearing rather than cosmetic.
        FakeRedisConnection connection = new();
        RedisRateLimitStore store = new(connection);

        await store.TryWriteAsync("k", 0, StateWithCount(7), Ttl);

        string raw = connection.GetRaw("rl:k")!;
        Assert.StartsWith("1|", raw, StringComparison.Ordinal);
        Assert.Contains("\"C\":7", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("garbage-with-no-separator")]
    [InlineData("notanumber|{}")]
    [InlineData("1|{not json")]
    [InlineData("")]
    public async Task A_corrupt_value_reads_as_absent_rather_than_throwing(string corrupt)
    {
        // A partial write or a key colliding with another application must degrade to "this
        // caller has no history", not to an exception on every request they make.
        FakeRedisConnection connection = new();
        connection.SetRaw("rl:k", corrupt);
        RedisRateLimitStore store = new(connection);

        Assert.Null(await store.ReadAsync("k"));
    }

    [Fact]
    public async Task A_corrupt_value_can_be_overwritten_by_a_first_write()
    {
        // Otherwise a single corrupt entry would make that caller's key permanently unwritable.
        FakeRedisConnection connection = new();
        connection.SetRaw("rl:k", "garbage");
        RedisRateLimitStore store = new(connection);

        Assert.True(await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl));
    }

    [Fact]
    public async Task Sliding_window_log_timestamps_survive_a_round_trip()
    {
        // The state shape that stresses the store abstraction hardest: a list rather than a
        // scalar. If serialisation dropped it, the exact algorithm would silently become
        // unlimited.
        RedisRateLimitStore store = new(new FakeRedisConnection());
        LimiterState state = new() { Timestamps = [1000, 2000, 3000] };

        await store.TryWriteAsync("k", 0, state, Ttl);
        StoreEntry? entry = await store.ReadAsync("k");

        Assert.NotNull(entry);
        Assert.Equal([1000L, 2000L, 3000L], entry.Value.State.Timestamps.ToArray().AsEnumerable());
    }

    [Fact]
    public async Task Fractional_token_counts_survive_a_round_trip()
    {
        // Truncating this to an integer would stall every bucket refilling slower than one
        // permit per second.
        RedisRateLimitStore store = new(new FakeRedisConnection());

        await store.TryWriteAsync("k", 0, new LimiterState { Tokens = 0.25, LastUpdatedTicks = 99 }, Ttl);
        StoreEntry? entry = await store.ReadAsync("k");

        Assert.NotNull(entry);
        Assert.Equal(0.25, entry.Value.State.Tokens, precision: 10);
    }

    [Fact]
    public async Task Store_failures_surface_to_the_caller()
    {
        // The adapter does not swallow them. Deciding what an unreachable store means is the
        // engine's failure policy, not the store's.
        FakeRedisConnection connection = new() { Fault = new InvalidOperationException("connection refused") };
        RedisRateLimitStore store = new(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReadAsync("k"));
    }

    [Fact]
    public async Task Reset_removes_a_callers_state()
    {
        FakeRedisConnection connection = new();
        RedisRateLimitStore store = new(connection);
        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);

        Assert.True(await store.ResetAsync("k"));
        Assert.Null(await store.ReadAsync("k"));
    }

    [Fact]
    public async Task The_engine_behaves_identically_over_redis_and_over_memory()
    {
        // The claim the whole design rests on: swapping the store changes nothing else. Both
        // engines are given the same rule, the same clock and the same traffic, and must reach
        // the same decisions.
        ManualClock clock = new();
        RateLimitRule rule = new()
        {
            Name = "per-client",
            Priority = 1,
            Policy = new RateLimitPolicy
            {
                Algorithm = RateLimitAlgorithm.TokenBucket,
                Limit = 4,
                Window = TimeSpan.FromSeconds(10),
            },
        };

        RateLimitEngine overMemory = new(
            new StaticRuleSource(rule), new InMemoryRateLimitStore(clock), clock: clock);
        RateLimitEngine overRedis = new(
            new StaticRuleSource(rule), new RedisRateLimitStore(new FakeRedisConnection()), clock: clock);

        RequestContext request = new() { ClientId = "acct-1" };

        for (int i = 0; i < 8; i++)
        {
            RateLimitDecision memory = await overMemory.EvaluateAsync(request);
            RateLimitDecision redis = await overRedis.EvaluateAsync(request);

            Assert.Equal(memory.IsAllowed, redis.IsAllowed);
            Assert.Equal(memory.RemainingPermits, redis.RemainingPermits);

            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
