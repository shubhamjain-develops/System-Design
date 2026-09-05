# ADR 0008 — Fail open by default, guarded by a circuit breaker

**Status:** accepted · **Date:** 2026-09-06

## Context

The store will be unreachable sometimes. When it is, the limiter cannot tell whether a caller is
over their limit and must decide anyway.

## Decision

`StoreFailurePolicy` is required configuration, defaulting to `FailOpen`. A `CircuitBreaker`
short-circuits calls once the store starts failing. Every decision made under a failure carries
`StoreFailureOccurred`, and `RateLimiterMetrics` counts them.

## Alternatives rejected

**Fail closed by default.** Enforcement is the limiter's job, so refusing when it cannot enforce is
the conservative reading.

Rejected as a default because it converts a cache outage into a full service outage. A limiter that
takes the whole API down when Redis blinks has caused a larger incident than the abuse it was
protecting against. It remains the correct setting for metered, billed or authenticating endpoints,
which is why it is configuration rather than a decision made here.

**A per-request timeout instead of a breaker.** Simpler, and it bounds each request.

Rejected because a timeout still pays the timeout. At a thousand requests per second against a 50ms
timeout, the limiter holds fifty seconds of latency per second and exhausts the connection pool of
the service it protects — the failure mode becomes the limiter. A breaker makes that a one-off cost
and, by not calling at all, lets an overloaded store recover instead of being held down by retries.

**Release all traffic when the breaker's timer expires.** One less state.

Rejected because it slams a recovering store with full production load, which is a common way to
knock it straight back over. Half-open admits exactly one trial request and only its result decides.

## Consequences

- Under `FailOpen`, the limiter stops limiting during an outage and nothing looks wrong — no error
  rate moves, no latency moves, every response says 200. `RateLimiterMetrics.StoreFailures` is the
  only signal, which makes it the number to alert on. The demo makes this visible: with
  `--fail-after`, a throttled client's strip flips from `#...#.....#` to `!!!!!!!!!!!!` while
  rejections stop climbing.
- Exhausting every compare-and-swap attempt routes through the same policy but is counted
  separately, and deliberately does **not** trip the breaker: contention means the store is alive and
  busy, not failing. The remedy differs too — contention argues for server-side evaluation
  ([ADR 0009](0009-cas-over-server-side-scripts.md)), not for repairing a store.
- Programming errors are excluded from the failure path. An unregistered algorithm propagates rather
  than being absorbed, because failing open on a bug hides it behind a limiter that appears to work.
