# ADR 0004 — Rules are selected first-match-wins over an explicit priority

**Status:** accepted · **Date:** 2026-09-06

## Context

Several rules can match one request: a free-tier rule, an endpoint rule, and a catch-all may all
apply. Something has to decide which limit is enforced.

## Decision

Rules carry an explicit integer `Priority`. They are evaluated in ascending priority order and the
**first** match decides the request. Exactly one rule decides any request, and its name appears on
the decision.

## Alternatives rejected

**All-match-and-intersect** — apply every matching rule and require the request to satisfy all of
them. This is what Envoy's rate-limit descriptors do, and it is genuinely safer: you cannot
accidentally widen a limit by adding a rule, because adding a rule can only ever constrain further.

Rejected on explicability. Under all-match, "why was this request rejected?" is a multi-rule answer
requiring the reader to reconstruct an intersection. Under first-match it is one rule name that fits
in a log line and on the demo screen. For a library whose purpose is to be understood, that decided
it — but it is the closest call in this design, and for a system where accidentally widening a limit
is a security incident, the other answer is correct.

**File order instead of an explicit priority.** Simpler to write.

Rejected because "why did this rule stop working?" would have the answer "someone inserted a line
above it", which is a class of incident worth designing out for the cost of one integer.

## Consequences

- The cost of first-match is that a broad rule placed early silently disables the narrower rules
  beneath it — precisely the failure the rejected design prevents. That cost is paid rather than
  deferred: `RuleSet.Create` detects shadowed rules and names both the victim and the shadower. A
  limit you believe you have and do not is the worst outcome available here.
- Making shadowing detectable constrains the match language. Conditions are AND across dimensions
  and OR within one, with no negation and no nesting, which is what makes `RuleMatch.Covers`
  decidable. A rules language rich enough to express anything would make reachability undecidable
  and the warning impossible.
- Ordering is total and stable — priority, then declaration order — so the same file always produces
  the same behaviour and the same diagnostics.
