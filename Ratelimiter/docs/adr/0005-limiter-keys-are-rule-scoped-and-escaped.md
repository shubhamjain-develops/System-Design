# ADR 0005 — Limiter keys are scoped by rule and escaped

**Status:** accepted · **Date:** 2026-09-06

## Context

A rule says *how much*; the key says *who is counted together*. The key is built from
caller-supplied values.

## Decision

`{escaped rule name}|{field}={tag}{escaped value}|…`, where the tag distinguishes a present value
from an absent dimension. `RateLimitRule.KeyBy` chooses the dimensions; an empty list produces one
allowance shared across every matching request, which is how a genuine global limit is expressed.

## Alternatives rejected

**Key on the dimension values alone, without the rule name.** Shorter keys, and it seems harmless
because different rules are "obviously" different things.

Rejected because it is a silent correctness bug. Two rules that both key on client ID would share a
counter, so a caller subject to a 100/hour rule and a 10/minute rule has each rule consuming the
other's allowance — both limits effectively halved, with nothing in any output to indicate it. This
has a dedicated test, and the mutation check confirms that removing the rule name from the key fails
exactly that test and no other.

**Concatenate values without escaping.** Simpler.

Rejected as a quota-evasion vector. If a client can choose an ID containing the delimiter, it can
craft one whose key collides with another client's, spending their quota or hiding inside it.

**A reserved string to mark an absent dimension** (`"__absent__"` or similar).

Rejected because any reserved string is forgeable: a caller who learns the marker sends it as their
own client ID and joins the population of callers who omitted that dimension. A tag character before
every present value makes the absent encoding unreachable from caller input.

## Consequences

- Keys are longer than the minimum. Irrelevant next to the value they hold.
- `RateLimitDecision.LimiterKey` is exposed, so key isolation is observable in a test and in the
  demo rather than merely intended.
- Empty-string dimension values resolve as absent rather than as a distinct value, so a caller
  cannot obtain a second allowance by sending an empty header.
