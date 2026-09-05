using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers rule selection, key isolation and algorithm dispatch end to end.
/// </summary>
public sealed class RateLimitEngineTests
{
    private static RateLimitPolicy Policy(RateLimitAlgorithm kind, int limit) => new()
    {
        Algorithm = kind,
        Limit = limit,
        Window = TimeSpan.FromSeconds(10),
    };

    private static (RateLimitEngine Engine, ManualClock Clock) Build(params RateLimitRule[] rules)
    {
        ManualClock clock = new();
        return (new RateLimitEngine(new StaticRuleSource(rules), new InMemoryRateLimitStore(clock), clock: clock), clock);
    }

    private static RequestContext Request(string clientId, string? endpoint = null, string? tier = null) => new()
    {
        ClientId = clientId,
        Endpoint = endpoint,
        Tier = tier,
    };

    [Fact]
    public async Task A_request_matching_two_rules_is_decided_by_the_higher_priority_one()
    {
        RateLimitEngine engine = Build(
            new RateLimitRule
            {
                Name = "strict-free-tier",
                Priority = 1,
                Match = RuleMatch.On((RequestFields.Tier, "free")),
                Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 1),
            },
            new RateLimitRule
            {
                Name = "generous-catch-all",
                Priority = 100,
                Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 1000),
            }).Engine;

        RequestContext request = Request("acct-1", tier: "free");

        RateLimitDecision first = await engine.EvaluateAsync(request);
        RateLimitDecision second = await engine.EvaluateAsync(request);

        Assert.True(first.IsAllowed);
        Assert.Equal("strict-free-tier", first.RuleName);

        // The generous rule also matches, but never gets a say.
        Assert.False(second.IsAllowed);
        Assert.Equal("strict-free-tier", second.RuleName);
    }

    [Fact]
    public async Task Two_rules_keyed_on_the_same_client_keep_independent_counters()
    {
        // Without the rule name in the key these would share a counter, and each rule would
        // silently consume the other's allowance. Nothing in the output would reveal it.
        RateLimitEngine engine = Build(
            new RateLimitRule
            {
                Name = "search",
                Priority = 1,
                Match = RuleMatch.On((RequestFields.Endpoint, "/v1/search")),
                Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 2),
                KeyBy = [RequestFields.ClientId],
            },
            new RateLimitRule
            {
                Name = "payments",
                Priority = 2,
                Match = RuleMatch.On((RequestFields.Endpoint, "/v1/payments")),
                Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 2),
                KeyBy = [RequestFields.ClientId],
            }).Engine;

        // Exhaust the search allowance for this client.
        Assert.True((await engine.EvaluateAsync(Request("acct-1", "/v1/search"))).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request("acct-1", "/v1/search"))).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request("acct-1", "/v1/search"))).IsAllowed);

        // Payments must be untouched for the same client.
        Assert.True((await engine.EvaluateAsync(Request("acct-1", "/v1/payments"))).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request("acct-1", "/v1/payments"))).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request("acct-1", "/v1/payments"))).IsAllowed);
    }

    [Fact]
    public async Task Different_clients_under_one_rule_are_counted_separately()
    {
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "per-client",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 1),
            KeyBy = [RequestFields.ClientId],
        }).Engine;

        Assert.True((await engine.EvaluateAsync(Request("acct-1"))).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request("acct-1"))).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request("acct-2"))).IsAllowed);
    }

    [Fact]
    public async Task A_rule_keyed_on_nothing_shares_one_allowance_across_all_callers()
    {
        // How a genuine global limit is expressed, as opposed to a per-caller one.
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "global",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 2),
            KeyBy = [],
        }).Engine;

        Assert.True((await engine.EvaluateAsync(Request("acct-1"))).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request("acct-2"))).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request("acct-3"))).IsAllowed);
    }

    [Fact]
    public async Task A_client_id_containing_the_key_delimiter_cannot_collide_with_another_client()
    {
        // Key injection. Without escaping, a caller who picks the right ID can land on another
        // caller's counter — spending their quota or hiding inside it.
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "per-client",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 1),
            KeyBy = [RequestFields.ClientId, RequestFields.Endpoint],
        }).Engine;

        // These two differ only in where the delimiter falls.
        RequestContext crafted = Request("acct|endpoint=/v1/a", "/v1/b");
        RequestContext victim = Request("acct", "/v1/a");

        RateLimitDecision first = await engine.EvaluateAsync(crafted);
        RateLimitDecision second = await engine.EvaluateAsync(victim);

        Assert.True(first.IsAllowed);
        Assert.True(second.IsAllowed);
        Assert.NotEqual(first.LimiterKey, second.LimiterKey);
    }

    [Fact]
    public async Task An_unmatched_request_is_allowed_and_says_so()
    {
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "free-only",
            Priority = 1,
            Match = RuleMatch.On((RequestFields.Tier, "free")),
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 1),
        }).Engine;

        RateLimitDecision decision = await engine.EvaluateAsync(Request("acct-1", tier: "enterprise"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(RateLimitEngine.UnmatchedRuleName, decision.RuleName);
    }

    [Fact]
    public async Task The_decision_reports_the_algorithm_that_produced_it()
    {
        // The observable that reveals whether a rules edit actually took effect. If every
        // decision reports the same algorithm after the file changed, the rules were not
        // reloaded.
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "bucketed",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.TokenBucket, limit: 5),
        }).Engine;

        RateLimitDecision decision = await engine.EvaluateAsync(Request("acct-1"));

        Assert.Equal(RateLimitAlgorithm.TokenBucket, decision.Algorithm);
    }

    [Theory]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindowLog)]
    [InlineData(RateLimitAlgorithm.SlidingWindowCounter)]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.LeakyBucket)]
    public async Task Changing_only_the_algorithm_changes_nothing_else(RateLimitAlgorithm kind)
    {
        // The claim that switching algorithms is a configuration change: identical rule,
        // identical key, identical entitlement, only the named algorithm differs.
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "same-rule",
            Priority = 1,
            Policy = Policy(kind, limit: 3),
        }).Engine;

        RequestContext request = Request("acct-1");

        for (int i = 0; i < 3; i++)
        {
            Assert.True((await engine.EvaluateAsync(request)).IsAllowed);
        }

        RateLimitDecision rejected = await engine.EvaluateAsync(request);

        Assert.False(rejected.IsAllowed);
        Assert.Equal(kind, rejected.Algorithm);
        Assert.Equal("same-rule", rejected.RuleName);
    }

    [Fact]
    public async Task Permits_are_carried_through_to_the_algorithm()
    {
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "weighted",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 10),
        }).Engine;

        Assert.True((await engine.EvaluateAsync(Request("acct-1"), permits: 7)).IsAllowed);
        Assert.False((await engine.EvaluateAsync(Request("acct-1"), permits: 7)).IsAllowed);
        Assert.True((await engine.EvaluateAsync(Request("acct-1"), permits: 3)).IsAllowed);
    }

    [Fact]
    public async Task State_persists_across_evaluations_through_the_store()
    {
        ManualClock clock = new();
        InMemoryRateLimitStore store = new(clock);
        RateLimitRule rule = new()
        {
            Name = "shared",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 2),
        };

        // Two engines over one store: the fleet case, in miniature. State lives in the store,
        // so a second instance enforces the same limit rather than a second copy of it.
        RateLimitEngine first = new(new StaticRuleSource(rule), store, clock: clock);
        RateLimitEngine second = new(new StaticRuleSource(rule), store, clock: clock);

        Assert.True((await first.EvaluateAsync(Request("acct-1"))).IsAllowed);
        Assert.True((await second.EvaluateAsync(Request("acct-1"))).IsAllowed);
        Assert.False((await second.EvaluateAsync(Request("acct-1"))).IsAllowed);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_client_never_exceed_the_limit()
    {
        // The end-to-end version of the store's CAS test: the promise a rate limiter makes,
        // under the conditions where it is hardest to keep.
        ManualClock clock = new();
        const int limit = 50;
        RateLimitEngine engine = new(
            new StaticRuleSource(new RateLimitRule
            {
                Name = "hot",
                Priority = 1,
                Policy = Policy(RateLimitAlgorithm.FixedWindow, limit),
            }),
            new InMemoryRateLimitStore(clock),
            clock: clock,
            options: new RateLimiterOptions { MaxWriteAttempts = 200 });

        RequestContext request = Request("acct-hot");

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => Task.Run(async () =>
                (await engine.EvaluateAsync(request)).IsAllowed)));

        Assert.Equal(limit, results.Count(allowed => allowed));
    }

    [Fact]
    public async Task Zero_permits_is_a_programming_error()
    {
        RateLimitEngine engine = Build(new RateLimitRule
        {
            Name = "any",
            Priority = 1,
            Policy = Policy(RateLimitAlgorithm.FixedWindow, limit: 5),
        }).Engine;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await engine.EvaluateAsync(Request("acct-1"), permits: 0));
    }
}
