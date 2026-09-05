# ADR 0010 — Leaky bucket is kept as a teaching artefact, not as a distinct control

**Status:** accepted · **Date:** 2026-09-06

## Context

Five algorithms were implemented behind one interface. This ADR records a finding that emerged from
writing them, not a decision made before.

The delivery plan flagged, before implementation, that leaky bucket might not fit an allow/reject
interface cleanly — it is a traffic *shaper*, whose natural behaviour is to hold a request until the
queue drains rather than to refuse it. That risk was confirmed, and more strongly than expected.

## The finding

**Under an allow/reject interface, leaky bucket and token bucket are indistinguishable.**

They are duals. A token bucket tracks how much allowance remains; a leaky bucket tracks how full the
queue is. With equal capacity those are the same number counted from opposite ends, and both accrue
lazily at the same rate, so `tokens >= permits` and `depth + permits <= capacity` are the same
condition written twice.

Every published difference between them — the smooth output rate, the inability to burst after
idling — derives from the leaky bucket **queuing** what it cannot serve yet. An API gateway cannot
queue: holding a request consumes a connection and a thread, which converts a rate limit into a
latency problem. Strip the queue out, as this interface must, and nothing observable is left to tell
them apart.

`AlgorithmBehaviourTests.Leaky_bucket_and_token_bucket_are_indistinguishable_through_this_interface`
asserts exactly this, driving both through the same irregular burst pattern and requiring identical
decisions at every step.

## Decision

Keep the implementation. Do not present it as a distinct control.

## Alternatives rejected

**Remove it.** It decides nothing the token bucket would decide differently, so it is arguably dead
weight.

Rejected because the equivalence is itself the most interesting thing either algorithm teaches, and
deleting the implementation would delete the test that demonstrates it. The finding is worth more
than the code.

**Implement real queuing** — admit a request by delaying it rather than refusing it.

Rejected as the wrong shape for this library. It would require the interface to return "wait this
long, then proceed", and every caller to hold a request open meanwhile. That is a legitimate design
for bandwidth shaping or media streaming, which is where leaky bucket genuinely belongs, and it is
not what an API gateway wants.

## Consequences

- The enum keeps five members and the registry resolves all five.
- Anyone choosing `LeakyBucket` expecting different behaviour from `TokenBucket` will get identical
  behaviour. The XML documentation on `LeakyBucketAlgorithm` says so directly rather than leaving it
  to be discovered.
- The uniform-policy decision in `RateLimitPolicy` is partly vindicated by this: because both
  algorithms express the same entitlement in the same shape, the equivalence was demonstrable in a
  test rather than merely arguable.
