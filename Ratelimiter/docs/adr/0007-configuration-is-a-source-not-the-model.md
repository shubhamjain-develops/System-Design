# ADR 0007 — Configuration is a source of rules, not the rule model

**Status:** accepted · **Date:** 2026-09-06

## Context

Rules must be definable in a file — the deployment architecture has a rule service publishing
`{limit, window, algorithm}` per route — and also in code, because tests should not need fixtures on
disk.

## Decision

JSON and a fluent builder both construct the same immutable `RateLimitRule`. `IRuleSource` supplies
an immutable `RuleSet` snapshot, read once per request.

## Alternatives rejected

**Deserialise JSON straight into the runtime types.** No parser to write, no duplication.

Rejected for error quality and coupling. A rules file is edited by a person under pressure, and
`rules[1].policy.limit must be a positive integer` is worth considerably more than a serialiser's
type mismatch at a JSON path. It also lets the wire format be a deliberate choice rather than a
shadow of C# property names — the shape can change without the file changing, and vice versa.

**Configuration objects as the rule model**, with the engine reading them directly.

Rejected because then every test needs a file or a JSON string, and the runtime semantics become
whatever the deserialiser happened to produce. Keeping configuration as a *source* means one set of
semantics to understand and one to test. A test asserts the builder and the parser produce equal
rules, which is what keeps that true rather than aspirational.

## Consequences

- Parsing never throws; every problem is a diagnostic. Reloads happen where an exception has nowhere
  useful to go, and the natural handling is to keep the previous rules — which should be a
  deliberate decision, not an exception handler.
- **A broken file must never disable rate limiting.** A failed reload keeps the previous rule set and
  increments `FailedReloadCount`. Falling back to an empty rule set would turn a typo into a gateway
  with no limits at all, where every response still says 200.
- Startup is the deliberate exception and throws: there is no previous good state, and starting
  unlimited is worse than not starting.
- A defect was found here during implementation: `RuleParseResult.Succeeded` originally checked only
  the rule set's own errors, and since unparseable rules are *skipped*, a document whose every rule
  was malformed produced an empty-but-valid rule set and reported success — the exact failure this
  ADR claims to prevent, arriving by an unconsidered path. Now any error diagnostic anywhere means
  the document was not understood.
- Polling is implemented rather than pub/sub because polling is the half that guarantees
  convergence: a missed push leaves a node serving stale limits forever with nothing to correct it.
  A push can be added in front without changing anything else.
