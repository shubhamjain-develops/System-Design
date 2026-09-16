# URL Shortener — v1

A minimal URL shortener, built from the design in [`url shortner.excalidraw`](./url%20shortner.excalidraw).

v1 is deliberately the "Simple Example" slice of that diagram: a client talks to a server that
holds everything in a single in-memory `Map`. No database, no Redis, no load balancer, no
sharding — those are all shown in the fuller diagram as later scaling steps, not part of this
version. Data is lost whenever the process restarts; that's expected, not a bug.

## Running it

Requires Node.js 20+ (no other dependencies).

```bash
npm start          # listens on http://localhost:3000 (set PORT to override)
npm test           # runs the store and HTTP-level tests
```

## API

### `POST /urls`

Create a short URL.

```json
{
  "longUrl": "https://example.com/very/long/path",
  "customAlias": "my-alias",
  "expiresInSeconds": 3600
}
```

`customAlias` and `expiresInSeconds` are optional. Responds `201` with:

```json
{
  "code": "my-alias",
  "shortUrl": "http://localhost:3000/my-alias",
  "longUrl": "https://example.com/very/long/path",
  "expiresAt": "2026-09-16T12:00:00.000Z"
}
```

Errors: `400` for a missing/invalid `longUrl` or a malformed `expiresInSeconds`, `409` if
`customAlias` is already taken.

### `GET /:code`

Redirects (`302`) to the original long URL. `404` if the code is unknown, `410` if it has
expired.

## What's not here (yet)

Persistence, caching, horizontal scaling, and the cache-miss locking shown in the full diagram
are all out of scope for v1 — a single in-memory `Map` doesn't need them. See the diagram for
the fuller design if/when the service needs to survive a restart or run on more than one node.
