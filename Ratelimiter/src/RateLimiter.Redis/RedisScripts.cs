namespace RateLimiter.Redis;

/// <summary>
/// The Lua run on the Redis server.
/// </summary>
/// <remarks>
/// Two scripts, and the difference between them is the central trade-off of this library made
/// concrete. <see cref="CompareAndSwap"/> is the one this adapter uses.
/// <see cref="TokenBucketWholeAlgorithm"/> is the one it does not, kept here because the
/// argument for it is real and a reader deserves to see what was declined rather than a claim
/// that it was considered.
/// </remarks>
public static class RedisScripts
{
    /// <summary>
    /// Sets a key only if its stored version matches the expected one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what the store abstraction asks Redis for. The algorithm stays in
    /// C#, so this script knows nothing about token buckets or windows and never needs changing
    /// when an algorithm is added.
    /// </para>
    /// <para>
    /// The version is the prefix of the stored value, before the first <c>|</c>, so the script
    /// compares it with a substring rather than parsing JSON in Lua — parsing structured data
    /// inside a script is where these become slow and hard to reason about.
    /// </para>
    /// <para>
    /// The cost this pays is one round trip to read plus one to write, per request, plus another
    /// pair for every lost race. That is the price of keeping the algorithms testable in C#.
    /// </para>
    /// </remarks>
    public const string CompareAndSwap = """
        local current = redis.call('GET', KEYS[1])
        local expected = ARGV[1]

        if current == false then
          -- Absent. Only a create-if-not-exists write may proceed.
          if expected ~= '0' then
            return 0
          end
        else
          local separator = string.find(current, '|', 1, true)
          if separator == nil then
            -- Unparseable value. Treat as absent rather than failing the request: a corrupt
            -- entry must not make a key permanently unwritable.
            if expected ~= '0' then
              return 0
            end
          else
            local version = string.sub(current, 1, separator - 1)
            if version ~= expected then
              return 0
            end
          end
        end

        redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        return 1
        """;

    /// <summary>
    /// The whole token-bucket algorithm, evaluated server-side in one round trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the rejected alternative, kept as documentation.</strong> It is not used
    /// by <see cref="RedisRateLimitStore"/> and nothing calls it.
    /// </para>
    /// <para>
    /// What it buys is substantial and should not be understated: one round trip instead of two,
    /// atomicity by construction with no version and no retry, and immunity to contention on a
    /// hot key. At high enough volume on a single key — a shared global limit, or one very large
    /// customer — this is the correct implementation and the CAS approach is not.
    /// </para>
    /// <para>
    /// What it costs is that the algorithm now exists twice: once in C#, where it is unit-tested
    /// against every boundary, and once here, where it can only be tested against a live server.
    /// The two will drift, and the copy that drifts is the one running in production. For a
    /// library whose stated purpose is that its boundary conditions are provable, that is the
    /// wrong trade.
    /// </para>
    /// <para>
    /// <strong>When to switch.</strong> The CAS path costs two round trips plus retries; this
    /// costs one and never retries. The crossover is contention, not throughput: CAS is fine
    /// until concurrent writers to a <em>single key</em> are frequent enough that lost races are
    /// common. Spread across many keys, CAS scales as well as anything. Watch the rate of
    /// exhausted write attempts, not requests per second.
    /// </para>
    /// </remarks>
    public const string TokenBucketWholeAlgorithm = """
        -- KEYS[1]  bucket key
        -- ARGV[1]  capacity (permits)
        -- ARGV[2]  refill rate (permits per second)
        -- ARGV[3]  now (unix milliseconds)
        -- ARGV[4]  permits requested
        -- ARGV[5]  ttl (milliseconds)
        --
        -- Returns { allowed, remaining, retryAfterMs }

        local capacity = tonumber(ARGV[1])
        local rate = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local permits = tonumber(ARGV[4])
        local ttl = tonumber(ARGV[5])

        local state = redis.call('HMGET', KEYS[1], 'tokens', 'updated')
        local tokens = tonumber(state[1])
        local updated = tonumber(state[2])

        if tokens == nil or updated == nil then
          -- An unseen caller has consumed nothing, so the bucket starts full.
          tokens = capacity
          updated = now
        else
          -- Clamped at zero: a backwards clock must never mint permits.
          local elapsed = math.max(0, now - updated)
          tokens = math.min(capacity, tokens + (elapsed / 1000.0) * rate)
          updated = now
        end

        local allowed = 0
        local retryAfter = 0

        if tokens >= permits then
          allowed = 1
          tokens = tokens - permits
        else
          retryAfter = math.ceil(((permits - tokens) / rate) * 1000)
        end

        redis.call('HSET', KEYS[1], 'tokens', tokens, 'updated', updated)
        redis.call('PEXPIRE', KEYS[1], ttl)

        return { allowed, math.floor(tokens), retryAfter }
        """;
}
