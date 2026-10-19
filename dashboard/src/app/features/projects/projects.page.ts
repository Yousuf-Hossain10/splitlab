import { Component, computed, inject, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { describeError } from '../../core/problem';
import { KEY_HINT, isValidKey, slugify } from '../../shared/keys';
import { Breadcrumbs, ErrorBanner, Loading } from '../../shared/ui';
import { ProjectsService } from './projects.service';

@Component({
  selector: 'app-projects',
  imports: [FormsModule, RouterLink, Breadcrumbs, ErrorBanner, Loading],
  template: `
    <app-breadcrumbs [crumbs]="[{ label: 'Projects' }]" />
    <h1>Projects</h1>

    @if (projects.isLoading()) {
      <app-loading />
    } @else if (projects.error()) {
      <app-error [message]="describe(projects.error())" />
    } @else {
      @if (projects.value()?.length) {
        <div class="grid">
          @for (project of projects.value(); track project.key) {
            <a class="card project" [routerLink]="['/projects', project.key]">
              <h2>{{ project.name }}</h2>
              <span class="mono muted">{{ project.key }}</span>
            </a>
          }
        </div>
      } @else {
        <div class="card empty">
          <p><strong>No projects yet.</strong></p>
          <p>A project holds your environments and experiments. Create the first one below.</p>
        </div>
      }
    }

    <section class="card create" aria-labelledby="new-project">
      <h2 id="new-project">New project</h2>
      <app-error [message]="error()" />
      <form (submit)="$event.preventDefault(); create()">
        <div class="field">
          <label for="name">Name</label>
          <input id="name" name="name" type="text" [ngModel]="name()" (ngModelChange)="onName($event)" />
        </div>
        <div class="field">
          <label for="key">Key</label>
          <input id="key" name="key" type="text" class="mono" [ngModel]="key()" (ngModelChange)="onKey($event)" aria-describedby="key-hint" />
          <div id="key-hint" class="hint">{{ keyHint }}</div>
          @if (key() && !keyValid()) {
            <div class="field-error" role="alert">That key is not valid.</div>
          }
        </div>
        <button class="btn primary" type="submit" [disabled]="busy() || !name().trim() || !keyValid()">Create project</button>
      </form>
    </section>
  `,
  styles: `
    .project {
      display: block;
      text-decoration: none;
      color: inherit;
      &:hover {
        border-color: var(--accent);
      }
    }
    .create {
      margin-top: 2rem;
      max-width: 520px;
    }
  `,
})
export class ProjectsPage {
  private readonly service = inject(ProjectsService);
  private readonly router = inject(Router);

  protected readonly projects = resource({ loader: () => this.service.list() });

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
      const project = await this.service.create(this.key(), this.name().trim());
      await this.router.navigate(['/projects', project.key]);
    } catch (e) {
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
