import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { ExperimentAnalysis, RecommendationKind, ResultsResponse, VariationResult } from '../../api/models';
import { ApiError } from '../../core/problem';
import { buttonByText, el, settle, text, type } from '../../testing';
import { ExperimentsService } from '../experiments/experiments.service';
import { ResultsPage } from './results.page';

function variation(overrides: Partial<VariationResult> & Pick<VariationResult, 'key' | 'isControl'>): VariationResult {
  return {
    exposed: 2000,
    converted: 200,
    observedRate: 0.1,
    posteriorMean: 0.1,
    credibleLow: 0.087,
    credibleHigh: 0.114,
    probabilityToBeatControl: null,
    expectedLoss: 0.0123,
    probabilityBest: 0.5,
    ...overrides,
  };
}

function results(kind: RecommendationKind, leader: string | null = null, mismatch = false): ResultsResponse {
  const analysis: ExperimentAnalysis = {
    variations: [
      variation({ key: 'control', isControl: true }),
      variation({
        key: 'treatment',
        isControl: false,
        converted: 300,
        observedRate: 0.15,
        posteriorMean: 0.15,
        credibleLow: 0.135,
        credibleHigh: 0.166,
        probabilityToBeatControl: 0.99999,
        expectedLoss: 0.00001,
      }),
    ],
    sampleRatio: { chiSquare: mismatch ? 16 : 0.2, degreesOfFreedom: 1, pValue: mismatch ? 0.0000633 : 0.65, mismatch },
    recommendation: {
      kind,
      variation: leader,
      message: kind === 'Untrustworthy' ? 'Sample ratio mismatch: traffic is not split as configured.' : "'treatment' is the leader.",
    },
    monteCarloSamples: 100000,
  };
  return {
    notice: 'Probabilities are conditional on the sample sizes shown.',
    results: { experiment: 'checkout', metric: 'purchase', status: 'Running', analysis, excludedUsers: 3, unknownVariationUsers: 0, generatedAt: '2026-10-20T12:00:00Z' },
  };
}

