namespace RateLimiter.Core.Storage;

/// <summary>
/// A stored <see cref="LimiterState"/> together with the version it was read at.
/// </summary>
/// <param name="State">The accounting state for the key.</param>
/// <param name="Version">
/// The version this state was read at. Passing it back to
/// <see cref="IRateLimitStore.TryWriteAsync"/> is what makes the update atomic: the write
/// succeeds only if nothing else has written since.
/// </param>
public readonly record struct StoreEntry(LimiterState State, long Version);

/// <summary>
/// The backing store limiter state is read from and written to.
/// </summary>
/// <remarks>
/// <para>
/// Two methods, both algorithm-agnostic, and the narrowness is the entire design.
/// </para>
/// <para>
/// The interface deliberately does not expose <c>Increment</c>, <c>AddToSortedSet</c>, or any
/// other operation an individual algorithm would enjoy having. Those are algorithm concerns,
/// and a store that grew a method per algorithm would not be an abstraction — it would be a
/// list of five backends wearing one interface, and adding a sixth algorithm would mean
/// changing every backend that already exists.
/// </para>
/// <para>
/// What remains is optimistic concurrency: read a state and its version, compute the next
/// state, and write it back only if the version still holds. Every algorithm here is a pure
/// function of the state it read, so read-compute-swap is sufficient for all five, and the
/// retry on a lost race is the caller's business rather than the store's.
/// </para>
/// <para>
/// <strong>The cost, stated plainly.</strong> Under contention on a single hot key, CAS
/// retries burn round trips that a server-side script would not — a Redis Lua script does the
/// whole read-modify-write in one. That is a real weakness of this choice and not a detail:
/// it is the reason <c>RateLimiter.Redis</c> carries the token-bucket Lua script alongside its
/// CAS implementation, documenting the escape hatch and the traffic level at which taking it
/// becomes correct. The trade accepted here is that a pure-function algorithm is testable
/// exhaustively and offline, and boundary conditions are where the bugs actually are.
/// </para>
/// <para>
/// <strong>TTL is a parameter, not an option.</strong> Every write must state when its entry
/// may be reclaimed, because per-key state with no expiry is a memory-exhaustion vector — one
/// request per new key is all it takes. Making it a required argument means a backend author
/// cannot forget it. Note that the TTL is a memory backstop and never the source of
/// correctness: the window arithmetic lives in the stored timestamps, so an entry expiring
/// early costs a caller nothing worse than a fresh allowance.
/// </para>
/// </remarks>
public interface IRateLimitStore
{
    /// <summary>
    /// Reads the state for a key.
    /// </summary>
    /// <param name="key">The limiter key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The stored entry, or <see langword="null"/> when the key is absent or expired. An
    /// expired entry is indistinguishable from an absent one by design — expiry is reclamation,
    /// not a state an algorithm should have to reason about.
    /// </returns>
    ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a new state for a key, but only if the stored version still matches.
    /// </summary>
    /// <param name="key">The limiter key.</param>
    /// <param name="expectedVersion">
    /// The version previously read, or <c>0</c> to assert the key is currently absent. Zero is
    /// how a create-if-not-exists is expressed, so the first write of a key races safely
    /// against every other first write of that key.
    /// </param>
    /// <param name="nextState">The state to store.</param>
    /// <param name="timeToLive">How long the entry may live before it may be reclaimed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// <see langword="true"/> when the write was applied; <see langword="false"/> when another
    /// writer got there first, in which case the caller should re-read and retry.
    /// </returns>
    /// <remarks>
    /// A <see langword="false"/> result is an ordinary outcome under concurrency, not an error,
    /// which is why it is a return value rather than an exception. Exceptions are reserved for
    /// the store being genuinely unavailable — the case the failure policy exists to handle.
    /// </remarks>
    ValueTask<bool> TryWriteAsync(
        string key,
        long expectedVersion,
        LimiterState nextState,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);
}
