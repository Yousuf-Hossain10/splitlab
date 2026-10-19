import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { RouterStateSnapshot } from '@angular/router';
import { safeReturnUrl } from '../features/sign-in/sign-in.page';
import { KEY_PATTERN, isValidKey, slugify } from '../shared/keys';
import { authGuard } from './auth.guard';
import { AuthStore, isExpired, readClaims } from './auth.store';
import { ApiError, describeError, toApiError } from './problem';

/** A structurally valid JWT whose payload we control (the signature is never checked client-side). */
function jwt(payload: object): string {
  const encode = (o: object) => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${encode({ alg: 'none' })}.${encode(payload)}.sig`;
}

describe('toApiError', () => {
  it('uses the problem detail when the server gives one', () => {
    const error = toApiError(409, { title: 'Conflict', detail: 'Project "acme" already exists.' });

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(409);
    expect(error.message).toBe('Project "acme" already exists.');
  });

  it('falls back to the title, then to a sensible default', () => {
    expect(toApiError(400, { title: 'Invalid request' }).message).toBe('Invalid request');
    expect(toApiError(404, undefined).message).toBe('Not found');
    expect(toApiError(500, null).message).toBe('Request failed (500)');
  });

  it('does not reveal whether a forbidden thing exists', () => {
    expect(toApiError(403, { detail: 'secret detail' }).message).toBe('You do not have access to this, or it does not exist.');
  });

  it('accepts a plain text body', () => {
    expect(toApiError(400, 'subject is required').message).toBe('subject is required');
  });
});

describe('describeError', () => {
  it('describes an API error by its message', () => {
    expect(describeError(new ApiError(409, 'Conflict', 'Taken.'))).toBe('Taken.');
  });

  it('explains a network failure plainly', () => {
    expect(describeError(new TypeError('Failed to fetch'))).toContain('Could not reach the server');
  });

  it('copes with anything thrown', () => {
    expect(describeError(new Error('boom'))).toBe('boom');
    expect(describeError('weird')).toBe('Something went wrong.');
  });
});

describe('JWT helpers', () => {
  it('reads claims without verifying', () => {
    expect(readClaims(jwt({ sub: 'alice', exp: 4_000_000_000 }))).toEqual({ sub: 'alice', exp: 4_000_000_000 });
  });

  it('returns null for something that is not a token', () => {
    expect(readClaims('nonsense')).toBeNull();
    expect(readClaims('a.%%%.c')).toBeNull();
  });

  it('knows when a token has expired', () => {
    const now = Date.UTC(2026, 9, 20);

    expect(isExpired(jwt({ exp: now / 1000 - 1 }), now)).toBe(true);
    expect(isExpired(jwt({ exp: now / 1000 + 60 }), now)).toBe(false);
    expect(isExpired(jwt({ sub: 'no-expiry' }), now)).toBe(false);
  });
});

describe('AuthStore', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.resetTestingModule();
  });

  it('starts signed out', () => {
    const store = TestBed.inject(AuthStore);

    expect(store.isSignedIn()).toBe(false);
    expect(store.subject()).toBeNull();
  });

  it('signs in, exposes the subject, and remembers the token for this tab', () => {
    const token = jwt({ sub: 'alice', exp: 4_000_000_000 });
    const store = TestBed.inject(AuthStore);

    store.signIn(`  ${token}  `);

    expect(store.isSignedIn()).toBe(true);
    expect(store.subject()).toBe('alice');
    expect(store.token()).toBe(token);
    expect(sessionStorage.getItem('splitlab.token')).toBe(token);
  });

  it('restores a token stored earlier in the same tab', () => {
    sessionStorage.setItem('splitlab.token', jwt({ sub: 'bob', exp: 4_000_000_000 }));

    expect(TestBed.inject(AuthStore).subject()).toBe('bob');
  });

  it('treats an expired token as signed out', () => {
    const store = TestBed.inject(AuthStore);

    store.signIn(jwt({ sub: 'alice', exp: 1 }));

    expect(store.isSignedIn()).toBe(false);
  });

  it('signs out and forgets the token', () => {
    const store = TestBed.inject(AuthStore);
    store.signIn(jwt({ sub: 'alice', exp: 4_000_000_000 }));

    store.signOut();

    expect(store.isSignedIn()).toBe(false);
    expect(sessionStorage.getItem('splitlab.token')).toBeNull();
  });

  it('refuses an empty token', () => {
    expect(() => TestBed.inject(AuthStore).signIn('   ')).toThrow();
  });
});

describe('authGuard', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
  });

  const run = (url: string) =>
    TestBed.runInInjectionContext(() => authGuard({} as never, { url } as RouterStateSnapshot));

  it('lets a signed-in person through', () => {
    TestBed.inject(AuthStore).signIn(jwt({ sub: 'alice', exp: 4_000_000_000 }));

    expect(run('/projects')).toBe(true);
  });

  it('sends everyone else to sign-in and remembers where they were going', () => {
    const result = run('/projects/acme');
    const router = TestBed.inject(Router);

    expect(router.serializeUrl(result as never)).toBe('/sign-in?returnUrl=%2Fprojects%2Facme');
  });
});

describe('safeReturnUrl', () => {
  it('follows same-site paths', () => {
    expect(safeReturnUrl('/projects/acme')).toBe('/projects/acme');
  });

  it('refuses anything that could leave the site', () => {
    expect(safeReturnUrl('https://evil.example')).toBe('/projects');
    expect(safeReturnUrl('//evil.example')).toBe('/projects');
    expect(safeReturnUrl('/\\evil.example')).toBe('/projects');
    expect(safeReturnUrl('javascript:alert(1)')).toBe('/projects');
    expect(safeReturnUrl(undefined)).toBe('/projects');
    expect(safeReturnUrl('')).toBe('/projects');
  });
});

describe('keys', () => {
  it('matches the rules the API enforces', () => {
    for (const ok of ['a', 'checkout_flow', 'a-b', '0abc', 'x'.repeat(64)]) {
      expect(isValidKey(ok)).toBe(true);
    }
    for (const bad of ['', 'A', 'has space', '-lead', '_lead', 'x'.repeat(65), 'ünï']) {
      expect(isValidKey(bad)).toBe(false);
    }
    expect(KEY_PATTERN.source).toBe('^[a-z0-9][a-z0-9_-]{0,63}$');
  });

  it('suggests a key from a name', () => {
    expect(slugify('Checkout Flow v2')).toBe('checkout-flow-v2');
    expect(slugify('  Spaces   &   symbols!! ')).toBe('spaces-symbols');
    expect(slugify('Café Menü')).toBe('cafe-menu');
    expect(slugify('***')).toBe('');
  });

  it('always suggests something valid or nothing', () => {
    for (const name of ['Checkout Flow v2', 'A', '  x  ', 'a'.repeat(200), 'Ünïcode']) {
      const key = slugify(name);
      expect(key === '' || isValidKey(key)).toBe(true);
    }
  });
});