describe('ResultsPage', () => {
  let load: ReturnType<typeof vi.fn>;

  function create(metric: string | undefined) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ExperimentsService, useValue: { results: load } }],
    });
    const fixture = TestBed.createComponent(ResultsPage);
    fixture.componentRef.setInput('project', 'acme');
    fixture.componentRef.setInput('environment', 'production');
    fixture.componentRef.setInput('key', 'checkout');
    fixture.componentRef.setInput('metric', metric);
    return fixture;
  }

  beforeEach(() => {
    load = vi.fn();
  });

  it('asks what to measure until a metric is chosen, and does not call the API', async () => {
    const fixture = create(undefined);

    await settle(fixture);

    expect(text(fixture)).toContain('Choose what to measure');
    expect(load).not.toHaveBeenCalled();
    expect(el(fixture).querySelector('table')).toBeNull();
    fixture.destroy();
  });

  it('loads the results for the chosen metric and shows every variation', async () => {
    load.mockResolvedValue(results('Leader', 'treatment'));
    const fixture = create('purchase');

    await settle(fixture);

    expect(load).toHaveBeenCalledWith({ project: 'acme', environment: 'production' }, 'checkout', 'purchase');
    const rows = Array.from(el(fixture).querySelectorAll('tbody tr')).map((r) => (r.textContent ?? '').replace(/\s+/g, ' ').trim());
    expect(rows).toHaveLength(2);
    expect(rows[0]).toContain('control');
    expect(rows[0]).toContain('baseline');
    expect(rows[1]).toContain('treatment');
    expect(rows[1]).toContain('15.00%');
    fixture.destroy();
  });

  it('names the leader and flags the winning row', async () => {
    load.mockResolvedValue(results('Leader', 'treatment'));
    const fixture = create('purchase');

    await settle(fixture);

    expect(text(fixture)).toContain('Leader: treatment');
    expect(el(fixture).querySelector('tr.leader')?.textContent).toContain('treatment');
    expect(el(fixture).querySelector('.verdict')?.getAttribute('role')).toBe('status');
    fixture.destroy();
  });

  it('shows the sample size next to every probability, and never claims certainty', async () => {
    load.mockResolvedValue(results('Leader', 'treatment'));
    const fixture = create('purchase');

    await settle(fixture);

    const treatmentRow = el(fixture).querySelectorAll('tbody tr')[1].textContent ?? '';
    expect(treatmentRow).toContain('> 99.9%'); // 0.99999 is shown as a bound, not as 100%
    expect(treatmentRow).toContain('n = 2,000 vs 2,000');
    expect(text(fixture)).toContain('Probabilities are conditional on the sample sizes shown.');
    fixture.destroy();
  });

  it('draws the interval chart with a text description for screen readers', async () => {
    load.mockResolvedValue(results('Leader', 'treatment'));
    const fixture = create('purchase');

    await settle(fixture);

    const svg = el(fixture).querySelector('svg[role="img"]');
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute('aria-label')).toContain('treatment: 15.00% (95% interval 13.50% to 16.60%)');
    expect(svg?.querySelectorAll('circle.mean')).toHaveLength(2);
    fixture.destroy();
  });

  it('reports the data quality checks', async () => {
    load.mockResolvedValue(results('Leader', 'treatment'));
    const fixture = create('purchase');

    await settle(fixture);

    expect(text(fixture)).toContain('Traffic split matches the configuration');
    expect(text(fixture)).toContain('3 users saw more than one variation and are excluded');
    fixture.destroy();
  });

  it('puts a prominent warning in place of a winner when the data cannot be trusted', async () => {
    load.mockResolvedValue(results('Untrustworthy', null, true));
    const fixture = create('purchase');

    await settle(fixture);

    const verdict = el(fixture).querySelector('.verdict');
    expect(verdict?.getAttribute('role')).toBe('alert');
    expect(verdict?.textContent).toContain('Do not trust these results');
    expect(verdict?.classList.contains('error')).toBe(true);
    expect(text(fixture)).toContain('Traffic split does not match the configuration');
    expect(text(fixture)).toContain('Shown for debugging only');
    expect(el(fixture).querySelector('tr.leader')).toBeNull();
    expect(text(fixture)).not.toContain('leader');
    fixture.destroy();
  });

  it('never marks a winner on untrustworthy data, even if the server sent a variation along with the warning', async () => {
    // Defence in depth: the page itself refuses to crown anyone when the data failed its quality check.
    load.mockResolvedValue(results('Untrustworthy', 'treatment', true));
    const fixture = create('purchase');

    await settle(fixture);

    expect(el(fixture).querySelector('tr.leader')).toBeNull();
    expect(Array.from(el(fixture).querySelectorAll('.chip')).some((c) => c.textContent?.trim() === 'leader')).toBe(false);
    fixture.destroy();
  });

  it('does not crown anyone when there is no clear winner', async () => {
    load.mockResolvedValue(results('Inconclusive'));
    const fixture = create('purchase');

    await settle(fixture);

    expect(text(fixture)).toContain('No clear winner yet');
    expect(el(fixture).querySelector('tr.leader')).toBeNull();
    fixture.destroy();
  });

  it('shows the reason when loading fails', async () => {
    load.mockRejectedValue(new ApiError(404, 'Not found', "Experiment 'checkout' was not found."));
    const fixture = create('purchase');

    await settle(fixture);

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain("Experiment 'checkout' was not found.");
    expect(el(fixture).querySelector('table')).toBeNull();
    fixture.destroy();
  });

  it('puts the chosen metric in the URL so the link can be shared', async () => {
    const fixture = create(undefined);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await settle(fixture);

    type(fixture, '#metric', 'signup');
    await settle(fixture);
    buttonByText(fixture, 'Show results')?.click();

    expect(navigate).toHaveBeenCalledWith([], { queryParams: { metric: 'signup' }, queryParamsHandling: 'merge' });
    fixture.destroy();
  });

  it('will not accept an invalid metric name', async () => {
    const fixture = create(undefined);
    await settle(fixture);

    type(fixture, '#metric', 'Not A Valid Name');
    await settle(fixture);

    expect(buttonByText(fixture, 'Show results')?.disabled).toBe(true);
    fixture.destroy();
  });
});
