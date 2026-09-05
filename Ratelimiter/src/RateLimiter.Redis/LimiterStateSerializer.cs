using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RateLimiter.Core.Storage;

namespace RateLimiter.Redis;

/// <summary>
/// Converts <see cref="LimiterState"/> to and from the string stored in Redis.
/// </summary>
/// <remarks>
/// <para>
/// The stored form is <c>&lt;version&gt;|&lt;json&gt;</c>. Putting the version in a plain prefix
/// rather than inside the JSON is what lets the compare-and-swap script check it with a
/// substring instead of parsing structured data in Lua, which is where server-side scripts stop
/// being cheap and start being hard to reason about.
/// </para>
/// <para>
/// The payload itself is JSON because a stored limiter entry is something an operator will read
/// during an incident. <c>GET</c> on the key should answer "how many permits does this caller
/// have left" without a decoder ring. A packed binary format would be smaller and would cost
/// exactly the thing the format is most needed for.
/// </para>
/// <para>
/// Serialisation lives in this project, not in the core. Which backend needs what encoding is a
/// backend's concern, and keeping it here is what allows the core to stay free of any
/// serialisation dependency at all.
/// </para>
/// </remarks>
public static class LimiterStateSerializer
{
    private const char VersionSeparator = '|';

    private static readonly JsonSerializerOptions Options = new()
    {
        // Compact: this is written on every admitted request.
        WriteIndented = false,
    };

    /// <summary>
    /// Renders a state and version for storage.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="version">The version being written.</param>
    /// <returns>The stored representation.</returns>
    public static string Serialize(LimiterState state, long version)
    {
        ArgumentNullException.ThrowIfNull(state);

        StringBuilder builder = new();
        builder.Append(version.ToString(CultureInfo.InvariantCulture));
        builder.Append(VersionSeparator);
        builder.Append(JsonSerializer.Serialize(Persisted.From(state), Options));

        return builder.ToString();
    }

    /// <summary>
    /// Parses a stored representation.
    /// </summary>
    /// <param name="stored">The value read from Redis.</param>
    /// <param name="entry">The parsed entry.</param>
    /// <returns><see langword="true"/> when the value could be parsed.</returns>
    /// <remarks>
    /// Returns false rather than throwing on a malformed value. A corrupt entry — a partial
    /// write, or a key colliding with something else in the same Redis — must degrade to
    /// "this caller has no history", not to an exception on every request they make. The key is
    /// then rewritten from scratch by the next admitted request.
    /// </remarks>
    public static bool TryDeserialize(string? stored, out StoreEntry entry)
    {
        entry = default;

        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        int separator = stored.IndexOf(VersionSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == stored.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                stored.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long version))
        {
            return false;
        }

        Persisted? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Persisted>(stored[(separator + 1)..], Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null)
        {
            return false;
        }

        entry = new StoreEntry(payload.ToState(), version);
        return true;
    }

    /// <summary>
    /// The JSON shape, named separately from <see cref="LimiterState"/> so the wire format is a
    /// deliberate choice rather than a shadow of the core's property names.
    /// </summary>
    private sealed record Persisted
    {
        public long W { get; init; }

        public long C { get; init; }

        public long P { get; init; }

        public double T { get; init; }

        public long U { get; init; }

        public long[]? L { get; init; }

        public static Persisted From(LimiterState state) => new()
        {
            W = state.WindowStartTicks,
            C = state.Count,
            P = state.PreviousCount,
            T = state.Tokens,
            U = state.LastUpdatedTicks,

            // Omitted entirely when empty, which is the case for four of the five algorithms.
            L = state.Timestamps.IsDefaultOrEmpty ? null : [.. state.Timestamps],
        };

        public LimiterState ToState() => new()
        {
            WindowStartTicks = W,
            Count = C,
            PreviousCount = P,
            Tokens = T,
            LastUpdatedTicks = U,
            Timestamps = L is null ? ImmutableArray<long>.Empty : [.. L],
        };
    }
}
