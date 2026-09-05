# Architecture decision records

One record per real fork in the design. Each states what was chosen, what was rejected, and why —
the rejections deliberately at more length than the choice, because in a year the question is
usually "was this considered?" rather than "was this good?".

| # | Decision | The fork it settles |
|---|---|---|
| [0001](0001-limiter-state-lives-in-the-store.md) | State lives in the store; algorithms are pure functions | Whether a limiter can be sharded at all |
| [0002](0002-store-exposes-only-read-and-cas.md) | The store exposes only read and compare-and-swap | Whether the backend is an abstraction or five backends in a trench coat |
| [0003](0003-limiter-state-is-a-flat-union.md) | Limiter state is a flat union, not a hierarchy | What every backend has to serialise |
| [0004](0004-first-match-wins-rule-selection.md) | Rules are first-match-wins over explicit priority | Whether a rejection is explicable in one line |
| [0005](0005-limiter-keys-are-rule-scoped-and-escaped.md) | Keys are rule-scoped and escaped | Two quota-evasion vectors and one silent halving bug |
| [0006](0006-time-is-an-injected-dependency.md) | Time is injected | Whether boundary conditions can be tested at all |
| [0007](0007-configuration-is-a-source-not-the-model.md) | Configuration is a source of rules, not the model | Whether a broken file can disable rate limiting |
| [0008](0008-fail-open-by-default-with-a-circuit-breaker.md) | Fail open by default, guarded by a breaker | Whether a cache outage becomes a service outage |
| [0009](0009-cas-over-server-side-scripts.md) | Redis uses a CAS script, not per-algorithm scripts | Where the algorithm lives, and when to change that |
| [0010](0010-leaky-bucket-is-a-teaching-artefact.md) | Leaky bucket is a teaching artefact | A finding, not a decision: it is the token bucket in disguise |

## The two that are closest to being wrong

Worth knowing which decisions are contestable rather than settled.

**0004, first-match-wins.** All-match-and-intersect is safer in the specific sense that you cannot
widen a limit by adding a rule. First-match was chosen for explicability and pays for it with the
shadowed-rule diagnostic. In a system where accidentally widening a limit is a security incident
rather than an inconvenience, the other answer is correct.

**0009, CAS over server-side scripts.** Correct until contention on individual keys is high, at
which point it is simply worse. The record names the metric that would say so —
`WriteContentionExhausted`, not requests per second — so the decision can be revisited on evidence.

## Where the honesty is load-bearing

Three limits are stated in these records rather than left to be discovered:

- The Redis Lua is not executed by any test (0009).
- Leaky bucket decides nothing a token bucket would not (0010).
- Under fail-open, the limiter stops limiting and nothing looks wrong (0008).
