using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers rule ordering and the diagnostics that pay for choosing first-match-wins.
/// </summary>
/// <remarks>
/// First-match-wins was chosen over all-match-and-intersect because it makes a rejection
/// explicable by a single rule name. The cost is that a broad rule placed early silently
/// disables the narrower ones beneath it — a limit you believe you have and do not. These tests
/// are that cost being paid.
/// </remarks>
public sealed class RuleSetValidationTests
{
    private static RateLimitPolicy Policy(int limit = 5) => new()
    {
        Algorithm = RateLimitAlgorithm.FixedWindow,
        Limit = limit,
        Window = TimeSpan.FromSeconds(10),
    };

    private static RateLimitRule Rule(string name, int priority, RuleMatch? match = null, RateLimitPolicy? policy = null) => new()
    {
        Name = name,
        Priority = priority,
        Match = match ?? RuleMatch.Any,
        Policy = policy ?? Policy(),
    };

    [Fact]
    public void Rules_are_ordered_by_priority_not_declaration()
    {
        RuleSet set = RuleSet.Create(
        [
            Rule("third", 30),
            Rule("first", 10),
            Rule("second", 20),
        ]);

        Assert.Equal(["first", "second", "third"], set.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Equal_priorities_keep_declaration_order()
    {
        // Ordering must be total and stable, or the same rules file would behave differently
        // between runs and the shadowing diagnostics would be non-deterministic with it.
        RuleSet set = RuleSet.Create(
        [
            Rule("a", 10),
            Rule("b", 10),
            Rule("c", 10),
        ]);

        Assert.Equal(["a", "b", "c"], set.Rules.Select(r => r.Name));
    }

    [Fact]
    public void A_catch_all_placed_first_shadows_everything_after_it()
    {
        // The exact mistake first-match-wins invites, and the reason this diagnostic exists.
        RuleSet set = RuleSet.Create(
        [
            Rule("catch-all", 1),
            Rule("free-tier", 10, RuleMatch.On((RequestFields.Tier, "free"))),
        ]);

        RuleDiagnostic shadowed = Assert.Single(
            set.Diagnostics.Where(d => d.Severity == RuleDiagnosticSeverity.Warning));

        Assert.Equal("free-tier", shadowed.RuleName);
        Assert.Contains("catch-all", shadowed.Message, StringComparison.Ordinal);
        Assert.Contains("Unreachable", shadowed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broader_condition_shadows_a_narrower_one_on_the_same_dimension()
    {
        RuleSet set = RuleSet.Create(
        [
            Rule("any-tier", 1, RuleMatch.On((RequestFields.Tier, RuleMatch.Wildcard))),
            Rule("free-only", 10, RuleMatch.On((RequestFields.Tier, "free"))),
        ]);

        Assert.Contains(
            set.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Warning && d.RuleName == "free-only");
    }

    [Fact]
    public void A_narrower_rule_placed_first_shadows_nothing()
    {
        // The correct ordering must be silent, or the diagnostic becomes noise that gets ignored
        // — and a warning nobody reads is worse than no warning.
        RuleSet set = RuleSet.Create(
        [
            Rule("free-tier", 1, RuleMatch.On((RequestFields.Tier, "free"))),
            Rule("catch-all", 10),
        ]);

        Assert.DoesNotContain(set.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Rules_constraining_different_dimensions_do_not_shadow_each_other()
    {
        RuleSet set = RuleSet.Create(
        [
            Rule("by-tier", 1, RuleMatch.On((RequestFields.Tier, "free"))),
            Rule("by-endpoint", 10, RuleMatch.On((RequestFields.Endpoint, "/v1/search"))),
        ]);

        Assert.DoesNotContain(set.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Warning);
    }

    [Fact]
    public void An_extra_condition_makes_a_rule_narrower_and_therefore_reachable()
    {
        // free+search is narrower than free alone, so placing free first does shadow it — but
        // placing free-and-search first does not shadow plain free.
        RuleSet set = RuleSet.Create(
        [
            Rule("free-search", 1, RuleMatch.On((RequestFields.Tier, "free"), (RequestFields.Endpoint, "/v1/search"))),
            Rule("free-any", 10, RuleMatch.On((RequestFields.Tier, "free"))),
        ]);

        Assert.DoesNotContain(set.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Duplicate_names_are_an_error_because_names_form_the_storage_key()
    {
        RuleSet set = RuleSet.Create([Rule("same", 1), Rule("same", 2)]);

        Assert.True(set.HasErrors);
        Assert.Contains(
            set.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Error && d.Message.Contains("share a counter", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unnamed_rule_is_an_error()
    {
        RuleSet set = RuleSet.Create([Rule("  ", 1)]);

        Assert.True(set.HasErrors);
    }

    [Fact]
    public void Burst_capacity_on_a_window_algorithm_warns_without_failing_the_file()
    {
        // A warning, not an error: failing the whole rules file over one ignored field would
        // take every other rule down with it.
        RateLimitPolicy policy = new()
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            Limit = 5,
            Window = TimeSpan.FromSeconds(10),
            BurstCapacity = 20,
        };

        RuleSet set = RuleSet.Create([Rule("windowed", 1, policy: policy)]);

        Assert.False(set.HasErrors);
        Assert.Contains(
            set.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Warning && d.Message.Contains("burstCapacity", StringComparison.Ordinal));
    }

    [Fact]
    public void Burst_capacity_on_a_bucket_algorithm_is_silent()
    {
        RateLimitPolicy policy = new()
        {
            Algorithm = RateLimitAlgorithm.TokenBucket,
            Limit = 5,
            Window = TimeSpan.FromSeconds(10),
            BurstCapacity = 20,
        };

        RuleSet set = RuleSet.Create([Rule("bursty", 1, policy: policy)]);

        Assert.DoesNotContain(set.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_rule_set_with_no_catch_all_says_so()
    {
        RuleSet set = RuleSet.Create([Rule("free", 1, RuleMatch.On((RequestFields.Tier, "free")))]);

        Assert.Contains(set.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Information);
    }

    [Fact]
    public void Validation_reports_rather_than_throws()
    {
        // A rules file arriving from outside the process must be able to report everything wrong
        // with it. A constructor that threw on the first problem would report exactly one.
        RuleSet set = RuleSet.Create([Rule("dupe", 1), Rule("dupe", 2), Rule("shadowed", 3, RuleMatch.On((RequestFields.Tier, "free")))]);

        Assert.True(set.Diagnostics.Length > 1);
    }

    [Fact]
    public void An_empty_rule_set_matches_nothing()
    {
        Assert.False(RuleSet.Empty.TryMatch(new RequestContext { ClientId = "x" }, out _));
    }
}
