'use strict';

const http = require('http');
const { UrlStore, ValidationError, ConflictError } = require('./store');

const PORT = process.env.PORT || 3000;
const MAX_BODY_BYTES = 10 * 1024;

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        reject(new ValidationError('request body too large'));
        req.destroy();
        return;
      }
      data += chunk;
    });
    req.on('end', () => {
      if (!data) return resolve({});
      try {
        resolve(JSON.parse(data));
      } catch {
        reject(new ValidationError('request body must be valid JSON'));
      }
    });
    req.on('error', reject);
  });
}

function sendJson(res, statusCode, body) {
  const payload = JSON.stringify(body);
  res.writeHead(statusCode, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(payload),
  });
  res.end(payload);
}

function createRequestHandler(store) {
  return async function handleRequest(req, res) {
    let pathname;
    try {
      pathname = new URL(req.url, `http://${req.headers.host || 'localhost'}`).pathname;
    } catch {
      return sendJson(res, 400, { error: 'invalid request URL' });
    }
    const segments = pathname.split('/').filter(Boolean);

    try {
      if (req.method === 'POST' && segments.length === 1 && segments[0] === 'urls') {
        const body = await readJsonBody(req);
        const result = store.create(body.longUrl, {
          customAlias: body.customAlias,
          expiresInSeconds: body.expiresInSeconds,
        });
        return sendJson(res, 201, {
          code: result.code,
          shortUrl: `http://${req.headers.host}/${result.code}`,
          longUrl: result.longUrl,
          expiresAt: result.expiresAt === null ? null : new Date(result.expiresAt).toISOString(),
        });
      }

      if (req.method === 'GET' && segments.length === 1) {
        const [code] = segments;
        const result = store.resolve(code);
        if (result.status === 'not_found') return sendJson(res, 404, { error: 'short url not found' });
        if (result.status === 'expired') return sendJson(res, 410, { error: 'short url expired' });
        res.writeHead(302, { Location: result.longUrl });
        return res.end();
      }

      return sendJson(res, 404, { error: 'not found' });
    } catch (err) {
      if (err instanceof ValidationError) return sendJson(res, 400, { error: err.message });
      if (err instanceof ConflictError) return sendJson(res, 409, { error: err.message });
      // Unexpected error: log for the operator, never leak err.message to the caller.
      console.error('unexpected error handling request:', err);
      return sendJson(res, 500, { error: 'internal server error' });
    }
  };
}

function createServer() {
  const store = new UrlStore();
  const server = http.createServer(createRequestHandler(store));
  return { server, store };
}

if (require.main === module) {
  const { server } = createServer();
  server.listen(PORT, () => {
    console.log(`url shortener listening on http://localhost:${PORT}`);
  });
}

module.exports = { createServer };
