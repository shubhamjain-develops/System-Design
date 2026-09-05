using RateLimiter.Core.Storage;

namespace RateLimiter.Demo;

/// <summary>
/// Wraps a store so the demo can make it fail on a keypress.
/// </summary>
/// <remarks>
/// Exists so the fail-open behaviour can be watched rather than described. The interesting thing
/// about a fail-open is that nothing looks wrong: requests keep succeeding, no error appears,
/// and only the store-failure counter moves. That is much easier to believe after seeing the
/// rejections stop and the counter climb at the same instant.
/// </remarks>
internal sealed class ToggleableStore(IRateLimitStore inner) : IRateLimitStore
{
    public bool IsFailing { get; set; }

    public ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.ReadAsync(key, cancellationToken);
    }

    public ValueTask<bool> TryWriteAsync(
        string key,
        long expectedVersion,
        LimiterState nextState,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.TryWriteAsync(key, expectedVersion, nextState, timeToLive, cancellationToken);
    }

    private void ThrowIfFailing()
    {
        if (IsFailing)
        {
            throw new TimeoutException("simulated cache outage");
        }
    }
}
