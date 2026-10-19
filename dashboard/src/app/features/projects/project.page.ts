import { Component, computed, inject, input, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/problem';
import { KEY_HINT, isValidKey, slugify } from '../../shared/keys';
import { Breadcrumbs, ErrorBanner, Loading } from '../../shared/ui';
import { ProjectsService } from './projects.service';

@Component({
  selector: 'app-project',
  imports: [FormsModule, RouterLink, Breadcrumbs, ErrorBanner, Loading],
  template: `
    <app-breadcrumbs [crumbs]="[{ label: 'Projects', link: ['/projects'] }, { label: project() }]" />

    @if (details.isLoading()) {
      <app-loading />
    } @else if (details.error()) {
      <app-error [message]="describe(details.error())" />
      <p><a routerLink="/projects">Back to projects</a></p>
    } @else {
      <h1>{{ details.value()?.name }}</h1>
      <p class="muted mono">{{ project() }}</p>

      <h2>Environments</h2>
      @if (environments.error()) {
        <app-error [message]="describe(environments.error())" />
      } @else if (environments.value()?.length) {
        <div class="grid">
          @for (env of environments.value(); track env.key) {
            <div class="card">
              <h3>{{ env.name }}</h3>
              <p class="mono muted">{{ env.key }} · config v{{ env.configVersion }}</p>
              <div class="row">
                <a class="btn small primary" [routerLink]="['/projects', project(), 'environments', env.key]">Experiments</a>
                <a class="btn small" [routerLink]="['/projects', project(), 'environments', env.key, 'keys']">SDK keys</a>
              </div>
            </div>
          }
        </div>
      } @else if (!environments.isLoading()) {
        <div class="card empty">
          <p><strong>No environments yet.</strong></p>
          <p>An environment, such as production, is where experiments run. Create one to get started.</p>
        </div>
      }

      <section class="card create" aria-labelledby="new-env">
        <h2 id="new-env">New environment</h2>
        <p class="hint">Needs the Admin role.</p>
        <app-error [message]="error()" />
        <form (submit)="$event.preventDefault(); create()">
          <div class="field">
            <label for="env-name">Name</label>
            <input id="env-name" name="name" type="text" [ngModel]="name()" (ngModelChange)="onName($event)" />
          </div>
          <div class="field">
            <label for="env-key">Key</label>
            <input id="env-key" name="key" type="text" class="mono" [ngModel]="key()" (ngModelChange)="onKey($event)" aria-describedby="env-key-hint" />
            <div id="env-key-hint" class="hint">{{ keyHint }}</div>
          </div>
          <button class="btn primary" type="submit" [disabled]="busy() || !name().trim() || !keyValid()">Create environment</button>
        </form>
      </section>
    }
  `,
  styles: `
    .create {
      margin-top: 2rem;
      max-width: 520px;
    }
  `,
})
export class ProjectPage {
  private readonly service = inject(ProjectsService);

  /** Bound from the `:project` route parameter. */
  readonly project = input.required<string>();

  protected readonly details = resource({ params: () => this.project(), loader: ({ params }) => this.service.get(params) });
  protected readonly environments = resource({ params: () => this.project(), loader: ({ params }) => this.service.environments(params) });

  protected readonly name = signal('');
  protected readonly key = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly keyHint = KEY_HINT;
  protected readonly keyValid = computed(() => isValidKey(this.key()));
  protected readonly describe = describeError;

  private keyEdited = false;

  protected onName(value: string): void {
    this.name.set(value);
    if (!this.keyEdited) {
      this.key.set(slugify(value));
    }
  }

  protected onKey(value: string): void {
    this.keyEdited = true;
    this.key.set(value);
  }

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.service.createEnvironment(this.project(), this.key(), this.name().trim());
      this.name.set('');
      this.key.set('');
      this.keyEdited = false;
      this.environments.reload();
    } catch (e) {
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
