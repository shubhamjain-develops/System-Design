using System.Collections.Concurrent;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Storage;

/// <summary>
/// An in-process <see cref="IRateLimitStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// <para>
/// Suitable for a single node, for tests, and for the demo. It is emphatically not suitable
/// for a fleet: each process holds its own state, so N nodes enforce N times the intended
/// limit. That is the failure this library's whole shape exists to make visible — the store is
/// the thing you swap, and swapping it is the only change required.
/// </para>
/// <para>
/// The compare-and-swap is real rather than approximated. Entries are immutable, so
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryUpdate(TKey, TValue, TValue)"/> comparing
/// by reference is exactly a CAS on the entry: it succeeds only if the entry object present is
/// the one that was read. A lock per key would also work and would be easier to read, but it
/// would make the in-memory store the odd one out — no distributed backend can hold a lock
/// across a network call, so the lock-based version would be testing a concurrency model that
/// no other implementation could reproduce.
/// </para>
/// </remarks>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    /// <summary>
    /// Successful writes between opportunistic sweeps of expired entries.
    /// </summary>
    /// <remarks>
    /// Sweeping is tied to write volume rather than to a timer. A timer would need a
    /// background thread and disposal, and would fire pointlessly on an idle process; write
    /// volume is self-scaling, because the only way to accumulate expired entries is to have
    /// written them. If writes stop, the residue stops growing too, so the ceiling is the peak
    /// live key count rather than unbounded growth.
    /// </remarks>
    private const int SweepInterval = 1024;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private int _writesSinceSweep;
    private int _sweepInProgress;

    /// <summary>
    /// Creates a store using the given clock for expiry.
    /// </summary>
    /// <param name="clock">
    /// The clock used to evaluate TTLs. Injected rather than assumed so that a test can expire
    /// an entry by advancing time instead of by waiting for it.
    /// </param>
    public InMemoryRateLimitStore(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Creates a store using the system clock.
    /// </summary>
    public InMemoryRateLimitStore()
        : this(SystemClock.Instance)
    {
    }

    /// <summary>
    /// The number of entries currently held, including any that have expired but not yet been
    /// reclaimed.
    /// </summary>
    /// <remarks>
    /// Exposed so that eviction can be asserted in a test. Without it, "the store does not grow
    /// without bound" would be a claim rather than a check.
    /// </remarks>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key, out Entry? entry) || IsExpired(entry))
        {
            return ValueTask.FromResult<StoreEntry?>(null);
        }

        return ValueTask.FromResult<StoreEntry?>(new StoreEntry(entry.State, entry.Version));
    }

    /// <inheritdoc />
    public ValueTask<bool> TryWriteAsync(
        string key,
        long expectedVersion,
        LimiterState nextState,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nextState);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        long expiresAt = _clock.UtcNow.UtcTicks + timeToLive.Ticks;

        while (true)
        {
            if (_entries.TryGetValue(key, out Entry? current))
            {
                // An expired entry is logically absent, so a create-if-not-exists write must be
                // allowed to replace it. Reading its version instead would make a first write
                // fail against a corpse.
                long currentVersion = IsExpired(current) ? 0 : current.Version;

                if (currentVersion != expectedVersion)
                {
                    return ValueTask.FromResult(false);
                }

                Entry replacement = new(nextState, currentVersion + 1, expiresAt);

                // Reference comparison against the exact entry just read: this is the CAS.
                if (_entries.TryUpdate(key, replacement, current))
                {
                    OnWritten();
                    return ValueTask.FromResult(true);
                }

                // Someone replaced the entry between the read and the swap. Re-read and
                // re-evaluate rather than assuming the loser lost on version — the new entry
                // may itself be expired, in which case this write can still legitimately win.
                continue;
            }

            if (expectedVersion != 0)
            {
                // Caller expected an existing version; the key is gone. Whether it expired or
                // was never written does not matter — the state it computed from is stale.
                return ValueTask.FromResult(false);
            }

            if (_entries.TryAdd(key, new Entry(nextState, 1, expiresAt)))
            {
                OnWritten();
                return ValueTask.FromResult(true);
            }

            // Lost a race to create. Loop to find out whether the winner's entry is live (we
            // lose) or already expired (we may still win).
        }
    }

    /// <summary>
    /// Removes every entry whose time to live has elapsed.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    /// <remarks>
    /// Runs automatically as writes accumulate; exposed publicly so a test can force it rather
    /// than having to issue a thousand writes to trigger it.
    /// </remarks>
    public int RemoveExpired()
    {
        long now = _clock.UtcNow.UtcTicks;
        int removed = 0;

        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (pair.Value.ExpiresAtTicks > now)
            {
                continue;
            }

            // Remove only if the entry is still the expired one observed. A concurrent writer
            // may have refreshed this key since the scan reached it, and reclaiming that live
            // entry would hand its owner a fresh allowance.
            if (_entries.TryRemove(KeyValuePair.Create(pair.Key, pair.Value)))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool IsExpired(Entry entry) => entry.ExpiresAtTicks <= _clock.UtcNow.UtcTicks;

    private void OnWritten()
    {
        if (Interlocked.Increment(ref _writesSinceSweep) < SweepInterval)
        {
            return;
        }

        // One sweeper at a time. A second thread arriving mid-sweep skips rather than queues:
        // the work is idempotent and unurgent, so contending for it would cost more than the
        // delay of waiting for the next interval.
        if (Interlocked.CompareExchange(ref _sweepInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _writesSinceSweep, 0);
            RemoveExpired();
        }
        finally
        {
            Interlocked.Exchange(ref _sweepInProgress, 0);
        }
    }

    /// <summary>
    /// An immutable snapshot of one key's stored state. Immutability is what allows the
    /// dictionary's reference comparison to serve as a compare-and-swap.
    /// </summary>
    private sealed class Entry(LimiterState state, long version, long expiresAtTicks)
    {
        public LimiterState State { get; } = state;

        public long Version { get; } = version;

        public long ExpiresAtTicks { get; } = expiresAtTicks;
    }
}
