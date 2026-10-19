import { Component, computed, inject, input, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { describeError } from '../../core/problem';
import { KEY_HINT, isValidKey, slugify } from '../../shared/keys';
import { Breadcrumbs, ErrorBanner, Loading, StatusChip } from '../../shared/ui';
import { ExperimentsService } from './experiments.service';
import { VariationsEditor } from './variations-editor';
import { VariationRow, formatPercentValue, toVariations, validateVariations } from './variations';

@Component({
  selector: 'app-experiments',
  imports: [FormsModule, RouterLink, Breadcrumbs, ErrorBanner, Loading, StatusChip, VariationsEditor],
  template: `
    <app-breadcrumbs
      [crumbs]="[
        { label: 'Projects', link: ['/projects'] },
        { label: project(), link: ['/projects', project()] },
        { label: environment() },
      ]"
    />
    <div class="row spread">
      <h1>Experiments</h1>
      <a class="btn" [routerLink]="['/projects', project(), 'environments', environment(), 'keys']">SDK keys</a>
    </div>
    <p class="muted">Environment <span class="mono">{{ environment() }}</span></p>

    @if (experiments.isLoading()) {
      <app-loading />
    } @else if (experiments.error()) {
      <app-error [message]="describe(experiments.error())" />
    } @else if (experiments.value()?.length) {
      <div class="card table-wrap">
        <table>
          <caption class="visually-hidden">Experiments in this environment</caption>
          <thead>
            <tr>
              <th scope="col">Experiment</th>
              <th scope="col">Status</th>
              <th scope="col">Variations</th>
              <th scope="col"><span class="visually-hidden">Open</span></th>
            </tr>
          </thead>
          <tbody>
            @for (experiment of experiments.value(); track experiment.key) {
              <tr>
                <td>
                  <a [routerLink]="[experiment.key]"><strong>{{ experiment.name }}</strong></a>
                  <div class="mono muted">{{ experiment.key }}</div>
                </td>
                <td><app-status [status]="experiment.status" /></td>
                <td>
                  @for (v of experiment.variations; track v.key; let last = $last) {
                    <span class="mono">{{ v.key }}</span>&nbsp;<span class="muted">{{ share(v.trafficBasisPoints) }}%</span>@if (!last) {,&ensp;}
                  }
                </td>
                <td class="num"><a class="btn small" [routerLink]="[experiment.key, 'results']">Results</a></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else {
      <div class="card empty">
        <p><strong>No experiments yet.</strong></p>
        <p>Create one below, then start it to publish it to your SDKs.</p>
      </div>
    }

    <section class="card create" aria-labelledby="new-exp">
      <h2 id="new-exp">New experiment</h2>
      <p class="hint">Needs the Editor role. It starts as a draft and reaches SDKs only once you start it.</p>
      <app-error [message]="error()" />
      <form (submit)="$event.preventDefault(); create()">
        <div class="field">
          <label for="exp-name">Name</label>
          <input id="exp-name" name="name" type="text" [ngModel]="name()" (ngModelChange)="onName($event)" />
        </div>
        <div class="field">
          <label for="exp-key">Key</label>
          <input id="exp-key" name="key" type="text" class="mono" [ngModel]="key()" (ngModelChange)="onKey($event)" aria-describedby="exp-key-hint" />
          <div id="exp-key-hint" class="hint">Used by your code to ask for a decision. {{ keyHint }}</div>
        </div>
        <app-variations-editor [(rows)]="rows" />
        @if (problem(); as text) {
          @if (touched()) {
            <p class="field-error" role="alert">{{ text }}</p>
          }
        }
        <button class="btn primary" type="submit" [disabled]="busy() || !name().trim() || !keyValid()">Create experiment</button>
      </form>
    </section>
  `,
  styles: `
    .create {
      margin-top: 2rem;
      max-width: 640px;
    }
  `,
})
export class ExperimentsPage {
  private readonly service = inject(ExperimentsService);
  private readonly router = inject(Router);

  readonly project = input.required<string>();
  readonly environment = input.required<string>();

  private readonly scope = computed(() => ({ project: this.project(), environment: this.environment() }));
  protected readonly experiments = resource({ params: () => this.scope(), loader: ({ params }) => this.service.list(params) });

  protected readonly name = signal('');
  protected readonly key = signal('');
  protected readonly rows = signal<VariationRow[]>([
    { key: 'control', percent: 50 },
    { key: 'treatment', percent: 50 },
  ]);
  protected readonly busy = signal(false);
  protected readonly touched = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly keyHint = KEY_HINT;
  protected readonly keyValid = computed(() => isValidKey(this.key()));
  protected readonly problem = computed(() => validateVariations(this.rows(), { minimum: 1 }));
  protected readonly describe = describeError;

  private keyEdited = false;

  protected share(basisPoints: number): string {
    return formatPercentValue(basisPoints / 100);
  }

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
    this.touched.set(true);
    this.error.set(null);
    if (this.problem()) {
      return;
    }

    this.busy.set(true);
    try {
      const created = await this.service.create(this.scope(), this.key(), this.name().trim(), toVariations(this.rows()));
      await this.router.navigate(['/projects', this.project(), 'environments', this.environment(), 'experiments', created.key]);
    } catch (e) {
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
