---
slug: url-shortener-v1
tier: 2
confidence_pre: 78
confidence_post: null
repo: System-Design
base: origin/main
branch: feat/url-shortener-v1
phase: branched
created: 2026-09-16
---

# Build a simple, in-memory URL shortener service (v1)

## 1. Original issue

> check the D:\Repos\System-Design\URLShortner\url shortner.excalidraw and design a very simple
> url shortner service, it needs to be very simple as mentioned in the diagram, we can use local
> variables to store the data, no need ofr redis or local variable.
>
> (Follow-up, after discussing an incremental roadmap and locking:) "build v1 first" / "go head,
> also push the changes to github after testing"

The diagram (`URLShortner/url shortner.excalidraw`, sourced from hellointerview.com) specifies:

- Functional requirements: submit a long URL and receive a short one; optionally provide a
  custom alias and/or expiration; access the original URL via the short URL.
- Non-functional requirements: eventual consistency for availability, scalable, low-latency
  redirects, short URLs must be unique.
- API routes: `GET /urls/{shorturl} -> long url`, `POST /urls -> creates a short url, accepts
  custom short url and exp time`.
- A "Simple Example" high-level design: Client -> Server -> DB only (no Redis, no load balancer,
  no sharding) — this is the shape the user asked for, explicitly rejecting the more elaborate
  Redis/sharding/lock design shown elsewhere in the same diagram.

## 2. What this change does

Before: `URLShortner/` contains only the design diagram; no service exists.
After: a small Node.js HTTP service exists that can shorten a URL and redirect from the short
code to the original URL, backed entirely by an in-memory `Map` (no database, no cache, no
external dependency, data lost on restart — matching the user's explicit "local variables"
instruction).

A user (or a script) can `POST /urls` with a long URL to get back a short code, then `GET` that
code and be redirected to the original URL, exactly as bitly's core loop works, minus every
scaling mechanism the diagram shows for a real deployment.

## 3. Where it touches

| File | What changes | Why |
|---|---|---|
| `URLShortner/package.json` | new | project metadata, `npm start` / `npm test` scripts, zero runtime dependencies |
| `URLShortner/src/store.js` | new | in-memory `Map`-backed store: create short entries (random or custom code), resolve a code to its long URL, expiration handling |
| `URLShortner/src/server.js` | new | built-in `http` server wiring `POST /urls` and `GET /:code` to the store |
| `URLShortner/test/store.test.js` | new | `node:test` unit tests for the store (creation, collision, custom alias, expiration, lookup) |
| `URLShortner/README.md` | new | how to run it and the two routes it exposes |

**Blast radius:** none outside the new `URLShortner/` folder. Nothing in `Ratelimiter/` or the
repo root is read or modified. No existing file is edited — every file listed is new.

**Deliberately NOT touched:**
- No `GET /urls/{shorturl}` JSON-lookup route alongside the redirect — the diagram documents
  both, but a redirect (`GET /:code` → `302`) is what functional requirement 3 actually needs
  (a user "accessing" the URL via the short link), and a second route returning the same data as
  JSON would be pure duplication for a "very simple" v1. Flagged to the user before starting;
  no objection raised.
- No Redis, no Postgres, no load balancer, no sharding, no cache-miss locking — all explicitly
  out of scope per the user's instruction and reserved for a later, separately-scoped step if
  ever wanted.
