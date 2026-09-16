'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { UrlStore, ValidationError, ConflictError } = require('../src/store');

test('create then resolve returns the same long URL', () => {
  const store = new UrlStore();
  const { code, longUrl } = store.create('https://example.com/very/long/path');
  assert.equal(longUrl, 'https://example.com/very/long/path');
  assert.match(code, /^[A-Za-z0-9]{7}$/);

  const result = store.resolve(code);
  assert.equal(result.status, 'ok');
  assert.equal(result.longUrl, 'https://example.com/very/long/path');
});

test('custom alias is used as the code', () => {
  const store = new UrlStore();
  const { code } = store.create('https://example.com/a', { customAlias: 'my-alias' });
  assert.equal(code, 'my-alias');
  assert.equal(store.resolve('my-alias').status, 'ok');
});

test('a taken custom alias is rejected with ConflictError', () => {
  const store = new UrlStore();
  store.create('https://example.com/a', { customAlias: 'taken' });
  assert.throws(
    () => store.create('https://example.com/b', { customAlias: 'taken' }),
    ConflictError,
  );
});

test('the reserved "urls" alias is rejected', () => {
  const store = new UrlStore();
  assert.throws(() => store.create('https://example.com/a', { customAlias: 'urls' }), ConflictError);
});

test('a non-absolute or non-http(s) longUrl is rejected', () => {
  const store = new UrlStore();
  assert.throws(() => store.create('not-a-url'), ValidationError);
  assert.throws(() => store.create('ftp://example.com/file'), ValidationError);
});

test('a non-positive expiresInSeconds is rejected', () => {
  const store = new UrlStore();
  assert.throws(() => store.create('https://example.com/a', { expiresInSeconds: 0 }), ValidationError);
  assert.throws(() => store.create('https://example.com/a', { expiresInSeconds: -5 }), ValidationError);
});

test('resolving an unknown code reports not_found', () => {
  const store = new UrlStore();
  assert.equal(store.resolve('doesNotExist').status, 'not_found');
});

test('an entry past its expiry reports expired, not ok', async () => {
  const store = new UrlStore();
  const { code } = store.create('https://example.com/a', { expiresInSeconds: 0.01 });
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(store.resolve(code).status, 'expired');
});
