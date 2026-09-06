# ADR 0003 — Limiter state is a flat union, not a type hierarchy

**Status:** accepted · **Date:** 2026-09-06

## Context

Five algorithms need different state. A fixed window needs a window start and a count; a token
bucket needs a token balance and a timestamp; the sliding window log needs a list. That state
crosses a process boundary into a shared store.

## Decision

One `LimiterState` record with every field. Each algorithm reads and writes the two or three it
needs and leaves the rest at their defaults.

## Alternatives rejected

**An abstract base with a derived state per algorithm.** Tidier, and it would make it impossible for
a token bucket to read a window counter.

Rejected on the boundary this type has to cross. State is serialised to a shared store, so a
polymorphic hierarchy makes every backend responsible for discriminated serialisation — writing a
type tag and dispatching on read. A backend that guesses wrong fails at read time, in production,
on a request. A flat record serialises the same way in every backend.

**Opaque `byte[]` or `string`, with each algorithm serialising its own state.** Maximum flexibility
and the store never needs to know anything.

Rejected because it moves serialisation into the core library, which would then need a serialiser,
and because every algorithm would carry format-versioning code of its own.

## Consequences

- Each entry carries roughly forty bytes of fields it does not use. Irrelevant next to the per-key
  dictionary and TTL overhead that dominates either way.
- Nothing but a test stops an algorithm reading a field it did not write, which is why each
  algorithm's state handling is tested in isolation.
- The record needed hand-written structural equality: `ImmutableArray<long>` compares by reference,
  so the compiler's generated `Equals` would have reported two identical states as different. A
  public record that advertises value semantics and does not have them is a trap for whoever writes
  the next backend, so it was fixed rather than documented.
