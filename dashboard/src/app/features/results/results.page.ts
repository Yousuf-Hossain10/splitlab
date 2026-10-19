import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Subject } from 'rxjs';
import { isValidKey } from '../../shared/keys';
import { Breadcrumbs, ErrorBanner, Loading } from '../../shared/ui';
import { ExperimentsService } from '../experiments/experiments.service';
import { IntervalChart } from './interval-chart';
import { INITIAL_RESULTS, resultsStream } from './results.stream';
import { formatCount, formatLoss, formatProbability, formatRate, headlineFor, summariseSampleRatio, toneFor } from './results.view';

@Component({
  selector: 'app-results',
  imports: [FormsModule, RouterLink, Breadcrumbs, ErrorBanner, Loading, IntervalChart],
  templateUrl: './results.page.html',
  styleUrl: './results.page.scss',
})
export class ResultsPage {
  private readonly service = inject(ExperimentsService);
  private readonly router = inject(Router);

  readonly project = input.required<string>();
  readonly environment = input.required<string>();
  readonly key = input.required<string>();

  /** The conversion event being measured. Lives in the URL (`?metric=`) so a link to results is shareable. */
  readonly metric = input<string>();

  protected readonly metricDraft = linkedSignal(() => this.metric() ?? '');
  protected readonly autoRefresh = signal(true);

  private readonly manualRefresh = new Subject<void>();
  private readonly scope = computed(() => ({ project: this.project(), environment: this.environment() }));
  private readonly params = computed(() => ({ key: this.key(), metric: (this.metric() ?? '').trim() }));

  protected readonly state = toSignal(
    resultsStream(
      toObservable(this.params),
      toObservable(this.autoRefresh),
      (key, metric) => this.service.results(this.scope(), key, metric),
      15_000,
      () => new Date(),
      this.manualRefresh,
    ),
    { initialValue: INITIAL_RESULTS },
  );

  protected readonly results = computed(() => this.state().data?.results ?? null);
  protected readonly notice = computed(() => this.state().data?.notice ?? null);
  protected readonly analysis = computed(() => this.results()?.analysis ?? null);
  protected readonly recommendation = computed(() => this.analysis()?.recommendation ?? null);
  protected readonly variations = computed(() => this.analysis()?.variations ?? []);
  protected readonly control = computed(() => this.variations().find((v) => v.isControl) ?? null);
  protected readonly tone = computed(() => {
    const r = this.recommendation();
    return r ? toneFor(r.kind) : 'info';
  });
  protected readonly headline = computed(() => {
    const r = this.recommendation();
    return r ? headlineFor(r.kind, r.variation) : '';
  });
  protected readonly trusted = computed(() => this.recommendation()?.kind !== 'Untrustworthy');
  protected readonly sampleRatio = computed(() => summariseSampleRatio(this.analysis()?.sampleRatio));
  protected readonly metricValid = computed(() => isValidKey(this.metricDraft().trim()));

  protected readonly formatRate = formatRate;
  protected readonly formatProbability = formatProbability;
  protected readonly formatLoss = formatLoss;
  protected readonly formatCount = formatCount;

  protected applyMetric(): void {
    const metric = this.metricDraft().trim();
    void this.router.navigate([], { queryParams: { metric: metric || null }, queryParamsHandling: 'merge' });
  }

  protected refreshNow(): void {
    this.manualRefresh.next();
  }

  protected toggleAuto(on: boolean): void {
    this.autoRefresh.set(on);
  }
}
