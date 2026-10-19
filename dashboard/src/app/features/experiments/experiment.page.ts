import { Component, computed, inject, input, linkedSignal, resource, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, describeError } from '../../core/problem';
import { Breadcrumbs, ErrorBanner, Loading, StatusChip } from '../../shared/ui';
import { ExperimentsService } from './experiments.service';
import { VariationsEditor } from './variations-editor';
import { fromVariations, toVariations, validateVariations } from './variations';

type Action = 'start' | 'stop';

@Component({
  selector: 'app-experiment',
  imports: [RouterLink, Breadcrumbs, ErrorBanner, Loading, StatusChip, VariationsEditor],
  template: `
    <app-breadcrumbs
      [crumbs]="[
        { label: 'Projects', link: ['/projects'] },
        { label: project(), link: ['/projects', project()] },
        { label: environment(), link: ['/projects', project(), 'environments', environment()] },
        { label: key() },
      ]"
    />

    @if (experiment.isLoading() && !experiment.hasValue()) {
      <app-loading />
    } @else if (experiment.error() && !experiment.hasValue()) {
      <app-error [message]="describe(experiment.error())" />
    } @else if (experiment.value(); as current) {
      <div class="row spread">
        <div>
          <h1>{{ current.name }}</h1>
          <p class="muted"><span class="mono">{{ current.key }}</span> · version {{ current.version }}</p>
        </div>
        <div class="row">
          <app-status [status]="current.status" />
          <a class="btn primary" [routerLink]="['results']">View results</a>
        </div>
      </div>

      <app-error [message]="error()" />
      @if (stale()) {
        <div class="banner warn" role="alert">
          <strong>This experiment changed since you opened it.</strong>
          <button type="button" class="btn small" (click)="reload()">Reload the latest version</button>
        </div>
      }

      <section class="card stack" aria-labelledby="lifecycle">
        <h2 id="lifecycle">Lifecycle</h2>
        @switch (current.status) {
          @case ('Draft') {
            <p>This experiment is a draft: SDKs cannot see it yet. Starting it publishes it, and from then on its variations can no longer be changed.</p>
          }
          @case ('Running') {
            <p>This experiment is running and included in the datafile your SDKs download. Stopping it removes it from the datafile; it cannot be restarted.</p>
          }
          @default {
            <p>This experiment has stopped. Its results remain available.</p>
          }
        }

        @if (current.status === 'Draft' || current.status === 'Running') {
          @let action = current.status === 'Draft' ? 'start' : 'stop';
          @if (confirming() === action) {
            <div class="row" role="group" [attr.aria-label]="'Confirm ' + action">
              <strong>{{ action === 'start' ? 'Publish this experiment to your SDKs?' : 'Stop this experiment?' }}</strong>
              <button type="button" class="btn" [class.primary]="action === 'start'" [class.danger]="action === 'stop'" (click)="perform(action, current.version)" [disabled]="busy()">
                Yes, {{ action }}
              </button>
              <button type="button" class="btn" (click)="confirming.set(null)">Cancel</button>
            </div>
          } @else {
            <button type="button" class="btn" [class.primary]="action === 'start'" [disabled]="busy() || (action === 'start' && !!startProblem())" (click)="confirming.set(action)">
              {{ action === 'start' ? 'Start experiment' : 'Stop experiment' }}
            </button>
            @if (action === 'start' && startProblem(); as why) {
              <p class="hint">{{ why }}</p>
            }
          }
        }
      </section>

      <section class="card stack" aria-labelledby="variations">
        <h2 id="variations">Variations</h2>
        @if (current.status === 'Draft') {
          <app-variations-editor [(rows)]="rows" />
          @if (problem(); as text) {
            <p class="field-error" role="alert">{{ text }}</p>
          }
          <button type="button" class="btn primary" (click)="save(current.version)" [disabled]="busy() || !!problem() || !dirty()">Save variations</button>
        } @else {
          <div class="table-wrap">
            <table>
              <caption class="visually-hidden">Variations and their traffic share</caption>
              <thead>
                <tr><th scope="col">Key</th><th scope="col" class="num">Traffic</th></tr>
              </thead>
              <tbody>
                @for (v of current.variations; track v.key; let first = $first) {
                  <tr>
                    <td><span class="mono">{{ v.key }}</span> @if (first && v.key !== 'control') { <span class="chip">control</span> }</td>
                    <td class="num">{{ v.trafficBasisPoints / 100 }}%</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    }
  `,
})
export class ExperimentPage {
  private readonly service = inject(ExperimentsService);

  readonly project = input.required<string>();
  readonly environment = input.required<string>();
  readonly key = input.required<string>();

  private readonly scope = computed(() => ({ project: this.project(), environment: this.environment(), key: this.key() }));
  protected readonly experiment = resource({
    params: () => this.scope(),
    loader: ({ params }) => this.service.get(params, params.key),
  });

  /** The editable copy of the variations; reset whenever the experiment is (re)loaded. */
  protected readonly rows = linkedSignal(() => fromVariations(this.experiment.value()?.variations ?? []));
  protected readonly problem = computed(() => validateVariations(this.rows(), { minimum: 1 }));
  protected readonly startProblem = computed(() => validateVariations(fromVariations(this.experiment.value()?.variations ?? []), { minimum: 2 }));
  protected readonly dirty = computed(
    () => JSON.stringify(toVariations(this.rows())) !== JSON.stringify(this.experiment.value()?.variations ?? []),
  );

  protected readonly confirming = signal<Action | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly stale = signal(false);
  protected readonly describe = describeError;

  protected reload(): void {
    this.stale.set(false);
    this.error.set(null);
    this.experiment.reload();
  }

  protected save(version: number): Promise<void> {
    return this.run(() => this.service.replaceVariations(this.scope(), this.key(), version, toVariations(this.rows())));
  }

  protected perform(action: Action, version: number): Promise<void> {
    this.confirming.set(null);
    return this.run(() =>
      action === 'start' ? this.service.start(this.scope(), this.key(), version) : this.service.stop(this.scope(), this.key(), version),
    );
  }

  private async run(change: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.stale.set(false);
    try {
      await change();
      this.experiment.reload();
    } catch (e) {
      // 409 means somebody else changed it first; offer a reload instead of silently overwriting their work.
      this.stale.set(e instanceof ApiError && e.status === 409);
      this.error.set(describeError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
