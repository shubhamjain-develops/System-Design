'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('../src/server');

async function withServer(fn) {
  const { server } = createServer();
  await new Promise((resolve) => server.listen(0, resolve));
  const { port } = server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await fn(baseUrl);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

test('POST /urls then GET the code redirects to the long URL', async () => {
  await withServer(async (baseUrl) => {
    const createRes = await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'https://example.com/very/long/path' }),
    });
    assert.equal(createRes.status, 201);
    const created = await createRes.json();
    assert.match(created.code, /^[A-Za-z0-9]{7}$/);
    assert.equal(created.shortUrl, `${baseUrl}/${created.code}`);

    const getRes = await fetch(`${baseUrl}/${created.code}`, { redirect: 'manual' });
    assert.equal(getRes.status, 302);
    assert.equal(getRes.headers.get('location'), 'https://example.com/very/long/path');
  });
});

test('POST /urls with a custom alias is retrievable at that alias', async () => {
  await withServer(async (baseUrl) => {
    const res = await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'https://example.com/a', customAlias: 'my-alias' }),
    });
    assert.equal(res.status, 201);
    const body = await res.json();
    assert.equal(body.code, 'my-alias');

    const getRes = await fetch(`${baseUrl}/my-alias`, { redirect: 'manual' });
    assert.equal(getRes.status, 302);
  });
});

test('a taken custom alias responds 409', async () => {
  await withServer(async (baseUrl) => {
    await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'https://example.com/a', customAlias: 'dup' }),
    });
    const res = await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'https://example.com/b', customAlias: 'dup' }),
    });
    assert.equal(res.status, 409);
  });
});

test('an invalid longUrl responds 400', async () => {
  await withServer(async (baseUrl) => {
    const res = await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'not-a-url' }),
    });
    assert.equal(res.status, 400);
  });
});

test('GET of an unknown code responds 404', async () => {
  await withServer(async (baseUrl) => {
    const res = await fetch(`${baseUrl}/doesNotExist`, { redirect: 'manual' });
    assert.equal(res.status, 404);
  });
});

test('GET of an expired code responds 410', async () => {
  await withServer(async (baseUrl) => {
    const createRes = await fetch(`${baseUrl}/urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longUrl: 'https://example.com/a', expiresInSeconds: 0.01 }),
    });
    const { code } = await createRes.json();
    await new Promise((resolve) => setTimeout(resolve, 30));
    const res = await fetch(`${baseUrl}/${code}`, { redirect: 'manual' });
    assert.equal(res.status, 410);
  });
});
