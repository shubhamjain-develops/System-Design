# ADR 0006 — Time is an injected dependency

**Status:** accepted · **Date:** 2026-09-06

## Context

Every algorithm here is defined in terms of elapsed time, and the bugs in rate limiters cluster at
window boundaries.

## Decision

`IClock` supplies the current instant. Nothing in the library calls `DateTimeOffset.UtcNow`
directly. `ManualClock` ships in the main library rather than in the test project, because the demo
also needs to control time.

## Alternatives rejected

**Read the ambient clock.** No interface, no injection, no ceremony.

Rejected because it makes boundary tests sleep. A test for "the request one tick before the window
closes is admitted" would have to wait for a real window, which makes the suite slow and — far worse
— flaky, since the assertion then depends on the scheduler waking the thread in time. A flaky
boundary test is worse than no boundary test, because it trains the reader to re-run a red suite
instead of reading it. With a clock seam the same assertion is exact and instant.

**Monotonic time (`Stopwatch`) instead of wall time.** Immune to NTP corrections, which is a real
problem: a backwards clock jump can credit a token bucket with permits it never earned.

Rejected because monotonic tick counts are meaningful only within one process, and this state is
shared between processes. The concern is handled directly instead: every elapsed-time calculation
clamps at zero, so a backwards jump can stall refill briefly but can never mint permits. There is a
test for it.

## Consequences

- Boundary behaviour is asserted exactly, including the half-open window edge and the one-tick-before
  case.
- The full test suite runs in about 100ms despite covering multi-hour idle periods.
- `ManualClock` is thread-safe, because concurrency tests need many readers while one thread
  advances time, and a torn read would surface as a limiter bug rather than a clock bug.
