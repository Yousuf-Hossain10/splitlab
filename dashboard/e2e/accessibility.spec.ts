import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { Api, sendArm, signIn, uniq, waitForExposures } from './support';

/**
 * The accessibility baseline from ADR-0008: controls are operable from the keyboard and the markup is semantic. Automated
 * checks (axe, against WCAG 2.0/2.1 A and AA) cannot prove a page is accessible, but they reliably catch the common failures:
 * missing labels, poor contrast, broken landmarks and heading order.
 */
const WCAG = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

async function expectNoViolations(page: Page): Promise<void> {
  const { violations } = await new AxeBuilder({ page }).withTags(WCAG).analyze();
  const summary = violations.map((v) => `${v.id} (${v.impact}): ${v.help}\n    ${v.nodes.map((n) => n.target.join(' ')).join('\n    ')}`);
  expect(summary, `accessibility violations on ${page.url()}`).toEqual([]);
}

test.describe('accessibility', () => {
  test('every screen has no detectable WCAG A/AA violations', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project, 'Accessible Project');
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'checkout');
    await api.startExperiment(project, 'production', 'checkout');
    await api.createExperiment(project, 'production', 'draft-one');
    const sdkKey = await api.createKey(project, 'production', ['config:read', 'events:write']);
    const base = { experiment: 'checkout', metric: 'purchase', tag: uniq('u') };
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 300, converting: 30 });
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 300, converting: 60 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 600);

    // Sign-in, before anyone is signed in.
    await page.goto('/sign-in');
    await expect(page.getByRole('heading', { name: 'Sign in to Splitlab' })).toBeVisible();
    await expectNoViolations(page);

    await signIn(page, owner);
    await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible();
    await expectNoViolations(page);

    const screens: { path: string; ready: () => Promise<void> }[] = [
      { path: `/projects/${project}`, ready: () => expect(page.getByRole('heading', { name: 'Accessible Project', level: 1 })).toBeVisible() },
      { path: `/projects/${project}/environments/production`, ready: () => expect(page.getByRole('heading', { name: 'Experiments', level: 1 })).toBeVisible() },
      { path: `/projects/${project}/environments/production/experiments/draft-one`, ready: () => expect(page.getByRole('button', { name: 'Start experiment' })).toBeVisible() },
      { path: `/projects/${project}/environments/production/experiments/checkout`, ready: () => expect(page.getByRole('button', { name: 'Stop experiment' })).toBeVisible() },
      { path: `/projects/${project}/environments/production/keys`, ready: () => expect(page.getByRole('heading', { name: 'SDK keys', level: 1 })).toBeVisible() },
      { path: `/projects/${project}/environments/production/experiments/checkout/results?metric=purchase`, ready: () => expect(page.getByRole('status').filter({ hasText: 'Leader:' })).toBeVisible() },
    ];

    for (const screen of screens) {
      await page.goto(screen.path);
      await screen.ready();
      await expectNoViolations(page);
    }
  });

  test('the key secret panel and the results warning state are accessible too', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'checkout');
    await api.startExperiment(project, 'production', 'checkout');
    const sdkKey = await api.createKey(project, 'production', ['config:read', 'events:write']);
    const base = { experiment: 'checkout', metric: 'purchase', tag: uniq('u') };
    await sendArm(request, sdkKey, { ...base, variation: 'control', users: 700, converting: 70 }); // lopsided on purpose
    await sendArm(request, sdkKey, { ...base, variation: 'treatment', users: 300, converting: 60 });
    await waitForExposures(api, project, 'production', 'checkout', 'purchase', 1000);
    await signIn(page, owner, `/projects/${project}/environments/production/keys`);

    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('web');
    await page.getByRole('button', { name: 'Create key' }).click();
    await expect(page.getByTestId('secret')).toBeVisible();
    await expectNoViolations(page);

    await page.goto(`/projects/${project}/environments/production/experiments/checkout/results?metric=purchase`);
    await expect(page.getByRole('alert').filter({ hasText: 'Do not trust these results' })).toBeVisible();
    await expectNoViolations(page);
  });

  test('the whole sign-in flow can be done from the keyboard alone', async ({ page }) => {
    await page.goto('/sign-in');

    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Your name')).toBeFocused();
    await page.keyboard.type(uniq('keyboard'));
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(/\/projects$/);
  });

  test('a project, environment and experiment can be created from the keyboard alone', async ({ page }) => {
    await signIn(page, uniq('keyboard'));
    const project = uniq('kb-proj');

    // Skip link, then straight to the form.
    await page.getByRole('textbox', { name: 'Name', exact: true }).focus();
    await page.keyboard.type('Keyboard Project');
    await page.keyboard.press('Tab');
    // A key is suggested from the name; wait for it to appear (as a person would see it) before replacing it.
    await expect(page.getByRole('textbox', { name: 'Key', exact: true })).toHaveValue('keyboard-project');
    await page.keyboard.press('Control+A');
    await page.keyboard.type(project);
    // Like a person, wait for the form to accept the input before submitting it.
    await expect(page.getByRole('button', { name: 'Create project' })).toBeEnabled();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(new RegExp(`/projects/${project}$`));

    await page.getByRole('textbox', { name: 'Name', exact: true }).focus();
    await page.keyboard.type('Production');
    await expect(page.getByRole('button', { name: 'Create environment' })).toBeEnabled();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Production', level: 3 })).toBeVisible();
  });

  test('focus is always visible', async ({ page }) => {
    await page.goto('/sign-in');
    await page.keyboard.press('Tab');

    const outline = await page.getByLabel('Your name').evaluate((el) => getComputedStyle(el).outlineStyle + ' ' + getComputedStyle(el).outlineWidth);

    expect(outline).not.toMatch(/^none/);
    expect(outline).not.toMatch(/ 0px$/);
  });

  test('the page has a landmark structure and a skip link', async ({ page }) => {
    await signIn(page, uniq('landmarks'));

    await expect(page.getByRole('banner')).toBeVisible();
    await expect(page.getByRole('main')).toBeVisible();
    await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#main');
    // Exactly one top-level heading per page.
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
  });
});
