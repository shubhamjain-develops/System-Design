using System.Globalization;
using RateLimiter.Core.Storage;

namespace RateLimiter.Redis;

/// <summary>
/// An <see cref="IRateLimitStore"/> backed by Redis.
/// </summary>
/// <remarks>
/// <para>
/// This is the class that makes the whole design worth its shape. Swapping
/// <c>InMemoryRateLimitStore</c> for this one turns a single-node limiter into a fleet-wide one,
/// and nothing else changes: no algorithm, no rule, no engine code. If that swap required
/// touching anything else, the store abstraction would have failed.
/// </para>
/// <para>
/// <strong>What is proven here and what is not.</strong> The protocol, the serialisation, the
/// version handling and the failure behaviour are exercised by tests against a fake connection.
/// The Lua itself is not: only a live Redis can execute Lua, so the compare-and-swap script's
/// correctness rests on reading it. That gap is real, it is why this slice carries the lowest
/// confidence score in the plan, and closing it needs an integration test against a container —
/// deliberately out of scope here, and the honest statement of the limit is more useful than a
/// fake that pretends otherwise.
/// </para>
/// <para>
/// <strong>Consistent hashing is not this class's problem.</strong> A Redis Cluster deployment
/// hashes by key, and every operation here is single-key, so one caller's state always lands on
/// one shard and the script's atomicity holds without any coordination. That property is a
/// consequence of the store interface being single-key, which is a reason it is worth the
/// interface being that narrow.
/// </para>
/// </remarks>
public sealed class RedisRateLimitStore : IRateLimitStore
{
    private readonly IRedisConnection _connection;
    private readonly string _keyPrefix;

    /// <summary>
    /// Creates a store over a connection.
    /// </summary>
    /// <param name="connection">The Redis connection.</param>
    /// <param name="keyPrefix">
    /// Prefixed to every key. Defaults to <c>rl:</c>. Worth setting when a Redis instance is
    /// shared, so that a limiter key cannot collide with another application's — a collision
    /// there is a corrupt entry for both.
    /// </param>
    public RedisRateLimitStore(IRedisConnection connection, string keyPrefix = "rl:")
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(keyPrefix);

        _connection = connection;
        _keyPrefix = keyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        string? stored = await _connection
            .GetAsync(_keyPrefix + key, cancellationToken)
            .ConfigureAwait(false);

        // A corrupt or unparseable value reads as absent. The caller then starts from a clean
        // state and the next admitted request rewrites the key, which is a better outcome than
        // failing every request this caller makes until someone deletes the key by hand.
        return LimiterStateSerializer.TryDeserialize(stored, out StoreEntry entry) ? entry : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryWriteAsync(
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

        string payload = LimiterStateSerializer.Serialize(nextState, expectedVersion + 1);

        // TTL is sent in milliseconds. Rounded up rather than truncated, because a TTL rounded
        // down to zero would be rejected by Redis, and one rounded down at all shortens a bound
        // that the algorithms treat as a correctness guarantee.
        long ttlMilliseconds = Math.Max(1, (long)Math.Ceiling(timeToLive.TotalMilliseconds));

        long result = await _connection.EvaluateAsync(
            RedisScripts.CompareAndSwap,
            [_keyPrefix + key],
            [
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                payload,
                ttlMilliseconds.ToString(CultureInfo.InvariantCulture),
            ],
            cancellationToken).ConfigureAwait(false);

        return result == 1;
    }

    /// <summary>
    /// Removes a key's state.
    /// </summary>
    /// <param name="key">The limiter key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the key existed.</returns>
    /// <remarks>
    /// Not part of <see cref="IRateLimitStore"/>, because no algorithm needs it. It exists for
    /// operators: clearing one caller's limiter state is a routine support action, and the
    /// alternative is waiting out a TTL.
    /// </remarks>
    public ValueTask<bool> ResetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _connection.DeleteAsync(_keyPrefix + key, cancellationToken);
    }
}
