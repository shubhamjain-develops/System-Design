# ADR 0009 — Redis uses a compare-and-swap script, not per-algorithm scripts

**Status:** accepted · **Date:** 2026-09-06

## Context

Redis has no native compare-and-swap. [ADR 0002](0002-store-exposes-only-read-and-cas.md) requires
one, so something server-side is unavoidable — the only question is how much goes there.

## Decision

One small Lua script that sets a key only if its stored version matches. The algorithm stays in C#.
`RedisScripts.TokenBucketWholeAlgorithm` — the whole algorithm evaluated server-side — is included
in the source, documented, and **not used**.

## Alternatives rejected

**`WATCH`/`MULTI`/`EXEC`.** No script at all, using only core Redis commands.

Rejected because it is optimistic locking that needs its own retry loop on top of the one the engine
already runs, and the failure modes compose badly. The script is the smaller of the two.

**A script per algorithm** (the rejected half of [ADR 0001](0001-limiter-state-lives-in-the-store.md),
made concrete). One round trip instead of two, atomic by construction, no version, no retry, immune
to hot-key contention. At sufficient contention on a single key this is simply the better
implementation, and pretending otherwise would be dishonest.

Rejected because the algorithm would exist twice — once in C# where every boundary is unit-tested,
once in Lua where only a live server can test it — and the copy that drifts is the one in
production.

The script is kept in the source anyway, because "was this considered?" ages better than "was this
good?", and because a reader deserves to see the alternative rather than a claim that it was
weighed.

## When to revisit

The crossover is **contention, not throughput**. CAS costs two round trips plus retries; the script
costs one and never retries. Spread across many keys, CAS scales as well as anything. The signal is
`RateLimiterMetrics.WriteContentionExhausted` rising — concurrent writers to a *single* key frequent
enough that races are commonly lost. A global limit, or one very large customer on a shared key,
gets there first. Requests per second is the wrong number to watch.

## Consequences

- Adding an algorithm requires no Lua and no Redis change.
- **Stated limit:** the Lua is never executed by the test suite. `FakeRedisConnection` reproduces the
  script's semantics in C#, which establishes that the adapter uses the script correctly, not that
  the script is correct. Only a live Redis can establish the latter, and an integration test against
  a container is the work that would close it. This is why this area carried the lowest confidence
  score in the delivery plan.
- One test asserts the exact script text is what gets sent — the thin thread joining what is tested
  here to what would run there.
- The stored format puts the version in a plain prefix so the script compares a substring rather
  than parsing JSON in Lua, which is where server-side scripts stop being cheap.
