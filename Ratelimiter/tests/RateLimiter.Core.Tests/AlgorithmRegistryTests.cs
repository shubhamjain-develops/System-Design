using RateLimiter.Core.Algorithms;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers algorithm dispatch, the mechanism that makes the algorithm a configuration choice.
/// </summary>
public sealed class AlgorithmRegistryTests
{
    [Fact]
    public void Default_registry_resolves_every_declared_algorithm()
    {
        // Guards the gap that would otherwise open when a sixth enum member is added without an
        // implementation: the rules file would accept it and the engine would throw at runtime,
        // on a request, in production.
        foreach (RateLimitAlgorithm kind in Enum.GetValues<RateLimitAlgorithm>())
        {
            IRateLimitAlgorithm algorithm = AlgorithmRegistry.Default.Resolve(kind);

            Assert.Equal(kind, algorithm.Kind);
        }
    }

    [Fact]
    public void Resolving_an_unregistered_algorithm_names_what_is_available()
    {
        AlgorithmRegistry registry = new([new FixedWindowAlgorithm()]);

        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(
            () => registry.Resolve(RateLimitAlgorithm.TokenBucket));

        Assert.Contains("TokenBucket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FixedWindow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_registrations_are_rejected()
    {
        // Silently keeping one of them would mean a rules file's stated algorithm is not
        // necessarily the one enforcing it — a discrepancy nothing at runtime could reveal.
        Assert.Throws<ArgumentException>(
            () => new AlgorithmRegistry([new FixedWindowAlgorithm(), new FixedWindowAlgorithm()]));
    }

    [Fact]
    public void Registry_reports_what_it_holds()
    {
        AlgorithmRegistry registry = new([new FixedWindowAlgorithm(), new TokenBucketAlgorithm()]);

        Assert.Equal(2, registry.RegisteredKinds.Count);
        Assert.Contains(RateLimitAlgorithm.TokenBucket, registry.RegisteredKinds);
    }

    [Fact]
    public void Default_registry_is_shared_rather_than_rebuilt()
    {
        // Implementations are stateless, so one instance each is correct. At gateway volumes,
        // per-request allocation of five objects is measured in gigabytes per hour.
        Assert.Same(AlgorithmRegistry.Default, AlgorithmRegistry.Default);
        Assert.Same(
            AlgorithmRegistry.Default.Resolve(RateLimitAlgorithm.TokenBucket),
            AlgorithmRegistry.Default.Resolve(RateLimitAlgorithm.TokenBucket));
    }
}
