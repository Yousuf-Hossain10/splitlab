import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { ApiKey, IssuedApiKey } from '../../api/models';
import { toApiError } from '../../core/problem';
import { buttonByText, el, settle, text, type } from '../../testing';
import { KeysPage } from './keys.page';
import { KeysService } from './keys.service';

const active: ApiKey = { id: 'k1', name: 'web server', prefix: 'a1b2c3d4e5f6', permissions: ['config:read'], active: true };
const revoked: ApiKey = { id: 'k2', name: 'old', prefix: '0f0f0f0f0f0f', permissions: ['events:write'], active: false };
const issued = (secret: string): IssuedApiKey => ({ id: 'k3', name: 'web server', prefix: 'ffffffffffff', permissions: ['config:read'], secret });

describe('KeysPage', () => {
  let service: { list: ReturnType<typeof vi.fn>; create: ReturnType<typeof vi.fn>; revoke: ReturnType<typeof vi.fn>; rotate: ReturnType<typeof vi.fn> };

  function create() {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: KeysService, useValue: service }] });
    const fixture = TestBed.createComponent(KeysPage);
    fixture.componentRef.setInput('project', 'acme');
    fixture.componentRef.setInput('environment', 'production');
    return fixture;
  }

  beforeEach(() => {
    service = { list: vi.fn().mockResolvedValue([active, revoked]), create: vi.fn(), revoke: vi.fn().mockResolvedValue(undefined), rotate: vi.fn() };
  });

  it('lists keys by their non-secret prefix and shows which are revoked', async () => {
    const fixture = create();

    await settle(fixture);

    expect(service.list).toHaveBeenCalledWith({ project: 'acme', environment: 'production' });
    expect(text(fixture)).toContain('sl_a1b2c3d4e5f6…');
    expect(text(fixture)).toContain('web server');
    expect(text(fixture)).toContain('Revoked');
    // A revoked key has nothing left to do to it.
    expect(el(fixture).querySelectorAll('button[aria-label^="Rotate"]')).toHaveLength(1);
  });

  it('creates a key with the chosen permissions and shows its secret exactly once', async () => {
    service.create.mockResolvedValue(issued('sl_ffffffffffff_SECRETVALUE'));
    const fixture = create();
    await settle(fixture);

    type(fixture, '#key-name', 'web server');
    await settle(fixture);
    buttonByText(fixture, 'Create key')?.click();
    await settle(fixture);

    expect(service.create).toHaveBeenCalledWith({ project: 'acme', environment: 'production' }, 'web server', ['config:read', 'events:write']);
    expect(el(fixture).querySelector('[data-testid="secret"]')?.textContent).toBe('sl_ffffffffffff_SECRETVALUE');
    expect(text(fixture)).toContain('it is shown only once');
  });

  it('forgets the secret as soon as it is dismissed', async () => {
    service.create.mockResolvedValue(issued('sl_ffffffffffff_SECRETVALUE'));
    const fixture = create();
    await settle(fixture);
    type(fixture, '#key-name', 'web server');
    await settle(fixture);
    buttonByText(fixture, 'Create key')?.click();
    await settle(fixture);

    buttonByText(fixture, 'I have saved it')?.click();
    await settle(fixture);

    expect(text(fixture)).not.toContain('SECRETVALUE');
    expect(el(fixture).querySelector('[data-testid="secret"]')).toBeNull();
  });

  it('never puts a secret in the list of keys', async () => {
    const fixture = create();

    await settle(fixture);

    expect(text(fixture)).not.toMatch(/sl_[0-9a-f]{12}_/);
  });

  it('needs a name and at least one permission before it will create a key', async () => {
    const fixture = create();
    await settle(fixture);
    expect(buttonByText(fixture, 'Create key')?.disabled).toBe(true);

    type(fixture, '#key-name', 'x');
    await settle(fixture);
    expect(buttonByText(fixture, 'Create key')?.disabled).toBe(false);

    el(fixture).querySelector<HTMLInputElement>('#perm-config\\:read')?.click();
    el(fixture).querySelector<HTMLInputElement>('#perm-events\\:write')?.click();
    await settle(fixture);
    expect(buttonByText(fixture, 'Create key')?.disabled).toBe(true);
  });

  it('asks before revoking, and does nothing if the person cancels', async () => {
    const fixture = create();
    await settle(fixture);

    el(fixture).querySelector<HTMLButtonElement>('button[aria-label="Revoke web server"]')?.click();
    await settle(fixture);
    expect(service.revoke).not.toHaveBeenCalled();
    expect(text(fixture)).toContain('Yes, revoke');

    buttonByText(fixture, 'Cancel')?.click();
    await settle(fixture);
    expect(service.revoke).not.toHaveBeenCalled();
    expect(text(fixture)).not.toContain('Yes, revoke');
  });

  it('revokes once confirmed, then reloads the list', async () => {
    const fixture = create();
    await settle(fixture);

    el(fixture).querySelector<HTMLButtonElement>('button[aria-label="Revoke web server"]')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Yes, revoke')?.click();
    await settle(fixture);

    expect(service.revoke).toHaveBeenCalledWith({ project: 'acme', environment: 'production' }, 'k1');
    expect(service.list).toHaveBeenCalledTimes(2);
  });

  it('shows the replacement secret after a rotation', async () => {
    service.rotate.mockResolvedValue(issued('sl_ffffffffffff_ROTATED'));
    const fixture = create();
    await settle(fixture);

    el(fixture).querySelector<HTMLButtonElement>('button[aria-label="Rotate web server"]')?.click();
    await settle(fixture);

    expect(service.rotate).toHaveBeenCalledWith({ project: 'acme', environment: 'production' }, 'k1');
    expect(el(fixture).querySelector('[data-testid="secret"]')?.textContent).toBe('sl_ffffffffffff_ROTATED');
  });

  it('explains a refusal instead of failing silently', async () => {
    service.create.mockRejectedValue(toApiError(403, { title: 'Forbidden' }));
    const fixture = create();
    await settle(fixture);
    type(fixture, '#key-name', 'web');
    await settle(fixture);

    buttonByText(fixture, 'Create key')?.click();
    await settle(fixture);

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('You do not have access');
    expect(el(fixture).querySelector('[data-testid="secret"]')).toBeNull();
  });
});
