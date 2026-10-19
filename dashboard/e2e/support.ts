import { APIRequestContext, Page, expect } from '@playwright/test';

/** A short unique suffix, so every test creates its own data and tests never depend on each other. */
export function uniq(prefix: string): string {
  return `${prefix}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * Signs in through the real sign-in page, the way a person would, and returns once the app has moved on from it.
 * Waiting for the redirect matters: until then the sign-in form is still on screen, and a test that looked for a field
 * by a loose name could type into it instead of into the page it meant.
 */
export async function signIn(page: Page, name: string, returnUrl?: string): Promise<void> {
  await page.goto(returnUrl ? `/sign-in?returnUrl=${encodeURIComponent(returnUrl)}` : '/sign-in');
  await page.getByLabel('Your name').fill(name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/sign-in'));
}

/**
 * The results-table row for one variation, found by the key in its row header. (Matching on the row's text would be
 * fragile: cells are concatenated without separators, and the header "Chance to beat control" contains "control".)
 */
export function variationRow(page: Page, key: string) {
  return page.locator('tbody tr').filter({ has: page.locator('th .mono').filter({ hasText: new RegExp(`^${key}$`) }) });
}

export interface VariationSpec {
  key: string;
  trafficBasisPoints: number;
}

/**
 * Talks to the API directly (through the dashboard's dev server, as the browser does) to set up state quickly. Tests use
 * this for everything that is not the journey under test, and the UI for everything that is.
 */
export class Api {
  private constructor(
    private readonly request: APIRequestContext,
    readonly token: string,
    readonly subject: string,
  ) {}

  static async signedInAs(request: APIRequestContext, subject: string): Promise<Api> {
    const response = await request.post('/dev/token', { data: { subject } });
    expect(response.ok()).toBe(true);
    const { token } = (await response.json()) as { token: string };
    return new Api(request, token, subject);
  }

  private headers(token = this.token): Record<string, string> {
    return { Authorization: `Bearer ${token}` };
  }

  async createProject(key: string, name = key): Promise<void> {
    const response = await this.request.post('/v1/projects', { headers: this.headers(), data: { key, name } });
    expect(response.status()).toBe(201);
  }

  async createEnvironment(project: string, key: string, name = key): Promise<void> {
    const response = await this.request.post(`/v1/projects/${project}/environments`, { headers: this.headers(), data: { key, name } });
    expect(response.status()).toBe(201);
  }

  async createExperiment(project: string, environment: string, key: string, variations?: VariationSpec[]): Promise<void> {
    const response = await this.request.post(`/v1/projects/${project}/environments/${environment}/experiments`, {
      headers: this.headers(),
      data: {
        key,
        name: key,
        variations: variations ?? [
          { key: 'control', trafficBasisPoints: 5000 },
          { key: 'treatment', trafficBasisPoints: 5000 },
        ],
      },
    });
    expect(response.status()).toBe(201);
  }

  async startExperiment(project: string, environment: string, key: string): Promise<void> {
    const base = `/v1/projects/${project}/environments/${environment}/experiments/${key}`;
    const current = await this.request.get(base, { headers: this.headers() });
    const { version } = (await current.json()) as { version: number };
    const response = await this.request.post(`${base}/start`, { headers: this.headers(), data: { version } });
    expect(response.ok()).toBe(true);
  }

  /** Creates an SDK key and returns its secret, which the server shows only once. */
  async createKey(project: string, environment: string, permissions: string[]): Promise<string> {
    const response = await this.request.post(`/v1/projects/${project}/environments/${environment}/api-keys`, {
      headers: this.headers(),
      data: { name: 'e2e', permissions },
    });
    expect(response.status()).toBe(201);
    return ((await response.json()) as { secret: string }).secret;
  }

  async addMember(project: string, subject: string, role: string): Promise<void> {
    const response = await this.request.put(`/v1/projects/${project}/members/${subject}`, { headers: this.headers(), data: { role } });
    expect(response.ok()).toBe(true);
  }

  async results(project: string, environment: string, experiment: string, metric: string) {
    const response = await this.request.get(
      `/v1/projects/${project}/environments/${environment}/experiments/${experiment}/results?metric=${metric}`,
      { headers: this.headers() },
    );
    expect(response.ok()).toBe(true);
    return (await response.json()) as {
      results: { analysis: { variations: { key: string; exposed: number; converted: number }[] } };
    };
  }
}

interface SdkEvent {
  eventId: string;
  type: 'exposure' | 'conversion';
  experimentKey?: string;
  variationKey?: string;
  name?: string;
  userId: string;
  timestamp: string;
}

/**
 * Plays the part of an application's SDK: reports who saw which variation and who converted, with an environment's
 * SDK key. `users` people see `variation`; the first `converting` of them then complete `metric`.
 */
export async function sendArm(
  request: APIRequestContext,
  sdkKey: string,
  options: { experiment: string; variation: string; users: number; converting: number; metric: string; tag: string },
): Promise<void> {
  const now = Date.now();
  const events: SdkEvent[] = [];
  for (let i = 0; i < options.users; i++) {
    const userId = `${options.tag}-${options.variation}-${i}`;
    events.push({
      eventId: `exposure-${userId}`,
      type: 'exposure',
      experimentKey: options.experiment,
      variationKey: options.variation,
      userId,
      timestamp: new Date(now - 2 * 3_600_000).toISOString(),
    });
    if (i < options.converting) {
      events.push({
        eventId: `conversion-${userId}`,
        type: 'conversion',
        name: options.metric,
        userId,
        timestamp: new Date(now - 3_600_000).toISOString(),
      });
    }
  }

  // The API accepts at most 500 events per request.
  for (let start = 0; start < events.length; start += 500) {
    const response = await request.post('/v1/events', {
      headers: { Authorization: `Bearer ${sdkKey}` },
      data: { events: events.slice(start, start + 500) },
    });
    expect(response.status()).toBe(202);
    expect(((await response.json()) as { rejected: unknown[] }).rejected).toHaveLength(0);
  }
}

/** Waits until the events have been written and rolled up, so the page under test sees them. */
export async function waitForExposures(api: Api, project: string, environment: string, experiment: string, metric: string, expected: number): Promise<void> {
  await expect
    .poll(
      async () => {
        const { results } = await api.results(project, environment, experiment, metric);
        return results.analysis.variations.reduce((sum, v) => sum + v.exposed, 0);
      },
      { message: 'events should be ingested and rolled up', timeout: 20_000 },
    )
    .toBe(expected);
}
