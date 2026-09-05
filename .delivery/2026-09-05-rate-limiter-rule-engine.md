---
slug: rate-limiter-rule-engine
tier: 3
confidence_pre: 78
confidence_post: null
repo: System-Design
base: origin/main
branch: feat/rate-limiter-rule-engine
phase: branched
created: 2026-09-05
---

# Build a configurable rate-limiter library with a pluggable rule engine

## 1. Original issue

> create Ratelimiter folder and the I want to build a Rate Limiter library as a learning project,
> with a proper rule engine — not just one algorithm, but a configurable system that applies
> different limits based on rules. This is for my own interview prep, so I want to deeply
> understand every design decision, not just have it built for me.
>
> Before writing any code, please ask me clarifying questions about:
>
> Language — C# or Java (I'm open to either, help me decide if it matters for this exercise)
> Which rate-limiting algorithms to support (token bucket, sliding window log, sliding window
> counter, fixed window) — should these be pluggable strategies behind a common interface?
> How rules should be defined — e.g., different limits per client ID, per API endpoint, or per
> user tier — and whether that should be config-driven (JSON/YAML) or code-based
> Whether to support only in-memory storage for local testing, or also design a pluggable
> interface for a distributed backend (e.g., Redis) even if we stub it out
> Testing framework and how much test coverage I should aim for (xUnit/NUnit for C#, JUnit for Java)
> Whether I want a simple local demo/console harness that simulates a burst of requests so I can
> visually see allow/reject decisions in real time
>
> Once we agree on the design, build it incrementally: project structure first, then the core
> algorithms behind a shared interface, then the rule engine that selects/applies the right
> limiter, then tests, then the local demo. Explain your design decisions and trade-offs as you
> go — I want to be able to defend every choice in a technical interview, not just say "the AI
> built it."

**Answers given at intake (2026-09-05):** C#/.NET · all five algorithms · JSON config *and* code
builder · pluggable store with in-memory + stubbed Redis · xUnit at ~90% on algorithms and engine
· console demo with live burst view · ADRs plus inline rationale · user installs the .NET SDK.

## 2. What this change does

**Before:** the repo holds one Excalidraw diagram (`Rate-Limiter.excalidraw`) describing a rate
limiter — algorithms, Redis key layout, a rule-engine cache, and a session summary of five bugs
found in the pseudocode. None of it is executable. The reasoning exists only as diagram text.

**After:** a `Ratelimiter/` .NET solution that is the executable form of that diagram. A user can
edit a JSON rules file to give, say, `free`-tier clients 5 requests per 10s on `/search` under a
fixed window while `/payments` uses a sliding window log, run the console demo, and watch a burst
of simulated traffic be allowed or rejected per client in real time — seeing which rule matched
and which algorithm decided. Each real design fork is recorded as an ADR stating what was
rejected and why.

## 3. Where it touches

Greenfield. Every path below is new; no existing file is modified.

| File | What changes | Why |
|---|---|---|
| `Ratelimiter/RateLimiter.sln` | new | Solution root the user asked for |
| `Ratelimiter/src/RateLimiter.Core/` | new | Algorithms, store abstraction, rule engine, clock |
| `Ratelimiter/src/RateLimiter.Redis/` | new | Stubbed Redis store adapter |
| `Ratelimiter/src/RateLimiter.Demo/` | new | Console burst-visualisation harness |
| `Ratelimiter/tests/RateLimiter.Core.Tests/` | new | xUnit suite |
| `Ratelimiter/docs/adr/*.md` | new | One ADR per design fork |
| `.claude/delivery.json` | new | Quest config — **committed separately, not on this branch** |

**Blast radius:** none. Nothing in the repo imports this; `README.md` and the `.excalidraw` file
are untouched. The only shared surface is the repo root gaining one directory.

**Deliberately NOT touched:**
- `Rate-Limiter.excalidraw` — it is the source design and stays the record of the user's own
  reasoning. The library implements it; it does not replace it.
- `README.md` — left alone until the library exists and there is something true to describe.
- No live Redis, no Docker, no integration-test container (explicitly rejected at intake).
- No ASP.NET middleware / DelegatingHandler integration. Tempting and small, but it drags in a
  web framework dependency for a library whose point is the algorithms and the rule engine.

## 4. Tier and why

| Axis | Score | Evidence |
|---|---|---|
| Blast radius | T2 | ~30 new files across 4 projects; but zero existing consumers — nothing can break because nothing calls it |
| Reversibility | T1 | `git revert`, or delete `Ratelimiter/`. No data, no external side effects, no deploy |
| Unknowns | **T3** | Requires choosing between genuinely different designs: where limiter state lives (algorithm vs store), what atomicity primitive the store exposes, and rule-match semantics (first-match vs all-match). These are not answerable by reading — they are decisions |
| Verification | T2 | New xUnit suite plus manual exercise via the demo harness. No production observability because there is no production |
| Contract | T2 | Creates a public library API and a JSON rule schema. Creating a contract, not changing one — no existing consumer can be broken |
| Data / security | T2 | No PII, no secrets, no money. But rate limiting *is* quota enforcement, and the user's own notes already found an off-by-one at `count==5` that let unlimited requests through. A boundary bug here is an enforcement bypass |

**Tier 3, set by the Unknowns axis.**

Note on the hard-escalator list: "public API / serialized schema" and "quota" both appear there.
Neither fires as a *hard* escalator here — the API and schema are being created rather than
changed, and the quota is simulated with no money attached. Tier 3 is reached on the Unknowns
axis regardless, so the classification is unaffected; recording this so the reasoning is
reviewable rather than assumed.

---

### Design (Phase A)

#### A1. Problem statement

The user has done the design thinking already — the Excalidraw file contains the algorithm
trade-offs, the Redis key layout, the rule-engine-cache-with-pub/sub architecture, the fail-open
decision, and a list of five bugs they found in their own pseudocode. What it does not contain is
any resolution of the questions that only appear when you try to *run* it:

- Where does limiter state live, such that the same algorithm works on one node and on twenty?
- What is the minimum atomicity primitive a backing store must provide?
- When two rules both match a request, which one wins, and how does a person debug that?

The cost of the gap is specific: in an interview the user can currently describe a rate limiter
but cannot demonstrate that their design survives contact with concurrency, and cannot answer
"how would you shard this?" with anything they have actually built.

#### A2. Alternatives — where does state live?

This is the central fork; everything else follows from it.

**Option 1 — stateful algorithms.** Each algorithm owns its own storage (e.g. a
`ConcurrentDictionary<string, TokenBucket>`), and the store is an optional persistence layer
behind it.
- *Build cost:* lowest. Each algorithm is self-contained and obvious.
- *Living cost:* high. The algorithm and its storage are welded together, so making it
  distributed means rewriting every algorithm, not swapping a component.
- *How it fails:* silently and late. It works perfectly on one node, then double-counts the
  moment a second instance starts, because each node holds its own buckets.

**Option 2 — stateless algorithms as pure state transitions, atomicity in the store.** An
algorithm is a pure function `(currentState, policy, now, permits) -> (newState, decision)`. All
state lives in the store, which provides read + compare-and-swap + TTL.
- *Build cost:* medium. Needs a serialisable state model and a CAS-retry loop.
- *Living cost:* low. Algorithms are pure, so they are trivially unit-testable with no store at
  all, and a new backend is one class implementing two methods.
- *How it fails:* under high contention on a single hot key, CAS retries burn round trips. The
  user's own session notes already reached this conclusion — *"MULTI/WATCH/EXEC is optimistic
  locking — needs a retry loop, not ideal for hot path."*

**Option 3 — push the algorithm into the store.** Each algorithm ships as a server-side script
(Redis Lua), one round trip, atomic by construction.
- *Build cost:* highest, and doubled — every algorithm exists twice, once in C# for the in-memory
  path and once in Lua.
- *Living cost:* the two copies drift, and the Lua copy is the one that runs in production while
  the C# copy is the one under test.
- *How it fails:* a bug fixed in C# and not in Lua, discovered in production.

#### A3. The choice, and the rejections

**Chosen: Option 2.**

The deciding argument is testability of the thing most likely to be wrong. Algorithm boundary
conditions are where the bugs live — the user has already found an off-by-one at `count==5` in
their own pseudocode. Pure functions make those conditions directly assertable with no clock, no
store, and no concurrency, which means the boundary cases can be tested exhaustively and cheaply.

**Why not Option 1:** it makes the single most interesting property of the design —
shardability — untestable, because the storage decision is buried inside each algorithm. It also
inverts the dependency the user's own diagram assumes, where multiple RL nodes share one cache.

**Why not Option 3:** the duplication is the disqualifier. It is the right answer for a
production gateway at scale and the wrong answer for a learning project, because the copy under
test would not be the copy that runs.

**The rejection is not total.** Option 3's advantage is real and the CAS retry cost is a genuine
weakness of the choice, so `RateLimiter.Redis` will carry the Lua script for the token bucket
alongside the CAS path — documented, tested for shape, not executed against a live server. That
turns "we chose CAS" into "we chose CAS, here is what we would have written instead, and here is
the traffic level at which we would switch."

**Consequent decision — the store interface.** Two methods, both algorithm-agnostic:
`ReadAsync(key)` and `TryWriteAsync(key, expected, next, ttl)` returning false on version
mismatch. The interface deliberately does *not* expose `Increment` or `AddToSortedSet`, because
those are algorithm concerns; a store that needed a new method per algorithm would not be an
abstraction. Cost, recorded honestly: sliding-window-log state is a list of timestamps rather
than a scalar, so it is heavier to serialise through this interface than it would be through a
Redis-native sorted set. That is the price of the abstraction and it belongs in the ADR.

**Consequent decision — rule matching.** First-match-wins over a priority-ordered list, with a
mandatory default rule. Rejected: all-match-and-intersect (what Envoy's descriptors do), which is
safer against accidentally widening a limit but makes "why was this request rejected?" a
multi-rule answer. First-match keeps the answer to that question a single rule name, which the
demo can print. To cover the rejected option's advantage, the engine validates rules at load and
reports any rule made unreachable by an earlier broader one.

**Consequent decision — the limiter key includes the rule identity.** `{ruleName}:{dimension}:
{value}`, not just `{value}`. Without it, two rules keyed on the same client ID share a counter
and silently halve both limits. This gets an explicit test.

**Consequent decision — `IClock` everywhere.** No algorithm reads `DateTimeOffset.UtcNow`
directly. Time-based tests use a `FakeClock` and assert on boundaries rather than sleeping.
Sleep-based tests for a time-windowed algorithm are both slow and flaky, and flaky tests on
boundary conditions are worse than no tests because they train you to re-run.

#### A4. Rollout and migration

Not applicable in the usual sense: nothing is running, there is no data to migrate and no
cutover. The honest statement of this section is that the "rollout" is `git revert`, and the
point of no return does not exist. Recording it rather than deleting it, because a tier 3 that
quietly drops a section is indistinguishable from one that overlooked it.

#### A5. Observability

There is no production, so this section is scoped to what makes the library's behaviour visible
to the person running it:

- `RateLimitDecision` carries `RuleName`, `AlgorithmKind`, `RemainingPermits`, and `RetryAfter` —
  so every allow/reject can state *why*, not just *what*. Healthy value: every decision in a demo
  run names a rule; a decision naming the default rule when a specific one was expected is the
  signal that rule matching is wrong.
- The demo prints a per-client running tally of allowed vs rejected. Healthy value: under a burst
  larger than capacity, token bucket shows allows tapering as the bucket drains; fixed window
  shows a sharp reset at the boundary. If both look identical, the algorithm dispatch is not
  actually dispatching.
- Store failures increment a counter surfaced in the demo, so a fail-open event is visible rather
  than silent. A silent fail-open is the failure mode that matters: the limiter stops limiting
  and nothing says so.

#### A6. Threat model

Per `08-security.md` items 13-17, at design time where they can still change the design.

- **Enforcement bypass (the real one).** A boundary off-by-one lets a client exceed its limit.
  This is not hypothetical — the user found exactly this bug at `count==5` in their own
  pseudocode. Mitigation: every algorithm gets explicit at-limit, one-under, and one-over tests,
  and the mutation check is run on at least one of them.
- **Key collision / key injection.** A client-supplied ID containing the key delimiter could
  collide with another client's key, letting one client consume or evade another's quota.
  Mitigation: delimiter-safe key construction, plus a test using an ID that contains the
  delimiter.
- **Unbounded memory growth.** Per-key state with no eviction is a memory exhaustion vector — the
  user's notes already flag "No TTL → unbounded Redis memory growth." Mitigation: TTL is part of
  the store interface signature, not an optional afterthought, and the in-memory store evicts.
- **Fail-open is a deliberate availability-over-enforcement trade.** When the store is down, the
  limiter stops limiting. That is the user's documented choice and it is the right default for a
  gateway, but it must be explicit and configurable rather than emergent, and it must be visible
  when it happens. Mitigation: `OnStoreFailure` is a required option with no silent default
  behaviour, and fail-open events are counted and surfaced.
- **No secrets.** No credentials, connection strings, or tokens are introduced; the Redis adapter
  is stubbed and takes no real connection string. Verified at implementation by the secret scan.

---

### Decomposition (Phase B)

| Slice | What it does | Tier | Own pre-score | Depends on |
|---|---|---|---|---|
| 1 | Solution scaffold: 4 projects, `Directory.Build.props`, nullable + warnings-as-errors | T1 | 88 | — |
| 2 | Core primitives: `RequestContext`, `RateLimitPolicy`, `RateLimitDecision`, `IClock`/`FakeClock` | T1 | 85 | 1 |
| 3 | Store abstraction + in-memory CAS store with TTL eviction, concurrency tests | T2 | 76 | 2 |
| 4 | Five algorithms as pure state transitions, with boundary tests | T2 | 80 | 2 |
| 5 | Rule engine: model, priority matcher, key selector, unreachable-rule validation | T2 | 78 | 2, 4 |
| 6 | Rule sources: JSON schema + parser, fluent code builder, snapshot reload | T2 | 79 | 5 |
| 7 | Redis adapter: CAS path + documented Lua script, no live server | T2 | 72 | 3 |
| 8 | Resilience: fail-open/fail-closed policy + circuit breaker | T2 | 77 | 3 |
| 9 | Console demo with live burst view | T1 | 82 | all |
| 10 | ADRs for each fork in A3 | T1 | 90 | all |

**Overall pre-score is the minimum across slices, not the average: 72**, set by slice 7.

Slice ordering note: the tree is buildable and testable after every slice. Slices 7 and 8 are
deliberately after the demo's dependencies are otherwise satisfied, so that if slice 7 stalls,
slices 1-6 plus 9 still constitute a working library.

## 5. What good looks like

1. `dotnet build -c Release` succeeds with zero warnings (warnings are errors).
2. `dotnet test` passes, with ≥90% line coverage on `RateLimiter.Core` algorithms and rule engine.
3. All five algorithms — fixed window, sliding window log, sliding window counter, token bucket,
   leaky bucket — are selectable purely by changing the `algorithm` field in the JSON rules file,
   with no code change and no recompile of the library.
4. A request matching two rules is limited by the higher-priority one, and the returned decision
   names that rule.
5. Two different rules keyed on the same client ID maintain independent counters.
6. Each algorithm allows exactly `limit` requests and rejects the `limit+1`-th within a window —
   the off-by-one class of bug the original diagram identified.
7. Loading a rules file where a broad rule precedes a narrower one reports the narrower rule as
   unreachable rather than silently ignoring it.
8. With the store forced to fail, `FailOpen` allows requests and `FailClosed` rejects them, and
   the demo displays a non-zero store-failure count in both cases.
9. Running the demo shows a burst of requests being allowed then rejected per client in real
   time, labelled with the matched rule and dispatched algorithm.
10. `RateLimiter.Core` has no project reference to `RateLimiter.Redis` — the store abstraction is
    proven by dependency direction, not by assertion.
11. One ADR exists per fork in A3, each naming the rejected alternatives and why they lost.

## 6. How it is verified

| # | Verifies | Command or manual step | Expected |
|---|---|---|---|
| 1 | §5.1 | `dotnet build -c Release` | exit 0, no warnings |
| 2 | §5.2 | `dotnet test` | all pass; coverage report ≥90% on target assemblies |
| 3 | §5.3 | Test that runs one fixed request set against all five algorithm values, asserting divergent decision sequences | each algorithm produces a distinct, expected sequence |
| 4 | §5.4 | Rule-priority unit test | decision's `RuleName` is the higher-priority rule |
| 5 | §5.5 | Key-isolation unit test, two rules one client ID | counters independent; neither depletes the other |
| 6 | §5.6 | Per-algorithm boundary tests at `limit-1`, `limit`, `limit+1` | allow, allow, reject — for all five |
| 7 | §5.6 | **Mutation check:** invert one boundary comparison, re-run | the boundary test fails; restored, it passes |
| 8 | §5.7 | Load a rules file with a shadowed rule | validation reports the unreachable rule by name |
| 9 | §5.8 | Failing-store test under both policies | FailOpen allows, FailClosed rejects, failure counter > 0 |
| 10 | §5.9 | `dotnet run --project src/RateLimiter.Demo` — manual, with output captured | live view shows allow→reject transition per client with rule and algorithm labels |
| 11 | §5.10 | `dotnet list src/RateLimiter.Core package` and grep the `.csproj` for ProjectReference | no reference to the Redis project |
| 12 | §5.11 | `ls Ratelimiter/docs/adr/` and read each | one ADR per A3 fork, each with a rejections section |

## 7. Risks and rollback

- **Risk:** the CAS-retry store livelocks or degrades badly under a hot key with high
  concurrency → **Mitigation:** bounded retry count with a defined behaviour on exhaustion
  (treated as a store failure and routed through the `OnStoreFailure` policy), plus a concurrency
  test that hammers a single key from many tasks and asserts the total allowed never exceeds the
  limit.
- **Risk:** the pure-state-transition model fits the four counter-based algorithms but strains on
  sliding window log, whose state is an unbounded list → **Mitigation:** cap retained timestamps
  at the policy limit and prune on every evaluation; if this proves to distort behaviour, that is
  a design finding worth reporting, not worth hiding.
- **Risk:** leaky bucket is a traffic *shaper* (queue with constant drain), not an admission
  controller, so it may not fit the allow/reject interface cleanly → **Mitigation:** implement it
  as a virtual queue whose depth is state, and document the semantic difference in its ADR. If
  the interface has to bend to fit it, that is an ADR finding.
- **Risk:** ~90% coverage becomes a target that is gamed with shallow tests → **Mitigation:** the
  mutation check is the real gate; coverage is reported, not chased.
- **Rollback:** `git revert` the branch, or delete `Ratelimiter/`. Nothing else in the repo
  references it, no data is written outside the process, no external service is contacted. The
  undo is itself safe — there is no partial state a revert could leave behind. To be *rehearsed*,
  not merely asserted, at Phase D.

## 8. Confidence

**Pre-implementation: 78/100** for the quest as a whole; **72** as the gated number, taken as the
minimum across slices per the tier 3 rule.

| Judgment | Assessment | Evidence |
|---|---|---|
| Requirement identified, not just restated | Strong | The user's own diagram supplies the spec; the four intake answers resolve every stated option. Nothing about the goal is inferred |
| The design addresses that requirement | Strong | A3's chosen option directly enables §5.3 and §5.10, the two criteria that distinguish "a rate limiter" from "a configurable rate-limiting system" |
| No sibling gap left unaddressed | Moderate | The five bugs listed in the user's session summary each map to a §5 criterion or an A6 threat. Not yet verified that the list is exhaustive |
| Verification distinguishes working from broken | Moderate | Boundary tests plus the mutation check (§6.7) are genuinely discriminating. But §5.9 (the demo) is verified by eye — a human judgement, not an assertion |
| Blast radius fully examined | Strong | Greenfield with zero consumers; confirmed by `git log` showing one commit and the tree containing only `README.md` and the `.excalidraw` file |

**What keeps this below 100:** three named unknowns, in order of weight.

1. **The .NET SDK is not installed at scoring time.** `dotnet --list-sdks` returns "No SDKs were
   found" — only runtimes 8.0.21, 8.0.26 and 10.0.11 are present. Until the user's install
   completes, *every* command in section 6 is unrun, and a plan whose verification has never
   executed is a plan, not a result. This is the single largest drag on the number.
2. **CAS-retry behaviour under contention is predicted, not measured.** The design argues it is
   acceptable for a learning library; slice 3's concurrency test will be the first real evidence,
   and slice 7 carries the lowest slice score (72) for the related reason that the Redis adapter
   is verified for shape without a live server.
3. **Leaky bucket may not fit the common interface cleanly.** It is a shaper, not an admission
   controller. This is flagged as a risk rather than resolved, because resolving it requires
   writing it.

**What would raise it:** the SDK install completing and `dotnet test` running green against real
boundary tests, with the mutation check demonstrated on at least one algorithm. That single event
addresses unknown 1 outright and gives the first measurement for unknown 2.

## 9. Security

Floor: tier 3. Full results recorded per `08-security.md` as each check runs.

| # | Check | Result | Note |
|---|---|---|---|
| 1-12 | Tier 1 + 2 floor | pending | Run per slice (1-6) and once on the composed branch (7-12) |
| 13-17 | Threat model | **done at design time** | Recorded in A6 above, before implementation, where it can still change the design |

**Open findings:** none at classification. A6 identifies four design-time concerns; all four are
mitigated in the design rather than deferred, and each has a corresponding §5 criterion or test.

## 10. Post-implementation

_Appended after verification runs. Do not fill in before._
