# ADR 0001 — Limiter state lives in the store, algorithms are pure functions

**Status:** accepted · **Date:** 2026-09-06

## Context

A rate limiter has to remember what each caller has consumed. Where that memory lives determines
almost everything else about the design: whether the limiter can be sharded, what a backend has to
provide, and — the part that matters day to day — whether the boundary conditions can be tested.

## Decision

An algorithm is a pure function:

```
(currentState, policy, now, permits) -> (nextState, decision)
```

It holds no fields. All state lives in the store, and the engine performs read → compute → 
compare-and-swap, retrying on a lost race.

## Alternatives rejected

**Stateful algorithms.** Each algorithm owns a `ConcurrentDictionary<string, Bucket>` and the store
is an optional persistence layer beneath it. Less code, reads more naturally, and every tutorial
implementation looks like this.

Rejected because it fails silently and late. It works perfectly on one node and begins admitting N
times the limit the moment a second node starts, with no error raised anywhere. The single-node
assumption is invisible in the code and is discovered in production. It also inverts the dependency
that the deployment architecture assumes, where several limiter nodes share one cache.

**The algorithm inside the store** (a Redis Lua script per algorithm). One round trip instead of
two, atomic by construction, no version and no retry. This is the right answer for a gateway at
very high volume on individual keys, and it is not a strawman.

Rejected because every algorithm would then exist twice: once in C# where every boundary is
unit-tested, and once in Lua where only a live server can test it. The two will drift, and the copy
that drifts is the one running in production. For a library whose stated purpose is that its
boundary conditions are provable, that is the wrong trade.

The rejection is partial rather than total — see [ADR 0002](0002-store-exposes-only-read-and-cas.md).

## Consequences

- Boundary conditions are asserted directly, with no store, no clock and no concurrency. The test
  that catches the classic off-by-one is three lines.
- Distribution becomes a store swap. `RedisRateLimitStoreTests` drives the same engine over the
  in-memory and Redis stores and asserts identical decisions.
- **Cost:** two round trips per admitted request instead of one, plus retries under contention. The
  engine bounds retries at `MaxWriteAttempts` and counts exhaustions in
  `RateLimiterMetrics.WriteContentionExhausted`, which is the number that would justify revisiting
  this decision.
- **Cost:** state must be serialisable, which shapes `LimiterState` — see
  [ADR 0003](0003-limiter-state-is-a-flat-union.md).
