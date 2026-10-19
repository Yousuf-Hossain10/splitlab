import { Component, computed, inject, input, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { IssuedApiKey } from '../../api/models';
import { describeError } from '../../core/problem';
import { Breadcrumbs, ErrorBanner, Loading, StatusChip } from '../../shared/ui';
import { KeysService } from './keys.service';

const PERMISSIONS = [
  { value: 'config:read', label: 'Read configuration', hint: 'Lets an SDK download the experiment datafile and make decisions.' },
  { value: 'events:write', label: 'Send events', hint: 'Lets an SDK report exposures and conversions.' },
] as const;

@Component({
  selector: 'app-keys',
  imports: [FormsModule, Breadcrumbs, ErrorBanner, Loading, StatusChip],
  template: `
    <app-breadcrumbs
      [crumbs]="[
        { label: 'Projects', link: ['/projects'] },
        { label: project(), link: ['/projects', project()] },
        { label: environment(), link: ['/projects', project(), 'environments', environment()] },
        { label: 'SDK keys' },
      ]"
    />
    <h1>SDK keys</h1>
    <p class="muted">
      Keys belong to one environment (<span class="mono">{{ environment() }}</span>). Put one in your application so its SDK can read configuration and send events. Managing keys needs the Admin role.
    </p>

    <app-error [message]="error()" />

    @if (issued(); as key) {
      <section class="banner info secret" role="status" aria-labelledby="secret-title">
        <strong id="secret-title">Copy this key now: it is shown only once</strong>
        <p>Splitlab stores only a fingerprint, so it cannot show this key again. If you lose it, rotate the key to get a new one.</p>
        <div class="row">
          <code class="value" data-testid="secret">{{ key.secret }}</code>
          <button type="button" class="btn small" (click)="copy(key.secret)">{{ copied() ? 'Copied' : 'Copy' }}</button>
        </div>
        <button type="button" class="btn small" (click)="dismiss()">I have saved it</button>
      </section>
    }

    @if (keys.isLoading()) {
      <app-loading />
    } @else if (keys.error()) {
      <app-error [message]="describe(keys.error())" />
    } @else if (keys.value()?.length) {
      <div class="card table-wrap">
        <table>
          <caption class="visually-hidden">SDK keys for this environment</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Key</th>
              <th scope="col">Permissions</th>
              <th scope="col">Status</th>
              <th scope="col"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (k of keys.value(); track k.id) {
              <tr>
                <td>{{ k.name }}</td>
                <td class="mono">sl_{{ k.prefix }}…</td>
                <td>
                  @for (p of k.permissions; track p) {
                    <span class="chip">{{ p }}</span>
                  }
                </td>
                <td><app-status [status]="k.active ? 'Running' : 'Revoked'" /></td>
                <td class="num">
                  @if (k.active) {
                    @if (confirming() === k.id) {
                      <span class="row" role="group" [attr.aria-label]="'Confirm revoke ' + k.name">
                        <button type="button" class="btn small danger" (click)="revoke(k.id)" [disabled]="busy()">Yes, revoke</button>
                        <button type="button" class="btn small" (click)="confirming.set(null)">Cancel</button>
                      </span>
                    } @else {
                      <span class="row">
                        <button type="button" class="btn small" (click)="rotate(k.id)" [disabled]="busy()" [attr.aria-label]="'Rotate ' + k.name">Rotate</button>
                        <button type="button" class="btn small danger" (click)="confirming.set(k.id)" [disabled]="busy()" [attr.aria-label]="'Revoke ' + k.name">Revoke</button>
                      </span>
                    }
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else {
      <div class="card empty">
        <p><strong>No keys yet.</strong></p>
        <p>Create one to connect an SDK to this environment.</p>
      </div>
    }

    <section class="card create" aria-labelledby="new-key">
      <h2 id="new-key">New key</h2>
      <form (submit)="$event.preventDefault(); create()">
        <div class="field">
          <label for="key-name">Name</label>
          <input id="key-name" name="name" type="text" placeholder="e.g. web server" [ngModel]="name()" (ngModelChange)="name.set($event)" />
        </div>
        <fieldset>
          <legend>Permissions</legend>
          @for (p of permissionOptions; track p.value) {
            <label class="check" [for]="'perm-' + p.value">
              <input [id]="'perm-' + p.value" type="checkbox" [checked]="selected().includes(p.value)" (change)="toggle(p.value, $any($event.target).checked)" />
              <span>{{ p.label }} <span class="muted">— {{ p.hint }}</span></span>
            </label>
          }
        </fieldset>
        <button class="btn primary" type="submit" [disabled]="busy() || !name().trim() || selected().length === 0">Create key</button>
      </form>
    </section>
  `,
  styles: `
    .create {
      margin-top: 2rem;
      max-width: 640px;
    }
    .secret {
      margin-bottom: 1rem;
    }
    .value {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 0.35rem 0.6rem;
      overflow-wrap: anywhere;
      color: var(--text);
    }
  `,
})
export class KeysPage {
  private readonly service = inject(KeysService);

  readonly project = input.required<string>();
  readonly environment = input.required<string>();

  private readonly scope = computed(() => ({ project: this.project(), environment: this.environment() }));
  protected readonly keys = resource({ params: () => this.scope(), loader: ({ params }) => this.service.list(params) });

  protected readonly permissionOptions = PERMISSIONS;
  protected readonly name = signal('');
  protected readonly selected = signal<string[]>(['config:read', 'events:write']);
  protected readonly issued = signal<IssuedApiKey | null>(null);
  protected readonly copied = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly describe = describeError;

  protected toggle(permission: string, on: boolean): void {
    this.selected.update((current) => (on ? [...new Set([...current, permission])] : current.filter((p) => p !== permission)));
  }

  protected create(): Promise<void> {
    return this.run(async () => {
      this.issued.set(await this.service.create(this.scope(), this.name().trim(), this.selected()));
      this.name.set('');
    });
  }

  protected rotate(id: string): Promise<void> {
    return this.run(async () => this.issued.set(await this.service.rotate(this.scope(), id)));
  }

  protected revoke(id: string): Promise<void> {
    this.confirming.set(null);
    return this.run(() => this.service.revoke(this.scope(), id));
  }

  protected async copy(secret: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(secret);
      this.copied.set(true);
    } catch {
      this.error.set('Could not copy automatically. Select the key and copy it by hand.');
    }
  }

  protected dismiss(): void {
    this.issued.set(null);
    this.copied.set(false);
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.copied.set(false);
    try {
      await action();
      this.keys.reload();
    } catch (e) {
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
