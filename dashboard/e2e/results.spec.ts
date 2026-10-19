import { expect, test } from '@playwright/test';
import { Api, sendArm, signIn, uniq, variationRow, waitForExposures } from './support';

/**
 * These tests cover the whole pipeline: an "SDK" posts events with its key; they are queued, written, rolled up and
 * analysed by the API; and the dashboard shows the outcome. Nothing here is mocked.
 */
test.describe('results', () => {
  async function setup(request: Parameters<typeof Api.signedInAs>[0]) {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'checkout');
    await api.startExperiment(project, 'production', 'checkout');
    const sdkKey = await api.createKey(project, 'production', ['config:read', 'events:write']);
    return { owner, project, api, sdkKey, tag: uniq('u') };
  }

  const resultsUrl = (project: string, metric = 'purchase') =>
    `/projects/${project}/environments/production/experiments/checkout/results?metric=${metric}`;

  test('a clear winner is named, with the numbers and sample sizes behind it', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', metric: 'purchase', tag };
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 400, converting: 40 }); // 10%
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 400, converting: 80 }); // 20%
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 800);

    await signIn(page, owner, resultsUrl(project));

    await expect(page.getByRole('status').filter({ hasText: 'Leader: treatment' })).toBeVisible();

    const control = variationRow(page, 'control');
    await expect(control).toContainText('400');
    await expect(control).toContainText('40');
    await expect(control).toContainText('baseline');

    const treatment = variationRow(page, 'treatment');
    await expect(treatment).toContainText('leader');
    await expect(treatment).toContainText('20.15%'); // (1 + 80) / (2 + 400): shrunk slightly toward the prior
    await expect(treatment).toContainText('observed 20.00%');
    await expect(treatment).toContainText('> 99.9%'); // never claims certainty
    await expect(treatment).toContainText('n = 400 vs 400'); // sample sizes beside the probability

    await expect(page.getByText('Traffic split matches the configuration')).toBeVisible();
    await expect(page.getByText(/0 users saw more than one variation/)).toBeVisible();
    await expect(page.getByRole('img', { name: /Conversion rate with 95% credible interval/ })).toBeVisible();
    await expect(page.getByText('Probabilities are conditional on the sample sizes shown.')).toBeVisible();
  });

  test('a lopsided traffic split replaces the winner with a warning', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', metric: 'purchase', tag };
    // Configured 50/50, but 70/30 arrived, and treatment "wins": exactly the case that must not be believed.
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 700, converting: 70 });
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 300, converting: 120 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 1000);

    await signIn(page, owner, resultsUrl(project));

    const warning = page.getByRole('alert').filter({ hasText: 'Do not trust these results' });
    await expect(warning).toBeVisible();
    await expect(warning).toContainText('Sample ratio mismatch');
    await expect(page.getByText('Traffic split does not match the configuration')).toBeVisible();
    await expect(page.getByText('Shown for debugging only')).toBeVisible();
    // No winner anywhere.
    await expect(page.locator('tr.leader')).toHaveCount(0);
    await expect(page.getByText('Leader:')).toHaveCount(0);
  });

  test('too little data says to keep collecting rather than guessing', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', metric: 'purchase', tag };
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 30, converting: 3 });
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 30, converting: 20 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 60);

    await signIn(page, owner, resultsUrl(project));

    await expect(page.getByRole('status').filter({ hasText: 'Keep collecting data' })).toBeVisible();
    await expect(page.getByText(/at least 100 are needed/)).toBeVisible();
    await expect(page.locator('tr.leader')).toHaveCount(0);
  });

  test('users who saw both variations are excluded and reported', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', metric: 'purchase', tag };
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 200, converting: 20 });
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 200, converting: 20 });
    // Five control users are also (wrongly) shown the treatment.
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 5, converting: 0, tag: `${tag}-x` });
    for (let i = 0; i < 5; i++) {
      const response = await request.post('/v1/events', {
        headers: { Authorization: `Bearer ${sdkKey}` },
        data: {
          events: [
            {
              eventId: `both-${tag}-${i}`,
              type: 'exposure',
              experimentKey: 'checkout',
              variationKey: 'treatment',
              userId: `${tag}-control-${i}`,
              timestamp: new Date(Date.now() - 3_000_000).toISOString(),
            },
          ],
        },
      });
      expect(response.status()).toBe(202);
    }
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 200 - 5 + 200 + 5);

    await signIn(page, owner, resultsUrl(project));

    await expect(page.getByText(/5 users saw more than one variation and are excluded/)).toBeVisible();
    await expect(variationRow(page, 'control')).toContainText('195');
  });

  test('the metric is chosen on the page, lands in the URL, and different metrics give different answers', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', tag };
    await sendArm(request, sdkKey, { ...base, metric: 'purchase', variation: 'control', users: 200, converting: 20 });
    await sendArm(request, sdkKey, { ...base, metric: 'purchase', variation: 'treatment', users: 200, converting: 20 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 400);

    // Arrive without a metric: the page asks what to measure and does not guess.
    await signIn(page, owner, `/projects/${project}/environments/production/experiments/checkout/results`);
    await expect(page.getByText('Choose what to measure')).toBeVisible();

    await page.getByLabel('Conversion event to measure').fill('purchase');
    await page.getByRole('button', { name: 'Show results' }).click();
    await expect(page).toHaveURL(/results\?metric=purchase$/);
    await expect(variationRow(page, 'control')).toContainText('20'); // converted

    // Nobody did "signup", so the same users show no conversions.
    await page.getByLabel('Conversion event to measure').fill('signup');
    await page.getByRole('button', { name: 'Show results' }).click();
    await expect(page).toHaveURL(/results\?metric=signup$/);
    await expect(variationRow(page, 'control')).toContainText('0.50%'); // 1 / 202: no conversions, only the prior
  });

  test('refreshing picks up events that arrived after the page was opened', async ({ page, request }) => {
    const { owner, project, api, sdkKey, tag } = await setup(request);
    const base = { experiment: 'checkout', metric: 'purchase' };
    await sendArm(request, sdkKey, { ...base, tag, variation: 'control', users: 200, converting: 20 });
    await sendArm(request, sdkKey, { ...base, tag, variation: 'treatment', users: 200, converting: 20 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 400);
    await signIn(page, owner, resultsUrl(project));
    await expect(variationRow(page, 'treatment')).toContainText('200');

    // 100 more treatment users arrive while the page is open.
    await sendArm(request, sdkKey, { ...base, tag: `${tag}-late`, variation: 'treatment', users: 100, converting: 10 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 500);
    await page.getByRole('button', { name: 'Refresh now' }).click();

    await expect(variationRow(page, 'treatment')).toContainText('300');
  });

  test('a key that can only read configuration cannot send events', async ({ request }) => {
    const { api, project } = await setup(request);
    const readOnly = await api.createKey(project, 'production', ['config:read']);

    const response = await request.post('/v1/events', {
      headers: { Authorization: `Bearer ${readOnly}` },
      data: { events: [] },
    });

    expect(response.status()).toBe(403);
  });
});
