using System.Collections.Immutable;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the hand-written structural equality on the types that hold immutable collections.
/// </summary>
/// <remarks>
/// These implementations exist because a record holding an <see cref="ImmutableArray{T}"/> or an
/// <see cref="ImmutableDictionary{TKey, TValue}"/> gets compiler-generated equality that compares
/// those members by <em>reference</em> — so two instances built from identical data report
/// themselves different. Reload logic asking "did the rules actually change?" would then answer
/// yes every time.
///
/// Having replaced that behaviour by hand, the replacement has to be held to the contract the
/// compiler was providing: equal values are equal, equal values hash equally, and unequal values
/// are distinguished on every member that matters.
/// </remarks>
public sealed class ValueEqualityTests
{
    private static RateLimitPolicy Policy() => new()
    {
        Algorithm = RateLimitAlgorithm.TokenBucket,
        Limit = 5,
        Window = TimeSpan.FromSeconds(10),
    };

    // ---- RuleMatch ----

    [Fact]
    public void Matches_built_from_the_same_conditions_are_equal()
    {
        RuleMatch a = RuleMatch.On((RequestFields.Tier, "free"), (RequestFields.Endpoint, "/v1/search"));
        RuleMatch b = RuleMatch.On((RequestFields.Tier, "free"), (RequestFields.Endpoint, "/v1/search"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Match_equality_does_not_depend_on_condition_order()
    {
        // Two rules files listing the same conditions in a different order describe the same rule.
        RuleMatch a = RuleMatch.On((RequestFields.Tier, "free"), (RequestFields.Endpoint, "/v1/x"));
        RuleMatch b = RuleMatch.On((RequestFields.Endpoint, "/v1/x"), (RequestFields.Tier, "free"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Accepted_values_compare_as_a_set_not_a_sequence()
    {
        // Accepting free-or-trial is the same rule as accepting trial-or-free.
        RuleMatch a = new()
        {
            Conditions = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add(RequestFields.Tier, ["free", "trial"]),
        };

        RuleMatch b = new()
        {
            Conditions = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add(RequestFields.Tier, ["trial", "free"]),
        };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Matches_with_different_values_are_not_equal()
    {
        Assert.NotEqual(
            RuleMatch.On((RequestFields.Tier, "free")),
            RuleMatch.On((RequestFields.Tier, "pro")));
    }

    [Fact]
    public void Matches_with_different_condition_counts_are_not_equal()
    {
        Assert.NotEqual(
            RuleMatch.On((RequestFields.Tier, "free")),
            RuleMatch.On((RequestFields.Tier, "free"), (RequestFields.Endpoint, "/v1/x")));
    }

    [Fact]
    public void Matches_constraining_different_dimensions_are_not_equal()
    {
        Assert.NotEqual(
            RuleMatch.On((RequestFields.Tier, "free")),
            RuleMatch.On((RequestFields.Endpoint, "free")));
    }

    [Fact]
    public void A_match_is_equal_to_itself_and_not_to_null()
    {
        RuleMatch match = RuleMatch.On((RequestFields.Tier, "free"));

        Assert.Equal(match, match);
        Assert.False(match.Equals(null));
    }

    [Fact]
    public void The_empty_match_equals_another_empty_match()
    {
        Assert.Equal(RuleMatch.Any, new RuleMatch());
    }

    // ---- RateLimitRule ----

    private static RateLimitRule Rule() => new()
    {
        Name = "r",
        Priority = 10,
        Match = RuleMatch.On((RequestFields.Tier, "free")),
        KeyBy = [RequestFields.ClientId, RequestFields.Endpoint],
        Policy = Policy(),
    };

    [Fact]
    public void Identical_rules_are_equal_and_hash_equally()
    {
        Assert.Equal(Rule(), Rule());
        Assert.Equal(Rule().GetHashCode(), Rule().GetHashCode());
    }

    [Fact]
    public void Rules_differing_in_name_are_not_equal()
    {
        Assert.NotEqual(Rule(), Rule() with { Name = "other" });
    }

    [Fact]
    public void Rules_differing_in_priority_are_not_equal()
    {
        Assert.NotEqual(Rule(), Rule() with { Priority = 11 });
    }

    [Fact]
    public void Rules_differing_in_match_are_not_equal()
    {
        Assert.NotEqual(Rule(), Rule() with { Match = RuleMatch.Any });
    }

    [Fact]
    public void Rules_differing_in_policy_are_not_equal()
    {
        Assert.NotEqual(Rule(), Rule() with { Policy = Policy() with { Limit = 6 } });
    }

    [Fact]
    public void KeyBy_order_matters_because_it_determines_the_storage_key()
    {
        // Unlike a match's accepted values, these are not a set: reversing them produces a
        // different key layout and therefore a different counted population.
        RateLimitRule reversed = Rule() with { KeyBy = [RequestFields.Endpoint, RequestFields.ClientId] };

        Assert.NotEqual(Rule(), reversed);
        Assert.NotEqual(
            LimiterKey.Build(Rule(), new RequestContext { ClientId = "a", Endpoint = "/x" }),
            LimiterKey.Build(reversed, new RequestContext { ClientId = "a", Endpoint = "/x" }));
    }

    [Fact]
    public void Rules_differing_in_keyBy_length_are_not_equal()
    {
        Assert.NotEqual(Rule(), Rule() with { KeyBy = [RequestFields.ClientId] });
    }

    [Fact]
    public void A_rule_is_not_equal_to_null()
    {
        Assert.False(Rule().Equals(null));
    }

    // ---- LimiterState ----

    [Fact]
    public void States_with_the_same_values_are_equal()
    {
        LimiterState a = new() { Count = 3, WindowStartTicks = 100, Tokens = 1.5, LastUpdatedTicks = 7 };
        LimiterState b = new() { Count = 3, WindowStartTicks = 100, Tokens = 1.5, LastUpdatedTicks = 7 };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void States_with_identical_timestamp_lists_are_equal()
    {
        // The member the compiler would have compared by reference.
        LimiterState a = new() { Timestamps = [1, 2, 3] };
        LimiterState b = new() { Timestamps = [1, 2, 3] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void States_with_different_timestamp_lists_are_not_equal()
    {
        Assert.NotEqual(
            new LimiterState { Timestamps = [1, 2, 3] },
            new LimiterState { Timestamps = [1, 2, 4] });
    }

    [Fact]
    public void States_with_different_timestamp_lengths_are_not_equal()
    {
        Assert.NotEqual(
            new LimiterState { Timestamps = [1, 2] },
            new LimiterState { Timestamps = [1, 2, 3] });
    }

    [Theory]
    [InlineData(1L, 0L, 0L, 0.0, 0L)]
    [InlineData(0L, 1L, 0L, 0.0, 0L)]
    [InlineData(0L, 0L, 1L, 0.0, 0L)]
    [InlineData(0L, 0L, 0L, 1.0, 0L)]
    [InlineData(0L, 0L, 0L, 0.0, 1L)]
    public void Every_state_field_is_distinguished(long window, long count, long previous, double tokens, long updated)
    {
        LimiterState changed = new()
        {
            WindowStartTicks = window,
            Count = count,
            PreviousCount = previous,
            Tokens = tokens,
            LastUpdatedTicks = updated,
        };

        Assert.NotEqual(LimiterState.Empty, changed);
    }

    [Fact]
    public void The_empty_state_equals_a_freshly_constructed_one()
    {
        Assert.Equal(LimiterState.Empty, new LimiterState());
    }

    [Fact]
    public void A_state_is_not_equal_to_null()
    {
        Assert.False(LimiterState.Empty.Equals(null));
    }

    // ---- Rendering, used in logs and demo output ----

    [Fact]
    public void A_decision_renders_its_verdict_rule_and_algorithm()
    {
        RateLimitDecision allowed = new()
        {
            IsAllowed = true,
            RuleName = "free-search",
            Algorithm = RateLimitAlgorithm.TokenBucket,
            LimiterKey = "k",
            RemainingPermits = 4,
        };

        string rendered = allowed.ToString();

        Assert.Contains("ALLOW", rendered, StringComparison.Ordinal);
        Assert.Contains("free-search", rendered, StringComparison.Ordinal);
        Assert.Contains("TokenBucket", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_decision_renders_its_retry_delay_and_any_store_failure()
    {
        RateLimitDecision rejected = new()
        {
            IsAllowed = false,
            RuleName = "r",
            Algorithm = RateLimitAlgorithm.FixedWindow,
            LimiterKey = "k",
            RemainingPermits = 0,
            RetryAfter = TimeSpan.FromSeconds(2),
            StoreFailureOccurred = true,
        };

        string rendered = rejected.ToString();

        Assert.Contains("REJECT", rendered, StringComparison.Ordinal);
        Assert.Contains("retryAfter", rendered, StringComparison.Ordinal);
        Assert.Contains("store failure", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_renders_its_algorithm_limit_and_burst()
    {
        Assert.Contains("burst 20", (Policy() with { BurstCapacity = 20 }).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("burst", Policy().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_renders_its_name_priority_match_and_key()
    {
        string rendered = Rule().ToString();

        Assert.Contains("r [p10]", rendered, StringComparison.Ordinal);
        Assert.Contains("clientId+endpoint", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wildcard_match_renders_as_a_star()
    {
        Assert.Equal("*", RuleMatch.Any.ToString());
    }

    [Fact]
    public void A_diagnostic_renders_with_and_without_a_rule_name()
    {
        Assert.Contains("[r]", new RuleDiagnostic(RuleDiagnosticSeverity.Warning, "r", "m").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[", new RuleDiagnostic(RuleDiagnosticSeverity.Error, string.Empty, "m").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_clock_reports_utc_and_advances()
    {
        // The production clock is otherwise never exercised, since every other test injects a
        // manual one.
        DateTimeOffset first = SystemClock.Instance.UtcNow;

        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.True(SystemClock.Instance.UtcNow >= first);
    }
}
