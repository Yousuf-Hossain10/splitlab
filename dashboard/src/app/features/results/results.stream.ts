import { EMPTY, Observable, catchError, concat, distinctUntilChanged, from, map, merge, of, scan, startWith, switchMap, timer } from 'rxjs';
import type { ResultsResponse } from '../../api/models';
import { describeError } from '../../core/problem';

export interface ResultsState {
  /** The latest successful response. Kept while a refresh is in flight or fails, so numbers do not vanish. */
  data: ResultsResponse | null;
  error: string | null;
  loading: boolean;
  updatedAt: Date | null;
}

export const INITIAL_RESULTS: ResultsState = { data: null, error: null, loading: false, updatedAt: null };

type Change =
  | { type: 'reset' }
  | { type: 'loading' }
  | { type: 'data'; data: ResultsResponse; at: Date }
  | { type: 'error'; message: string };

function reduce(state: ResultsState, change: Change): ResultsState {
  switch (change.type) {
    case 'reset':
      return INITIAL_RESULTS;
    case 'loading':
      return { ...state, loading: true };
    case 'data':
      return { data: change.data, error: null, loading: false, updatedAt: change.at };
    case 'error':
      return { ...state, error: change.message, loading: false };
  }
}

/**
 * Results over time: loads when the experiment or metric changes, and again every {@link intervalMs} while
 * auto-refresh is on. Changing either input discards a request still in flight, so a slow answer for the old
 * metric can never overwrite the new one. This is the one place RxJS is the natural fit (ADR-0008).
 */
export function resultsStream(
  params$: Observable<{ key: string; metric: string }>,
  autoRefresh$: Observable<boolean>,
  load: (key: string, metric: string) => Promise<ResultsResponse>,
  intervalMs = 15_000,
  now: () => Date = () => new Date(),
  manualRefresh$: Observable<unknown> = EMPTY,
): Observable<ResultsState> {
  const fetchOnce = (key: string, metric: string): Observable<Change> =>
    concat(
      of<Change>({ type: 'loading' }),
      from(load(key, metric)).pipe(
        map((data): Change => ({ type: 'data', data, at: now() })),
        catchError((e: unknown) => of<Change>({ type: 'error', message: describeError(e) })),
      ),
    );

  return params$.pipe(
    distinctUntilChanged((a, b) => a.key === b.key && a.metric === b.metric),
    switchMap(({ key, metric }) => {
      if (!metric) {
        return of<Change>({ type: 'reset' });
      }
      // New experiment or metric: forget the old numbers first. Toggling auto-refresh only restarts the timer.
      return concat(
        of<Change>({ type: 'reset' }),
        autoRefresh$.pipe(
          distinctUntilChanged(),
          switchMap((auto) => merge(auto ? timer(0, intervalMs) : timer(0), manualRefresh$)),
          switchMap(() => fetchOnce(key, metric)),
        ),
      );
    }),
    scan(reduce, INITIAL_RESULTS),
    startWith(INITIAL_RESULTS),
  );
}
