import { BehaviorSubject, Subject } from 'rxjs';
import type { ResultsResponse } from '../../api/models';
import { ApiError } from '../../core/problem';
import { INITIAL_RESULTS, ResultsState, resultsStream } from './results.stream';

function response(metric: string, marker: string): ResultsResponse {
  return {
    notice: marker,
    results: {
      experiment: 'checkout',
      metric,
      status: 'Running',
      excludedUsers: 0,
      unknownVariationUsers: 0,
      generatedAt: '2026-10-20T12:00:00Z',
      analysis: { variations: [], sampleRatio: null, recommendation: { kind: 'Inconclusive', message: '' }, monteCarloSamples: 1 },
    },
  };
}

interface Harness {
  states: ResultsState[];
  load: ReturnType<typeof vi.fn>;
  params$: BehaviorSubject<{ key: string; metric: string }>;
  auto$: BehaviorSubject<boolean>;
  manual$: Subject<void>;
  last: () => ResultsState;
  stop: () => void;
}

function start(load: (key: string, metric: string) => Promise<ResultsResponse>, metric = 'purchase', auto = true): Harness {
  const fn = vi.fn(load);
  const params$ = new BehaviorSubject({ key: 'checkout', metric });
  const auto$ = new BehaviorSubject(auto);
  const manual$ = new Subject<void>();
  const states: ResultsState[] = [];
  const subscription = resultsStream(params$, auto$, fn, 1_000, () => new Date('2026-10-20T12:00:00Z'), manual$).subscribe((s) =>
    states.push(s),
  );
  return { states, load: fn, params$, auto$, manual$, last: () => states[states.length - 1], stop: () => subscription.unsubscribe() };
}

describe('resultsStream', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('starts empty and loads straight away', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'first')));

    expect(h.states[0]).toEqual(INITIAL_RESULTS);
    await vi.advanceTimersByTimeAsync(0);

    expect(h.load).toHaveBeenCalledWith('checkout', 'purchase');
    expect(h.last().data?.notice).toBe('first');
    expect(h.last().loading).toBe(false);
    expect(h.last().updatedAt).toEqual(new Date('2026-10-20T12:00:00Z'));
    h.stop();
  });

  it('shows a loading state while a request is in flight', async () => {
    const h = start(() => new Promise(() => undefined)); // never resolves

    await vi.advanceTimersByTimeAsync(0);

    expect(h.last().loading).toBe(true);
    expect(h.last().data).toBeNull();
    h.stop();
  });

  it('refreshes on the interval while auto-refresh is on', async () => {
    let n = 0;
    const h = start(() => Promise.resolve(response('purchase', `v${++n}`)));

    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1_000);
    await vi.advanceTimersByTimeAsync(1_000);

    expect(h.load).toHaveBeenCalledTimes(3);
    expect(h.last().data?.notice).toBe('v3');
    h.stop();
  });

  it('loads once and then waits when auto-refresh is off', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'once')), 'purchase', false);

    await vi.advanceTimersByTimeAsync(10_000);

    expect(h.load).toHaveBeenCalledTimes(1);
    h.stop();
  });

  it('stops polling when auto-refresh is switched off, and resumes when it is switched on', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'x')));
    await vi.advanceTimersByTimeAsync(0);

    h.auto$.next(false);
    await vi.advanceTimersByTimeAsync(5_000);
    const whileOff = h.load.mock.calls.length;
    h.auto$.next(true);
    await vi.advanceTimersByTimeAsync(0);

    expect(whileOff).toBe(2); // the first load, plus one when the timer was restarted by the toggle
    expect(h.load.mock.calls.length).toBe(3);
    h.stop();
  });

  it('keeps showing the numbers while toggling auto-refresh', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'kept')));
    await vi.advanceTimersByTimeAsync(0);

    h.auto$.next(false);

    expect(h.last().data?.notice).toBe('kept');
    h.stop();
  });

  it('refreshes on demand', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'x')), 'purchase', false);
    await vi.advanceTimersByTimeAsync(0);

    h.manual$.next();
    await vi.advanceTimersByTimeAsync(0);

    expect(h.load).toHaveBeenCalledTimes(2);
    h.stop();
  });

  it('forgets the old numbers when the metric changes, and loads the new one', async () => {
    const h = start((_key, metric) => Promise.resolve(response(metric, `for-${metric}`)));
    await vi.advanceTimersByTimeAsync(0);
    expect(h.last().data?.notice).toBe('for-purchase');

    h.params$.next({ key: 'checkout', metric: 'signup' });

    expect(h.last().data).toBeNull(); // not the old metric's numbers under the new heading
    await vi.advanceTimersByTimeAsync(0);
    expect(h.last().data?.notice).toBe('for-signup');
    h.stop();
  });

  it('discards a slow answer for a metric that is no longer selected', async () => {
    const pending = new Map<string, (r: ResultsResponse) => void>();
    const h = start(
      (_key, metric) =>
        new Promise((resolve) => {
          pending.set(metric, resolve);
        }),
    );
    await vi.advanceTimersByTimeAsync(0);

    h.params$.next({ key: 'checkout', metric: 'signup' });
    await vi.advanceTimersByTimeAsync(0);
    pending.get('signup')?.(response('signup', 'new'));
    await vi.advanceTimersByTimeAsync(0);
    pending.get('purchase')?.(response('purchase', 'stale')); // arrives late
    await vi.advanceTimersByTimeAsync(0);

    expect(h.last().data?.notice).toBe('new');
    h.stop();
  });

  it('does nothing without a metric, and clears what was showing', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'x')));
    await vi.advanceTimersByTimeAsync(0);

    h.params$.next({ key: 'checkout', metric: '' });
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.last()).toEqual(INITIAL_RESULTS);
    expect(h.load).toHaveBeenCalledTimes(1);
    h.stop();
  });

  it('does not reload when the parameters are unchanged', async () => {
    const h = start(() => Promise.resolve(response('purchase', 'x')), 'purchase', false);
    await vi.advanceTimersByTimeAsync(0);

    h.params$.next({ key: 'checkout', metric: 'purchase' });
    await vi.advanceTimersByTimeAsync(0);

    expect(h.load).toHaveBeenCalledTimes(1);
    h.stop();
  });

  it('keeps the last good numbers when a refresh fails, and says why', async () => {
    let call = 0;
    const h = start(() => {
      call++;
      return call === 1
        ? Promise.resolve(response('purchase', 'good'))
        : Promise.reject(new ApiError(500, 'Server error', 'The server had a problem.'));
    });

    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1_000);

    expect(h.last().data?.notice).toBe('good');
    expect(h.last().error).toBe('The server had a problem.');
    expect(h.last().loading).toBe(false);
    h.stop();
  });

  it('clears the error once a later refresh succeeds', async () => {
    let call = 0;
    const h = start(() => {
      call++;
      return call === 2 ? Promise.reject(new Error('blip')) : Promise.resolve(response('purchase', `ok${call}`));
    });

    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(h.last().error).toBe('blip');
    await vi.advanceTimersByTimeAsync(1_000);

    expect(h.last().error).toBeNull();
    expect(h.last().data?.notice).toBe('ok3');
    h.stop();
  });
});
