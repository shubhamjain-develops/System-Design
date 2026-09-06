namespace RateLimiter.Redis;

/// <summary>
/// The small slice of Redis this adapter needs.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a direct dependency on a client library, for two reasons. It keeps the
/// adapter testable without a running server, which is what lets the protocol and the
/// serialisation be exercised in an ordinary unit test. And it keeps the surface honest: three
/// methods is genuinely all a rate limiter needs from Redis, and stating that explicitly is more
/// informative than importing a client with several hundred.
/// </para>
/// <para>
/// Wiring this to StackExchange.Redis is one class: each method maps to one call. That
/// implementation is deliberately not included, because it could not be tested here and an
/// untested adapter to a live service is a liability rather than an asset.
/// </para>
/// </remarks>
public interface IRedisConnection
{
    /// <summary>
    /// Reads a string value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value, or <see langword="null"/> when the key does not exist.</returns>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a Lua script on the server.
    /// </summary>
    /// <param name="script">The script source.</param>
    /// <param name="keys">Keys the script operates on, becoming <c>KEYS</c>.</param>
    /// <param name="arguments">Arguments, becoming <c>ARGV</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The script's integer result.</returns>
    /// <remarks>
    /// Scripts matter here because Redis has no native compare-and-swap. The alternatives are
    /// WATCH/MULTI/EXEC, which is optimistic locking needing its own retry loop on top of the
    /// one the engine already runs, or a script, which is atomic by construction because Redis
    /// executes it without interleaving anything else. The script is the smaller of the two.
    /// </remarks>
    ValueTask<long> EvaluateAsync(
        string script,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the key existed.</returns>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
