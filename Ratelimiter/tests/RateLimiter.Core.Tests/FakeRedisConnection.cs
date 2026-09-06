using System.Globalization;
using RateLimiter.Redis;

namespace RateLimiter.Core.Tests;

/// <summary>
/// An in-process stand-in for Redis.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this does and does not establish.</strong> It reproduces the semantics of
/// <see cref="RedisScripts.CompareAndSwap"/> in C#, which lets the adapter's protocol,
/// serialisation, version handling and failure behaviour be tested without a server. It does
/// <em>not</em> execute the Lua, so it cannot show the script is correct — only that the adapter
/// uses it correctly. A fake that claimed more than that would be worse than none, because it
/// would convert an unverified assumption into an apparently green test.
/// </para>
/// <para>
/// The gap is closed only by running against a real Redis, which was deliberately excluded from
/// scope. The test that asserts the script text is the one being sent is the thin thread joining
/// what is tested here to what would run there.
/// </para>
/// </remarks>
internal sealed class FakeRedisConnection : IRedisConnection
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// The script source passed to the most recent evaluate call.
    /// </summary>
    public string? LastScript { get; private set; }

    /// <summary>
    /// Set to make every operation throw, standing in for an unreachable server.
    /// </summary>
    public Exception? Fault { get; set; }

    /// <summary>
    /// How many times any operation has been called.
    /// </summary>
    public int CallCount { get; private set; }

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Intercept();

        return ValueTask.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
    }

    public ValueTask<long> EvaluateAsync(
        string script,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        Intercept();
        LastScript = script;

        string key = keys[0];
        string expected = arguments[0];
        string payload = arguments[1];
        RecordTtl(arguments[2]);

        // Mirrors the Lua in RedisScripts.CompareAndSwap, including its treatment of an
        // unparseable stored value as absent.
        if (!_values.TryGetValue(key, out string? current))
        {
            if (expected != "0")
            {
                return ValueTask.FromResult(0L);
            }
        }
        else
        {
            int separator = current.IndexOf('|', StringComparison.Ordinal);

            if (separator < 0)
            {
                if (expected != "0")
                {
                    return ValueTask.FromResult(0L);
                }
            }
            else if (current[..separator] != expected)
            {
                return ValueTask.FromResult(0L);
            }
        }

        _values[key] = payload;
        return ValueTask.FromResult(1L);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Intercept();

        return ValueTask.FromResult(_values.Remove(key));
    }

    /// <summary>
    /// Writes a raw value, for testing how the adapter handles data it did not write.
    /// </summary>
    public void SetRaw(string key, string value) => _values[key] = value;

    /// <summary>
    /// Reads a raw value, to assert on the stored representation.
    /// </summary>
    public string? GetRaw(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// The TTL most recently sent, in milliseconds.
    /// </summary>
    public long LastTtlMilliseconds { get; private set; }

    private void Intercept()
    {
        CallCount++;

        if (Fault is not null)
        {
            throw Fault;
        }
    }

    internal void RecordTtl(string raw) =>
        LastTtlMilliseconds = long.Parse(raw, CultureInfo.InvariantCulture);
}
