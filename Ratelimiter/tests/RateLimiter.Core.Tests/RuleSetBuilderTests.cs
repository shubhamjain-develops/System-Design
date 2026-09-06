using RateLimiter.Core.Configuration;
using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the code-based rule builder, and that it agrees with the JSON path.
/// </summary>
public sealed class RuleSetBuilderTests
{
    [Fact]
    public void Builds_a_rule_with_every_part_set()
    {
        RuleSet set = RuleSetBuilder.Create()
            .Add("free-search", r => r
                .WithPriority(10)
                .Matching(RequestFields.Tier, "free")
                .Matching(RequestFields.Endpoint, "/v1/search")
                .KeyedBy(RequestFields.ClientId)
                .Using(RateLimitAlgorithm.TokenBucket, 5, TimeSpan.FromSeconds(10), burstCapacity: 8))
            .Build();

        RateLimitRule rule = Assert.Single(set.Rules);

        Assert.Equal(10, rule.Priority);
        Assert.Equal(RateLimitAlgorithm.TokenBucket, rule.Policy.Algorithm);
        Assert.Equal(8, rule.Policy.BurstCapacity);
        Assert.True(rule.Match.Matches(new RequestContext
        {
            ClientId = "a",
            Tier = "free",
            Endpoint = "/v1/search",
        }));
    }

    [Fact]
    public void Defaults_to_matching_everything_and_keying_by_client()
    {
        RuleSet set = RuleSetBuilder.Create()
            .Add("simple", r => r.Using(RateLimitAlgorithm.FixedWindow, 1, TimeSpan.FromSeconds(1)))
            .Build();

        RateLimitRule rule = Assert.Single(set.Rules);

        Assert.Equal(RuleMatch.Any, rule.Match);
        Assert.Equal(RequestFields.ClientId, Assert.Single(rule.KeyBy));
    }

    [Fact]
    public void A_rule_with_no_policy_fails_loudly()
    {
        // Code-defined rules are a programming error when incomplete, unlike file-defined ones
        // which must report rather than throw. Different input, different contract.
        Assert.Throws<InvalidOperationException>(
            () => RuleSetBuilder.Create().Add("incomplete", _ => { }).Build());
    }

    [Fact]
    public void Built_rules_go_through_the_same_validation_as_parsed_ones()
    {
        RuleSet set = RuleSetBuilder.Create()
            .Add("catch-all", r => r
                .WithPriority(1)
                .Using(RateLimitAlgorithm.FixedWindow, 1, TimeSpan.FromSeconds(1)))
            .Add("shadowed", r => r
                .WithPriority(2)
                .Matching(RequestFields.Tier, "free")
                .Using(RateLimitAlgorithm.FixedWindow, 1, TimeSpan.FromSeconds(1)))
            .Build();

        Assert.Contains(
            set.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Warning && d.RuleName == "shadowed");
    }

    [Fact]
    public void The_builder_and_the_parser_produce_identical_rules()
    {
        // The claim that configuration is a SOURCE of rules rather than the rule model itself.
        // If these two ever diverge, every test written against the builder stops saying
        // anything about what a deployment actually runs.
        RuleSet built = RuleSetBuilder.Create()
            .Add("free-search", r => r
                .WithPriority(10)
                .Matching(RequestFields.Tier, "free")
                .KeyedBy(RequestFields.ClientId, RequestFields.Endpoint)
                .Using(RateLimitAlgorithm.SlidingWindowLog, 5, TimeSpan.FromSeconds(10)))
            .Build();

        RuleSet parsed = JsonRuleParser.Parse("""
            {
              "rules": [
                {
                  "name": "free-search",
                  "priority": 10,
                  "match": { "tier": "free" },
                  "keyBy": ["clientId", "endpoint"],
                  "policy": { "algorithm": "slidingWindowLog", "limit": 5, "windowSeconds": 10 }
                }
              ]
            }
            """).RuleSet!;

        Assert.Equal(built.Rules[0], parsed.Rules[0]);
    }
}
