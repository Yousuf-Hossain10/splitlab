import { Injectable, computed, signal } from '@angular/core';

const STORAGE_KEY = 'splitlab.token';

/**
 * Holds the signed-in person's bearer token. It lives in sessionStorage, so it disappears when the tab closes and is
 * not shared across tabs. (A production setup should sign in through the identity provider; this is the token it would issue.)
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly _token = signal<string | null>(readStored());

  readonly token = this._token.asReadonly();

  /** Signed in, with a token that has not expired. */
  readonly isSignedIn = computed(() => {
    const token = this._token();
    return token !== null && !isExpired(token);
  });

  /** The token's `sub` claim, for display. */
  readonly subject = computed(() => {
    const token = this._token();
    return token === null ? null : (readClaims(token)?.sub ?? null);
  });

  signIn(token: string): void {
    const trimmed = token.trim();
    if (!trimmed) {
      throw new Error('A token is required.');
    }
    this._token.set(trimmed);
    store(trimmed);
  }

  signOut(): void {
    this._token.set(null);
    store(null);
  }
}

interface Claims {
  sub?: string;
  exp?: number;
}

/** Reads a JWT's claims without verifying it: the server is the authority; this is only for display and early expiry. */
export function readClaims(token: string): Claims | null {
  try {
    const payload = token.split('.')[1];
    if (!payload) {
      return null;
    }
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(payload.length / 4) * 4, '=');
    return JSON.parse(atob(base64)) as Claims;
  } catch {
    return null;
  }
}

export function isExpired(token: string, nowMs: number = Date.now()): boolean {
  const exp = readClaims(token)?.exp;
  return exp !== undefined && exp * 1000 <= nowMs;
}

function readStored(): string | null {
  try {
    return globalThis.sessionStorage?.getItem(STORAGE_KEY) ?? null;
  } catch {
    return null;
  }
}

function store(token: string | null): void {
  try {
    if (token === null) {
      globalThis.sessionStorage?.removeItem(STORAGE_KEY);
    } else {
      globalThis.sessionStorage?.setItem(STORAGE_KEY, token);
    }
  } catch {
    // Storage can be unavailable (private mode, blocked); the token then lasts for this page load only.
  }
}
