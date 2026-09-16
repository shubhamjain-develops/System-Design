'use strict';

const CODE_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
const CODE_LENGTH = 7;
// "urls" is the create-route path; reserving it stops a short code from ever shadowing that route.
const RESERVED_CODES = new Set(['urls']);
const ALIAS_PATTERN = /^[A-Za-z0-9_-]{3,32}$/;

class ValidationError extends Error {}
class ConflictError extends Error {}

function randomCode() {
  let code = '';
  for (let i = 0; i < CODE_LENGTH; i++) {
    code += CODE_ALPHABET[Math.floor(Math.random() * CODE_ALPHABET.length)];
  }
  return code;
}

function assertValidLongUrl(longUrl) {
  if (typeof longUrl !== 'string' || longUrl.length === 0) {
    throw new ValidationError('longUrl is required');
  }
  let parsed;
  try {
    parsed = new URL(longUrl);
  } catch {
    throw new ValidationError('longUrl must be an absolute URL');
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new ValidationError('longUrl must use http or https');
  }
  return parsed;
}

function assertValidExpiry(expiresInSeconds) {
  if (expiresInSeconds === undefined) return;
  if (typeof expiresInSeconds !== 'number' || !Number.isFinite(expiresInSeconds) || expiresInSeconds <= 0) {
    throw new ValidationError('expiresInSeconds must be a positive number');
  }
}

// In-memory only, per the requested design: data lives in this Map and is
// gone on restart. No database, no cache, no external process.
class UrlStore {
  constructor() {
    this._entries = new Map(); // code -> { longUrl, expiresAt }
  }

  create(longUrl, { customAlias, expiresInSeconds } = {}) {
    const parsed = assertValidLongUrl(longUrl);
    assertValidExpiry(expiresInSeconds);

    let code;
    if (customAlias !== undefined) {
      if (!ALIAS_PATTERN.test(customAlias)) {
        throw new ValidationError('customAlias must be 3-32 characters of letters, digits, - or _');
      }
      if (RESERVED_CODES.has(customAlias) || this._entries.has(customAlias)) {
        throw new ConflictError(`alias "${customAlias}" is already in use`);
      }
      code = customAlias;
    } else {
      // Synchronous check-then-insert on a single-threaded event loop: no
      // await between the has() check and the set() below, so there is no
      // window for a concurrent request to race this.
      do {
        code = randomCode();
      } while (RESERVED_CODES.has(code) || this._entries.has(code));
    }

    const expiresAt = expiresInSeconds !== undefined ? Date.now() + expiresInSeconds * 1000 : null;
    this._entries.set(code, { longUrl: parsed.toString(), expiresAt });
    return { code, longUrl: parsed.toString(), expiresAt };
  }

  resolve(code) {
    const entry = this._entries.get(code);
    if (!entry) return { status: 'not_found' };
    if (entry.expiresAt !== null && entry.expiresAt <= Date.now()) {
      this._entries.delete(code);
      return { status: 'expired' };
    }
    return { status: 'ok', longUrl: entry.longUrl };
  }
}

module.exports = { UrlStore, ValidationError, ConflictError };
