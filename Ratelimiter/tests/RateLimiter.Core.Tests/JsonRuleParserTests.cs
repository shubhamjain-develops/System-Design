using RateLimiter.Core.Configuration;
using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers the JSON rules schema and the quality of its failure reporting.
/// </summary>
public sealed class JsonRuleParserTests
{
    private const string ValidDocument = """
        {
          "rules": [
            {
              "name": "free-tier-search",
              "priority": 10,
              "match": { "tier": "free", "endpoint": "/v1/search" },
              "keyBy": ["clientId"],
              "policy": { "algorithm": "tokenBucket", "limit": 5, "windowSeconds": 10, "burstCapacity": 8 }
            },
            {
              "name": "catch-all",
              "priority": 1000,
              "policy": { "algorithm": "fixedWindow", "limit": 100, "windowSeconds": 60 }
            }
          ]
        }
        """;

    [Fact]
    public void Parses_a_complete_document()
    {
        RuleParseResult result = JsonRuleParser.Parse(ValidDocument);

        Assert.True(result.Succeeded);
        RuleSet set = result.RuleSet!;

        Assert.Equal(["free-tier-search", "catch-all"], set.Rules.Select(r => r.Name));

        RateLimitRule first = set.Rules[0];
        Assert.Equal(RateLimitAlgorithm.TokenBucket, first.Policy.Algorithm);
        Assert.Equal(5, first.Policy.Limit);
        Assert.Equal(TimeSpan.FromSeconds(10), first.Policy.Window);
        Assert.Equal(8, first.Policy.BurstCapacity);
        // Asserted element-wise rather than with a collection literal: a collection expression
        // takes its target type from the other argument, so Assert.Equal would bind to the
        // single-value overload and compare the ImmutableArray by reference — the very trap
        // RateLimitRule.Equals exists to avoid.
        Assert.Equal(RequestFields.ClientId, Assert.Single(first.KeyBy));
    }

    [Fact]
    public void The_parsed_rules_actually_match_requests()
    {
        // Parsing into the right shape is worth nothing if the shape does not match. This joins
        // the config path to the runtime path, which is the seam most likely to be wrong.
        RuleSet set = JsonRuleParser.Parse(ValidDocument).RuleSet!;

        Assert.True(set.TryMatch(
            new RequestContext { ClientId = "a", Tier = "free", Endpoint = "/v1/search" },
            out RateLimitRule? matched));
        Assert.Equal("free-tier-search", matched.Name);

        Assert.True(set.TryMatch(
            new RequestContext { ClientId = "a", Tier = "enterprise", Endpoint = "/v1/other" },
            out RateLimitRule? fallback));
        Assert.Equal("catch-all", fallback.Name);
    }

    [Theory]
    [InlineData("tokenBucket")]
    [InlineData("TokenBucket")]
    [InlineData("token_bucket")]
    [InlineData("token-bucket")]
    [InlineData("TOKENBUCKET")]
    public void Algorithm_names_tolerate_casing_and_separators(string spelling)
    {
        // Rejecting a hand-written file over a separator has a real cost: the file fails to
        // load and the previous limits stay in force unnoticed.
        string json = $$"""
            { "rules": [ { "name": "r", "policy": { "algorithm": "{{spelling}}", "limit": 1, "windowSeconds": 1 } } ] }
            """;

        RuleParseResult result = JsonRuleParser.Parse(json);

        Assert.True(result.Succeeded);
        Assert.Equal(RateLimitAlgorithm.TokenBucket, result.RuleSet!.Rules[0].Policy.Algorithm);
    }

    [Fact]
    public void A_match_value_may_be_a_single_string_or_a_list()
    {
        string json = """
            {
              "rules": [
                { "name": "r", "match": { "tier": ["free", "trial"] },
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } }
              ]
            }
            """;

        RuleSet set = JsonRuleParser.Parse(json).RuleSet!;

        Assert.True(set.TryMatch(new RequestContext { ClientId = "a", Tier = "trial" }, out _));
        Assert.False(set.TryMatch(new RequestContext { ClientId = "a", Tier = "pro" }, out _));
    }

