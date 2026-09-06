namespace RateLimiter.Core.Algorithms;

/// <summary>
/// Resolves a <see cref="RateLimitAlgorithm"/> value to the implementation that enforces it.
/// </summary>
/// <remarks>
/// <para>
/// This is the dispatch point that makes "change the algorithm in the rules file and nothing
/// else" true. A rule names an algorithm; the engine asks this registry for it; the same
/// engine, store and key then behave differently. Without it the algorithm choice would have to
/// be a construction-time decision, which is exactly the property the design set out to avoid —
/// one limiter cluster serving many routes, each with the algorithm that suits it.
/// </para>
/// <para>
/// Implementations are stateless and therefore shared. Registering one instance per algorithm
/// rather than constructing per request matters at gateway volumes, where per-request
/// allocation is measured in gigabytes per hour.
/// </para>
/// </remarks>
public sealed class AlgorithmRegistry
{
    private readonly Dictionary<RateLimitAlgorithm, IRateLimitAlgorithm> _byKind;

    /// <summary>
    /// Creates a registry from the given implementations.
    /// </summary>
    /// <param name="algorithms">
    /// The implementations to register, one per <see cref="IRateLimitAlgorithm.Kind"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Two implementations claim the same kind. Failing here rather than picking one silently
    /// matters: a duplicate registration means a rules file's stated algorithm is not
    /// necessarily the one enforcing it, which is unfalsifiable at runtime.
    /// </exception>
    public AlgorithmRegistry(IEnumerable<IRateLimitAlgorithm> algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);

        _byKind = [];
        foreach (IRateLimitAlgorithm algorithm in algorithms)
        {
            if (!_byKind.TryAdd(algorithm.Kind, algorithm))
            {
                throw new ArgumentException(
                    $"More than one implementation registered for '{algorithm.Kind}'.",
                    nameof(algorithms));
            }
        }
    }

    /// <summary>
    /// A registry holding all five built-in algorithms.
    /// </summary>
    public static AlgorithmRegistry Default { get; } = new(
    [
        new FixedWindowAlgorithm(),
        new SlidingWindowLogAlgorithm(),
        new SlidingWindowCounterAlgorithm(),
        new TokenBucketAlgorithm(),
        new LeakyBucketAlgorithm(),
    ]);

    /// <summary>
    /// The algorithm kinds this registry can resolve.
    /// </summary>
    public IReadOnlyCollection<RateLimitAlgorithm> RegisteredKinds => _byKind.Keys;

    /// <summary>
    /// Resolves the implementation for a kind.
    /// </summary>
    /// <param name="kind">The algorithm named by a policy.</param>
    /// <returns>The implementation.</returns>
    /// <exception cref="KeyNotFoundException">No implementation is registered for the kind.</exception>
    public IRateLimitAlgorithm Resolve(RateLimitAlgorithm kind)
    {
        if (_byKind.TryGetValue(kind, out IRateLimitAlgorithm? algorithm))
        {
            return algorithm;
        }

        throw new KeyNotFoundException(
            $"No rate-limiting algorithm is registered for '{kind}'. Registered: " +
            $"{string.Join(", ", _byKind.Keys)}.");
    }
}
