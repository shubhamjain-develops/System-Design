using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the compare-and-swap contract every algorithm depends on.
/// </summary>
/// <remarks>
/// These are the most load-bearing tests in the suite. The user-facing promise of a rate
/// limiter is that N is not exceeded, and under concurrency that promise reduces entirely to
/// whether this store loses updates. An algorithm can be flawless and still over-admit if a
/// read-modify-write can interleave.
/// </remarks>
public sealed class InMemoryRateLimitStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static LimiterState StateWithCount(long count) => new() { Count = count };

    /// <summary>
    /// Reads a key that the test requires to be present, failing the test if it is not.
    /// </summary>
    private static async Task<StoreEntry> ReadRequired(InMemoryRateLimitStore store, string key)
    {
        StoreEntry? entry = await store.ReadAsync(key);
        Assert.NotNull(entry);
        return entry.Value;
    }

    [Fact]
    public async Task Reading_an_unknown_key_returns_null()
    {
        InMemoryRateLimitStore store = new(new ManualClock());

        Assert.Null(await store.ReadAsync("absent"));
    }

    [Fact]
    public async Task First_write_expects_version_zero_and_produces_version_one()
    {
        // Version 0 is how "I believe this key is absent" is expressed. It has to be a real
        // version rather than a null, so that two concurrent first writes can be adjudicated.
        InMemoryRateLimitStore store = new(new ManualClock());

        Assert.True(await store.TryWriteAsync("k", expectedVersion: 0, StateWithCount(1), Ttl));

        StoreEntry entry = await ReadRequired(store, "k");
        Assert.Equal(1, entry.Version);
        Assert.Equal(1, entry.State.Count);
    }

    [Fact]
    public async Task Second_write_at_version_zero_is_rejected()
    {
        InMemoryRateLimitStore store = new(new ManualClock());
        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);

        Assert.False(await store.TryWriteAsync("k", 0, StateWithCount(99), Ttl));

        StoreEntry entry = await ReadRequired(store, "k");
        Assert.Equal(1, entry.State.Count);
    }

    [Fact]
    public async Task Write_at_the_read_version_succeeds_and_bumps_the_version()
    {
        InMemoryRateLimitStore store = new(new ManualClock());
        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);

        Assert.True(await store.TryWriteAsync("k", 1, StateWithCount(2), Ttl));

        StoreEntry entry = await ReadRequired(store, "k");
        Assert.Equal(2, entry.Version);
        Assert.Equal(2, entry.State.Count);
    }

    [Fact]
    public async Task Write_at_a_stale_version_is_rejected()
    {
        // The whole point of the interface: a caller that computed from state it read at
        // version 1 must not be able to overwrite version 2.
        InMemoryRateLimitStore store = new(new ManualClock());
        await store.TryWriteAsync("k", 0, StateWithCount(1), Ttl);
        await store.TryWriteAsync("k", 1, StateWithCount(2), Ttl);

        Assert.False(await store.TryWriteAsync("k", 1, StateWithCount(50), Ttl));

        StoreEntry entry = await ReadRequired(store, "k");
        Assert.Equal(2, entry.State.Count);
    }

    [Fact]
    public async Task Expired_entry_reads_as_absent()
    {
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(await store.ReadAsync("k"));
    }

    [Fact]
    public async Task Entry_is_still_live_one_tick_before_expiry()
    {
        // The boundary itself, pinned. TTL handling is exactly the kind of comparison where an
        // off-by-one hands out a free window.
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));

        Assert.NotNull(await store.ReadAsync("k"));
    }

    [Fact]
    public async Task Expired_entry_can_be_replaced_by_a_first_write()
    {
        // An expired entry is logically absent, so a create-if-not-exists must win against it.
        // Were this to fail, a key would become permanently unwritable once its TTL lapsed.
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.True(await store.TryWriteAsync("k", 0, StateWithCount(7), Ttl));

        StoreEntry entry = await ReadRequired(store, "k");
        Assert.Equal(7, entry.State.Count);
    }

    [Fact]
    public async Task Write_at_a_live_version_fails_once_the_entry_has_expired()
    {
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(await store.TryWriteAsync("k", 1, StateWithCount(2), Ttl));
    }

    [Fact]
    public async Task RemoveExpired_reclaims_only_expired_entries()
    {
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        await store.TryWriteAsync("short", 0, StateWithCount(1), TimeSpan.FromSeconds(5));
        await store.TryWriteAsync("long", 0, StateWithCount(1), TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(10));
        int removed = store.RemoveExpired();

        Assert.Equal(1, removed);
        Assert.Equal(1, store.Count);
        Assert.NotNull(await store.ReadAsync("long"));
    }

    [Fact]
    public async Task Store_does_not_grow_without_bound_as_keys_churn()
    {
        // The memory-exhaustion vector: one request per new key. The automatic sweep is tied to
        // write volume, so this asserts the residue is collected rather than accumulated.
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);

        for (int i = 0; i < 5_000; i++)
        {
            await store.TryWriteAsync($"key-{i}", 0, StateWithCount(1), TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.True(store.Count < 5_000, $"expected sweeping to bound growth, saw {store.Count} entries");
    }

    [Fact]
    public async Task Ttl_must_be_positive()
    {
        InMemoryRateLimitStore store = new(new ManualClock());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.TryWriteAsync("k", 0, StateWithCount(1), TimeSpan.Zero));
    }

    [Fact]
    public async Task Negative_expected_version_is_rejected()
    {
        InMemoryRateLimitStore store = new(new ManualClock());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.TryWriteAsync("k", -1, StateWithCount(1), Ttl));
    }

    [Fact]
    public async Task Concurrent_read_modify_write_loses_no_updates()
    {
        // This is the test the whole abstraction exists for. Every task performs the same
        // read-compute-CAS-retry an algorithm performs. If the store's compare-and-swap is not
        // genuinely atomic, increments are lost and the final count comes in under the number
        // of writers — which in production is a limiter admitting more than its limit.
        InMemoryRateLimitStore store = new(new ManualClock());
        const int writers = 64;
        const int incrementsPerWriter = 50;
        const string key = "hot";

        async Task Increment()
        {
            for (int i = 0; i < incrementsPerWriter; i++)
            {
                while (true)
                {
                    StoreEntry? current = await store.ReadAsync(key);
                    long version = current?.Version ?? 0;
                    long nextCount = (current?.State.Count ?? 0) + 1;

                    if (await store.TryWriteAsync(key, version, StateWithCount(nextCount), Ttl))
                    {
                        break;
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(Increment)));

        StoreEntry entry = await ReadRequired(store, key);
        Assert.Equal(writers * incrementsPerWriter, entry.State.Count);
    }

    [Fact]
    public async Task Concurrent_first_writes_admit_exactly_one_winner()
    {
        // Version 0 has to adjudicate a create race, not merely express intent. Exactly one
        // caller may believe it created the key.
        InMemoryRateLimitStore store = new(new ManualClock());
        const int racers = 32;

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, racers)
                .Select(i => Task.Run(async () => await store.TryWriteAsync("race", 0, StateWithCount(i), Ttl))));

        Assert.Equal(1, results.Count(won => won));
    }
}
