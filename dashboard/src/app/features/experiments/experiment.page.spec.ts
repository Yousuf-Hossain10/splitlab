import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { Experiment } from '../../api/models';
import { ApiError } from '../../core/problem';
import { buttonByText, el, settle, text } from '../../testing';
import { ExperimentPage } from './experiment.page';
import { ExperimentsService } from './experiments.service';

const scope = { project: 'acme', environment: 'production', key: 'checkout' };

function experiment(overrides: Partial<Experiment> = {}): Experiment {
  return {
    key: 'checkout',
    name: 'Checkout flow',
    status: 'Draft',
    version: 3,
    variations: [
      { key: 'control', trafficBasisPoints: 5000 },
      { key: 'treatment', trafficBasisPoints: 5000 },
    ],
    ...overrides,
  };
}

describe('ExperimentPage', () => {
  let service: Record<'get' | 'start' | 'stop' | 'replaceVariations', ReturnType<typeof vi.fn>>;

  function create() {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: ExperimentsService, useValue: service }] });
    const fixture = TestBed.createComponent(ExperimentPage);
    fixture.componentRef.setInput('project', 'acme');
    fixture.componentRef.setInput('environment', 'production');
    fixture.componentRef.setInput('key', 'checkout');
    return fixture;
  }

  beforeEach(() => {
    service = {
      get: vi.fn().mockResolvedValue(experiment()),
      start: vi.fn().mockResolvedValue(experiment({ status: 'Running', version: 4 })),
      stop: vi.fn().mockResolvedValue(experiment({ status: 'Stopped', version: 5 })),
      replaceVariations: vi.fn().mockResolvedValue(experiment({ version: 4 })),
    };
  });

  it('shows the experiment, its status and version', async () => {
    const fixture = create();

    await settle(fixture);

    expect(text(fixture)).toContain('Checkout flow');
    expect(text(fixture)).toContain('Draft');
    expect(text(fixture)).toContain('version 3');
    expect(service.get).toHaveBeenCalledWith(expect.objectContaining({ project: 'acme', environment: 'production' }), 'checkout');
  });

  it('explains that starting publishes the experiment and locks its variations', async () => {
    const fixture = create();

    await settle(fixture);

    expect(text(fixture)).toContain('SDKs cannot see it yet');
    expect(text(fixture)).toContain('can no longer be changed');
  });

  it('asks for confirmation before starting, and sends the version it saw', async () => {
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Start experiment')?.click();
    await settle(fixture);
    expect(service.start).not.toHaveBeenCalled();
    expect(text(fixture)).toContain('Publish this experiment to your SDKs?');

    buttonByText(fixture, 'Yes, start')?.click();
    await settle(fixture);

    expect(service.start).toHaveBeenCalledWith(expect.objectContaining(scope), 'checkout', 3);
  });

  it('backs out of a start when the person cancels', async () => {
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Start experiment')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Cancel')?.click();
    await settle(fixture);

    expect(service.start).not.toHaveBeenCalled();
    expect(buttonByText(fixture, 'Start experiment')).toBeDefined();
  });

  it('reloads after a change so the new status and version show', async () => {
    const fixture = create();
    await settle(fixture);
    service.get.mockResolvedValue(experiment({ status: 'Running', version: 4 }));

    buttonByText(fixture, 'Start experiment')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Yes, start')?.click();
    await settle(fixture);

    expect(text(fixture)).toContain('Running');
    expect(text(fixture)).toContain('version 4');
    expect(buttonByText(fixture, 'Stop experiment')).toBeDefined();
  });

  it('cannot start an experiment with fewer than two variations, and says why', async () => {
    service.get.mockResolvedValue(experiment({ variations: [{ key: 'only', trafficBasisPoints: 10000 }] }));
    const fixture = create();

    await settle(fixture);

    expect(buttonByText(fixture, 'Start experiment')?.disabled).toBe(true);
    expect(text(fixture)).toContain('at least 2 variations');
  });

  it('tells the person when someone else changed the experiment first, and offers to reload', async () => {
    service.start.mockRejectedValue(new ApiError(409, 'Conflict', 'Experiment is at version 4, not 3. Reload and retry.'));
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Start experiment')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Yes, start')?.click();
    await settle(fixture);

    expect(text(fixture)).toContain('This experiment changed since you opened it');
    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('Reload and retry');
    expect(buttonByText(fixture, 'Reload the latest version')).toBeDefined();
  });

  it('does not offer the stale-version reload for other kinds of failure', async () => {
    service.start.mockRejectedValue(new ApiError(400, 'Invalid request', 'An experiment needs at least two variations to start.'));
    const fixture = create();
    await settle(fixture);

    buttonByText(fixture, 'Start experiment')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Yes, start')?.click();
    await settle(fixture);

    expect(text(fixture)).toContain('needs at least two variations');
    expect(text(fixture)).not.toContain('changed since you opened it');
  });

  it('lets a running experiment be stopped, after confirming', async () => {
    service.get.mockResolvedValue(experiment({ status: 'Running', version: 4 }));
    const fixture = create();
    await settle(fixture);

    expect(text(fixture)).toContain('included in the datafile');
    buttonByText(fixture, 'Stop experiment')?.click();
    await settle(fixture);
    buttonByText(fixture, 'Yes, stop')?.click();
    await settle(fixture);

    expect(service.stop).toHaveBeenCalledWith(expect.objectContaining(scope), 'checkout', 4);
  });

  it('shows a running experiment’s variations read-only', async () => {
    service.get.mockResolvedValue(experiment({ status: 'Running', version: 4 }));
    const fixture = create();

    await settle(fixture);

    expect(el(fixture).querySelector('app-variations-editor')).toBeNull();
    expect(buttonByText(fixture, 'Save variations')).toBeUndefined();
    expect(text(fixture)).toContain('50%');
  });

  it('saves edited variations of a draft with the version it saw, and only once something changed', async () => {
    const fixture = create();
    await settle(fixture);
    expect(buttonByText(fixture, 'Save variations')?.disabled).toBe(true); // nothing edited yet

    const percent = el(fixture).querySelector<HTMLInputElement>('#vpct-0')!;
    percent.value = '70';
    percent.dispatchEvent(new Event('input', { bubbles: true }));
    const other = el(fixture).querySelector<HTMLInputElement>('#vpct-1')!;
    other.value = '30';
    other.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(fixture);
    expect(buttonByText(fixture, 'Save variations')?.disabled).toBe(false);

    buttonByText(fixture, 'Save variations')?.click();
    await settle(fixture);

    expect(service.replaceVariations).toHaveBeenCalledWith(expect.objectContaining(scope), 'checkout', 3, [
      { key: 'control', trafficBasisPoints: 7000 },
      { key: 'treatment', trafficBasisPoints: 3000 },
    ]);
  });

  it('will not save variations that add up to more than 100%', async () => {
    const fixture = create();
    await settle(fixture);

    const percent = el(fixture).querySelector<HTMLInputElement>('#vpct-0')!;
    percent.value = '80';
    percent.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(fixture);

    expect(buttonByText(fixture, 'Save variations')?.disabled).toBe(true);
    expect(text(fixture)).toContain('more than 100%');
  });

  it('shows why an experiment could not be loaded', async () => {
    service.get.mockRejectedValue(new ApiError(404, 'Not found', "Experiment 'checkout' was not found."));
    const fixture = create();

    await settle(fixture);

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain('was not found');
  });
});
