import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthStore } from '../../core/auth.store';
import { ApiError } from '../../core/problem';
import { buttonByText, el, settle, text, type } from '../../testing';
import { SignInPage } from './sign-in.page';
import { SignInService } from './sign-in.service';

function jwt(sub: string): string {
  const encode = (o: object) => btoa(JSON.stringify(o)).replace(/=+$/, '');
  return `${encode({ alg: 'none' })}.${encode({ sub, exp: 4_000_000_000 })}.sig`;
}

describe('SignInPage', () => {
  let service: { developmentToken: ReturnType<typeof vi.fn> };
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  function create(returnUrl?: string) {
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: SignInService, useValue: service }] });
    navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SignInPage);
    fixture.componentRef.setInput('returnUrl', returnUrl);
    return fixture;
  }

  beforeEach(() => {
    service = { developmentToken: vi.fn() };
  });

  it('signs in through the development issuer and goes where the person was headed', async () => {
    service.developmentToken.mockResolvedValue(jwt('alice'));
    const fixture = create('/projects/acme');
    await settle(fixture);

    type(fixture, '#subject', 'alice');
    await settle(fixture);
    buttonByText(fixture, 'Sign in')?.click();
    await settle(fixture);

    expect(service.developmentToken).toHaveBeenCalledWith('alice');
    expect(TestBed.inject(AuthStore).subject()).toBe('alice');
    expect(navigateByUrl).toHaveBeenCalledWith('/projects/acme');
  });

  it('refuses to be sent off-site by a crafted return link', async () => {
    service.developmentToken.mockResolvedValue(jwt('alice'));
    const fixture = create('https://evil.example/phish');
    await settle(fixture);

    type(fixture, '#subject', 'alice');
    await settle(fixture);
    buttonByText(fixture, 'Sign in')?.click();
    await settle(fixture);

    expect(navigateByUrl).toHaveBeenCalledWith('/projects');
  });

  it('explains what is missing instead of silently ignoring an empty submit', async () => {
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Sign in')?.click();
    await settle(fixture);

    expect(buttonByText(fixture, 'Sign in')?.disabled).toBe(false); // Enter must always do something
    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('Enter a name');
    expect(service.developmentToken).not.toHaveBeenCalled();
  });

  it('explains what is missing when no token was pasted', async () => {
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Use token')?.click();
    await settle(fixture);

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('Paste a token');
    expect(TestBed.inject(AuthStore).isSignedIn()).toBe(false);
  });

  it('says plainly when the server has no development sign-in, and does not sign in', async () => {
    service.developmentToken.mockRejectedValue(new ApiError(404, 'No development issuer', 'This server has no development sign-in. Paste a token from your identity provider instead.'));
    const fixture = create();
    await settle(fixture);

    type(fixture, '#subject', 'alice');
    await settle(fixture);
    buttonByText(fixture, 'Sign in')?.click();
    await settle(fixture);

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('Paste a token');
    expect(TestBed.inject(AuthStore).isSignedIn()).toBe(false);
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('accepts a pasted token', async () => {
    const fixture = create();
    await settle(fixture);

    type(fixture, '#token', `  ${jwt('carol')}  `);
    await settle(fixture);
    buttonByText(fixture, 'Use token')?.click();
    await settle(fixture);

    expect(TestBed.inject(AuthStore).subject()).toBe('carol');
    expect(navigateByUrl).toHaveBeenCalledWith('/projects');
    expect(text(fixture)).toContain('Use a token');
  });
});
