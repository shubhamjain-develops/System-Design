namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the policy's construction-time validation.
/// </summary>
/// <remarks>
/// The point of these tests is not that the guards throw — it is that an invalid policy cannot
/// be constructed at all. Every algorithm downstream assumes a positive limit over a positive
/// window, and none of them re-checks it. That assumption is only safe if it is impossible to
/// violate here, so these tests defend an invariant five other files rely on.
/// </remarks>
public sealed class RateLimitPolicyTests
{
    private static RateLimitPolicy Valid() => new()
    {
        Algorithm = RateLimitAlgorithm.FixedWindow,
        Limit = 5,
        Window = TimeSpan.FromSeconds(10),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Limit_rejects_non_positive_values(int limit)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            Limit = limit,
            Window = TimeSpan.FromSeconds(1),
        });

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Limit_of_one_is_valid()
    {
        // The smallest meaningful limit. Worth pinning because an off-by-one in the guard
        // would most plausibly reject exactly this value.
        RateLimitPolicy policy = Valid() with { Limit = 1 };

        Assert.Equal(1, policy.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Window_rejects_non_positive_durations(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            Limit = 5,
            Window = TimeSpan.FromSeconds(seconds),
        });
    }

    [Fact]
    public void BurstCapacity_rejects_non_positive_values_when_set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Valid() with { BurstCapacity = 0 });
    }

    [Fact]
    public void BurstCapacity_may_be_omitted()
    {
        RateLimitPolicy policy = Valid();

        Assert.Null(policy.BurstCapacity);
    }

    [Fact]
    public void EffectiveCapacity_falls_back_to_the_limit()
    {
        RateLimitPolicy policy = Valid() with { Limit = 7, BurstCapacity = null };

        Assert.Equal(7, policy.EffectiveCapacity);
    }

    [Fact]
    public void EffectiveCapacity_prefers_an_explicit_burst()
    {
        RateLimitPolicy policy = Valid() with { Limit = 7, BurstCapacity = 20 };

        Assert.Equal(20, policy.EffectiveCapacity);
    }

    [Fact]
    public void PermitsPerSecond_keeps_the_fractional_part()
    {
        // 5 permits per 10 seconds is 0.5/s. Truncating to an integer would round this to
        // zero and stall every bucket algorithm permanently, so the fractional result is
        // load-bearing rather than cosmetic.
        RateLimitPolicy policy = Valid() with { Limit = 5, Window = TimeSpan.FromSeconds(10) };

        Assert.Equal(0.5, policy.PermitsPerSecond, precision: 10);
    }

    [Fact]
    public void PermitsPerSecond_handles_sub_second_windows()
    {
        RateLimitPolicy policy = Valid() with { Limit = 3, Window = TimeSpan.FromMilliseconds(250) };

        Assert.Equal(12.0, policy.PermitsPerSecond, precision: 10);
    }

    [Fact]
    public void Policies_with_equal_values_are_equal()
    {
        // Value equality matters because policies are compared when deciding whether a
        // reloaded rules file actually changed anything.
        Assert.Equal(Valid(), Valid());
    }
}
