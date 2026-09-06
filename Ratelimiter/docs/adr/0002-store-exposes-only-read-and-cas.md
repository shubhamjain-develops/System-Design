# ADR 0002 — The store exposes only read and compare-and-swap

**Status:** accepted · **Date:** 2026-09-06

## Context

Given [ADR 0001](0001-limiter-state-lives-in-the-store.md), the store must support read-modify-write
without lost updates. The question is what it should offer to make that possible.

## Decision

Two methods, both algorithm-agnostic:

```csharp
ValueTask<StoreEntry?> ReadAsync(string key, CancellationToken ct);
ValueTask<bool> TryWriteAsync(string key, long expectedVersion, LimiterState nextState, TimeSpan ttl, CancellationToken ct);
```

`expectedVersion == 0` asserts the key is absent, so a create race is adjudicated by the same
mechanism as an update race. TTL is a required argument, not an option.

## Alternatives rejected

**A richer interface** — `IncrementAsync`, `AddToSortedSetAsync`, `RefillAndTakeAsync`. Each
algorithm would get an operation shaped for it, and each would cost one round trip instead of two.

Rejected because it is not an abstraction. A store growing a method per algorithm is five backends
wearing one interface, and adding a sixth algorithm means changing every backend that already
exists. The narrowness is the property that makes the interface worth having.

**Locking** — the store hands out a per-key lock. Easier to read than a CAS loop.

Rejected because no distributed backend can hold a lock across a network call without introducing
lease expiry, fencing tokens and a whole failure model of its own. The in-memory store would then be
testing a concurrency model no other implementation could reproduce.

## Consequences

- Adding a backend means implementing two methods. Adding an algorithm requires no backend change
  at all.
- Every operation is single-key, so Redis Cluster's hash-slot routing places a caller's state on one
  shard with no coordination — sharding falls out of the interface rather than being designed for.
- **Cost, and it is real:** the sliding window log's state is a list, so it is read and rewritten
  whole where a Redis-native sorted set would append and trim in place. This is the algorithm that
  pays most for the abstraction.
- **TTL is a correctness bound, not a memory hint.** An entry reclaimed early resets the caller to
  "never seen", which for a bucket means handing back a full bucket mid-drain — a bypass. Each
  algorithm computes a TTL that outlasts the point at which its state is equivalent to no state.
  An earlier draft of the interface documentation claimed TTL was "never a source of correctness";
  that was wrong and was corrected when the algorithms were written.