    [Fact]
    public void An_explicitly_empty_keyBy_is_preserved_as_a_global_limit()
    {
        // Silently defaulting this to clientId would turn a global limit into a per-client one,
        // multiplying the intended capacity by the number of callers.
        string json = """
            {
              "rules": [
                { "name": "global", "keyBy": [],
                  "policy": { "algorithm": "fixedWindow", "limit": 2, "windowSeconds": 60 } }
              ]
            }
            """;

        RuleSet set = JsonRuleParser.Parse(json).RuleSet!;

        Assert.Empty(set.Rules[0].KeyBy);
    }

    [Fact]
    public void Malformed_json_is_reported_not_thrown()
    {
        // Reloads happen on a background thread where an exception has nowhere useful to go.
        RuleParseResult result = JsonRuleParser.Parse("{ this is not json");

        Assert.False(result.Succeeded);
        Assert.Null(result.RuleSet);
        Assert.Contains(result.Diagnostics, d => d.Severity == RuleDiagnosticSeverity.Error);
    }

    [Fact]
    public void A_missing_rules_array_is_an_error()
    {
        RuleParseResult result = JsonRuleParser.Parse("""{ "notRules": [] }""");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("'rules' array", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "rules": [ { "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } } ] }""", "name")]
    [InlineData("""{ "rules": [ { "name": "r" } ] }""", "policy")]
    [InlineData("""{ "rules": [ { "name": "r", "policy": { "algorithm": "nope", "limit": 1, "windowSeconds": 1 } } ] }""", "algorithm")]
    [InlineData("""{ "rules": [ { "name": "r", "policy": { "algorithm": "fixedWindow", "limit": 0, "windowSeconds": 1 } } ] }""", "limit")]
    [InlineData("""{ "rules": [ { "name": "r", "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 0 } } ] }""", "windowSeconds")]
    public void Field_errors_name_the_field_that_is_wrong(string json, string expectedMention)
    {
        // A rules file is edited by a person under pressure. The message has to say what to fix.
        RuleParseResult result = JsonRuleParser.Parse(json);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Error
                 && d.Message.Contains(expectedMention, StringComparison.Ordinal));
    }

    [Fact]
    public void Error_messages_carry_the_index_of_the_offending_rule()
    {
        string json = """
            {
              "rules": [
                { "name": "ok", "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } },
                { "name": "bad", "policy": { "algorithm": "fixedWindow", "limit": -5, "windowSeconds": 1 } }
              ]
            }
            """;

        RuleParseResult result = JsonRuleParser.Parse(json);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rules[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_match_list_is_rejected_as_unmatchable()
    {
        // [] would match nothing, which is almost certainly not what was meant, and is
        // indistinguishable at runtime from a rule that simply never fires.
        string json = """
            {
              "rules": [
                { "name": "r", "match": { "tier": [] },
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } }
              ]
            }
            """;

        Assert.False(JsonRuleParser.Parse(json).Succeeded);
    }

    [Fact]
    public void Validation_diagnostics_survive_parsing()
    {
        // Shadowing is a property of the rule set, not the document, so it must appear on the
        // parse result too — otherwise loading from JSON would silently lose the warning.
        string json = """
            {
              "rules": [
                { "name": "catch-all", "priority": 1,
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } },
                { "name": "specific", "priority": 2, "match": { "tier": "free" },
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } }
              ]
            }
            """;

        RuleParseResult result = JsonRuleParser.Parse(json);

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == RuleDiagnosticSeverity.Warning && d.RuleName == "specific");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        // Hand-edited configuration files acquire both.
        string json = """
            {
              // limits for the search API
              "rules": [
                { "name": "r", "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } },
              ]
            }
            """;

        Assert.True(JsonRuleParser.Parse(json).Succeeded);
    }
}
