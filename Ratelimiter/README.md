# RateLimiter

A configurable rate-limiting library: five algorithms behind one interface, a rule engine that
selects between them per request, and a pluggable store so the same rules work on one node or a
fleet.

Built as the executable form of the design in `../Rate-Limiter.excalidraw`.

## Running it

Requires the .NET 10 SDK.

```bash
dotnet test  RateLimiter.slnx -c Release          # 208 tests
dotnet run --project src/RateLimiter.Demo         # live burst view
```

Demo keys: `a` cycles every rule onto one algorithm, `f` simulates a cache outage, `q` quits.
For a scripted run: `--seconds 10 --fail-after 5`.

```
client         tier  endpoint       rule             algorithm          rem   allow  rejct  recent
acct-bursty    free  /v1/search     free-search      TokenBucket          0      12      5   ###########.....#
acct-greedy    free  /v1/search     free-search      TokenBucket          0      14     22   #########...#.....#.....#....#
acct-pro       pro   /v1/search     pro-search       TokenBucket         18      36      0   ####################################
acct-payer     pro   /v1/payments   payments-exact   SlidingWindowLog     0       4      7   ###.......#
```

`acct-greedy` and `acct-pro` send identical traffic. The only difference is which rule matched.

## Layout

```
src/RateLimiter.Core     algorithms, rule engine, store abstraction — zero dependencies
src/RateLimiter.Redis    stubbed Redis store; references Core, never the reverse
src/RateLimiter.Demo     console harness
tests/                   xUnit
docs/adr                 one record per design fork
```

`RateLimiter.Core` has **no** `ProjectReference` and **no** `PackageReference`. That is what proves
the store abstraction holds: the core cannot reach a backend even by accident, and the compiler
enforces it rather than a review.

## Using it

```csharp
RuleSet rules = RuleSetBuilder.Create()
    .Add("free-search", r => r
        .WithPriority(10)
        .Matching(RequestFields.Tier, "free")
        .Matching(RequestFields.Endpoint, "/v1/search")
        .KeyedBy(RequestFields.ClientId)
        .Using(RateLimitAlgorithm.TokenBucket, limit: 5, window: TimeSpan.FromSeconds(10), burstCapacity: 8))
    .Add("catch-all", r => r
        .WithPriority(1000)
        .Using(RateLimitAlgorithm.FixedWindow, limit: 100, window: TimeSpan.FromMinutes(1)))
    .Build();

RateLimitEngine engine = new(new StaticRuleSource(rules), new InMemoryRateLimitStore());

RateLimitDecision decision = await engine.EvaluateAsync(new RequestContext
{
    ClientId = "acct-1",
    Tier = "free",
    Endpoint = "/v1/search",
});

if (!decision.IsAllowed)
{
    // decision.RuleName says which rule refused it; decision.RetryAfter says when to come back.
}
```

The same rules in JSON (`src/RateLimiter.Demo/rules.json`) produce identical `RateLimitRule`
objects — configuration is a *source* of rules, not the rule model. A test asserts it.

## The design in one page

**State lives in the store; algorithms are pure functions** of the state handed to them. This is the
decision everything else follows from. Stateful algorithms work perfectly on one node and
double-count the moment a second starts, with no error anywhere — and pure functions make boundary
conditions assertable with no store, no clock and no concurrency. ([ADR 0001](docs/adr/0001-limiter-state-lives-in-the-store.md))

**The store exposes only read and compare-and-swap.** No `Increment`, no `AddToSortedSet` — those
are algorithm concerns, and a store growing a method per algorithm is not an abstraction. Adding a
backend means implementing two methods; adding an algorithm needs no backend change.
([ADR 0002](docs/adr/0002-store-exposes-only-read-and-cas.md))

**Rules are first-match-wins over explicit priority**, so a rejection is explicable by one rule name.
The cost is that a broad rule placed early silently disables narrower ones, so the engine reports
shadowed rules by name. ([ADR 0004](docs/adr/0004-first-match-wins-rule-selection.md))

**Fail open by default, behind a circuit breaker.** A limiter that takes the API down when its cache
blinks has caused a bigger incident than the abuse it prevented — but a limiter that has stopped
limiting looks exactly like one that works, so `RateLimiterMetrics.StoreFailures` is the number to
alert on. ([ADR 0008](docs/adr/0008-fail-open-by-default-with-a-circuit-breaker.md))

## Three things this does not do

Stated here rather than left to be found:

1. **The Redis Lua is never executed by the tests.** The fake connection reproduces the script's
   semantics, which shows the adapter uses it correctly — not that the script is correct. Closing
   that needs an integration test against a real server.
2. **Leaky bucket decides nothing a token bucket would not.** Under an allow/reject interface the
   two are duals; every difference between them comes from queuing, which an API gateway cannot do.
   A test asserts the equivalence. ([ADR 0010](docs/adr/0010-leaky-bucket-is-a-teaching-artefact.md))
3. **The sliding window counter is an approximation.** It assumes the previous window's traffic was
   uniformly distributed, which bursty traffic is not. Use the log where precision matters.