- No persistence across restarts — explicitly requested ("local variables ... no need for
  redis").

## 4. Tier and why

| Axis | Score | Evidence |
|---|---|---|
| Blast radius | T1 | 5 new files, one new folder, nothing existing edited |
| Reversibility | T1 | `git revert` / delete the folder undoes it completely |
| Unknowns | T1 | scope fully specified by the diagram plus this conversation; the one open question (redirect-only vs. also JSON lookup) was raised and resolved before implementation started |
| Verification | T2 | brand-new code path with no pre-existing tests — a new test must be written to prove it works |
| Contract | T1 | new API; nothing external currently depends on it |
| Data / security | T2 | accepts an arbitrary user-supplied URL (open-redirect-shaped input) and a user-supplied custom alias (path-segment input) |

**Tier 2, set by the verification and data/security axes.**

### Approach

Chosen: **plain Node.js, zero dependencies** — built-in `http`/`URL` modules for routing and
parsing, a plain `Map` for storage, `node:test` (built into Node 24, confirmed via `node
--version`) for tests.

Rejected:
- **Node.js + Express** — less routing/parsing boilerplate, but adds a dependency, which at tier
  2 requires its own separate approval step (security check 8) for a two-route service that does
  not need a framework.
- **Python + Flask** — equally simple in isolation, but introduces a third language into a
  two-project practice repo (the sibling `Ratelimiter` project is .NET) with no benefit over the
  zero-dependency Node option.

## 5. What good looks like

1. `POST /urls` with `{ "longUrl": "https://example.com/very/long/path" }` returns `201` with a
   `shortUrl` whose code resolves back to that long URL.
2. `POST /urls` with a `customAlias` uses that exact alias as the code; a second request reusing
   the same alias is rejected with `409`.
3. `POST /urls` with a non-URL `longUrl` (e.g. missing scheme) is rejected with `400`.
4. `GET /<code>` for a code that was just created responds `302` with `Location` set to the
   original long URL.
5. `GET /<code>` for an unknown code responds `404`.
6. `POST /urls` with `expiresInSeconds` in the past (or a very small value that elapses before
   the follow-up request) causes a subsequent `GET /<code>` to respond `410`, not `302`.
7. Restarting the process loses all previously created short URLs (no persistence) — the
   explicitly requested behavior, not a bug.

## 6. How it is verified

_Filled in during step 6, with real command output._

## 7. Risks and rollback

- **Risk:** short-code generation collides with an existing code under concurrent load.
  **Mitigation:** the store re-rolls on collision before inserting (checked against the same
  `Map`, synchronous, no `await` between check and insert — no race window in Node's
  single-threaded event loop); collision probability at v1's scale is irrelevant since the store
  is process-local and not shared.
- **Risk:** a custom alias or generated code collides with a reserved path (e.g. a future
  `/urls` route). **Mitigation:** reject aliases that collide with the service's own route
  prefix (`urls`) at creation time.
- **Risk:** the long URL is used for an open redirect to an attacker-controlled destination.
  **Mitigation:** this is inherent to what a URL shortener *is* (see security check 7) — the
  only affordable v1 mitigation is validating `longUrl` parses as `http:`/`https:` before
  accepting it, which is included.
- **Rollback:** delete the `URLShortner/src`, `URLShortner/test`, `URLShortner/package.json` —
  nothing else in the repo references them. Safe; no data migration, no external state.

## 8. Confidence

**Pre-implementation: 78/100**

| Judgment | Assessment | Evidence |
|---|---|---|
| Cause identified, not just symptom | Scope is a spec, not a bug — fully derived from the diagram's Functional Requirements, API Routes, and "Simple Example" sections | diagram text extracted via grep, quoted in §1 |
| Fix addresses the cause | The two routes and in-memory store map 1:1 onto the diagram's Simple Example flow and the three functional requirements | §3, §5 |
| No other call site has the same defect | N/A — greenfield, no other call sites exist | isolated new folder, confirmed via blast-radius review |
| Verification distinguishes fixed from unfixed | Will write `node:test` tests and mutation-check them (fail without the fix, pass with it) | to be recorded in §6 at step 6 |
| Blast radius fully examined | Confirmed no existing file is read or written by this change | `git status --porcelain` shows only pre-existing untracked diagram files |

**What keeps this below 100:** the redirect-only route design (skipping the diagram's separate
`GET /urls/{shorturl}` JSON route) was inferred from the functional requirements rather than
explicitly confirmed line-by-line by the user — they did not object when it was flagged, but
"did not object" is weaker evidence than "confirmed."

**What would raise it:** the tests demonstrating the real request path works end-to-end (not
just the store in isolation), which happens at step 6.

## 9. Security

Floor: tier 2. Results per `08-security.md`.

| # | Check | Result | Note |
|---|---|---|---|
| 1 | Secret scan | pending | run on clean tree before implementation, and again on final diff |
| 2 | Personal-data guard | pending | |
| 3 | Permission diff | pending | |
| 4 | No manifest/lockfile movement | n/a expected | zero dependencies — `package.json` will be created but with no `dependencies` block and no lockfile |
| 5 | Repo's own guards | n/a | `verify.security` not configured for this repo (no `.claude/delivery.json`, no CI) — recorded as a gap, not silently skipped |
| 6 | No hook bypass | pending | |
| 7 | Input-trust review | pending | `longUrl` (arbitrary string from an untrusted HTTP caller) and `customAlias`/path code (untrusted path segment) are the two input paths |
| 8 | Dependency review | n/a | no dependency added (zero-dependency approach chosen specifically to avoid this) |
| 9 | Authorization touchpoints | n/a | no auth in this service |
| 10 | Logging/output review | pending | |
| 11 | Egress review | n/a | no outbound network calls |
| 12 | `/security-review` | pending | run on branch diff before push gate |

**Open findings:** none yet — checks not yet run, recorded above as `pending`.

## 10. Post-implementation

_Appended after verification runs. Do not fill in before._
