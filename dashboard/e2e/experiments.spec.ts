import { expect, test } from '@playwright/test';
import { Api, signIn, uniq } from './support';

interface Datafile {
  schemaVersion: number;
  experiments: { key: string; variations: { key: string; trafficBasisPoints: number }[] }[];
}

test.describe('experiment lifecycle', () => {
  test('a person builds an experiment from nothing and publishes it to SDKs, then stops it', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    await signIn(page, owner);

    // Create a project.
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Acme Store');
    await page.getByRole('textbox', { name: 'Key', exact: true }).fill(project);
    await page.getByRole('button', { name: 'Create project' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${project}$`));
    await expect(page.getByRole('heading', { name: 'Acme Store', level: 1 })).toBeVisible();

    // Create an environment.
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Production');
    await page.getByRole('textbox', { name: 'Key', exact: true }).fill('production');
    await page.getByRole('button', { name: 'Create environment' }).click();
    await expect(page.getByRole('heading', { name: 'Production', level: 3 })).toBeVisible();

    // An SDK key, so we can see what an application would be given. (Created by API: the key screens have their own tests.)
    const api = await Api.signedInAs(request, owner);
    const sdkKey = await api.createKey(project, 'production', ['config:read']);
    const datafile = async () => {
      const response = await request.get('/v1/config/production', { headers: { Authorization: `Bearer ${sdkKey}` } });
      expect(response.status()).toBe(200);
      return (await response.json()) as Datafile;
    };

    // Create an experiment.
    await page.getByRole('link', { name: 'Experiments' }).click();
    // Wait for the new page: the outgoing one has a "Name" field too, and typing into it would be lost.
    await expect(page.getByRole('heading', { name: 'Experiments', level: 1 })).toBeVisible();
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Checkout flow');
    await expect(page.getByRole('textbox', { name: 'Key', exact: true })).toHaveValue('checkout-flow'); // suggested from the name
    await page.getByRole('textbox', { name: 'Key', exact: true }).fill('checkout');
    await page.getByRole('button', { name: 'Create experiment' }).click();

    await expect(page).toHaveURL(/\/experiments\/checkout$/);
    await expect(page.getByRole('heading', { name: 'Checkout flow', level: 1 })).toBeVisible();
    await expect(page.getByText('Draft', { exact: true })).toBeVisible();

    // A draft is invisible to SDKs.
    expect((await datafile()).experiments).toEqual([]);

    // Starting asks for confirmation first, and changes nothing until confirmed.
    await page.getByRole('button', { name: 'Start experiment' }).click();
    await expect(page.getByText('Publish this experiment to your SDKs?')).toBeVisible();
    expect((await datafile()).experiments).toEqual([]);
    await page.getByRole('button', { name: 'Yes, start' }).click();

    await expect(page.getByText('Running', { exact: true })).toBeVisible();
    await expect(page.getByText('version 2')).toBeVisible();
    // Once running, variations are read-only.
    await expect(page.getByRole('button', { name: 'Save variations' })).toHaveCount(0);

    // Now the SDK sees it, with the variations that were configured.
    const running = await datafile();
    expect(running.experiments).toHaveLength(1);
    expect(running.experiments[0]).toEqual({
      key: 'checkout',
      variations: [
        { key: 'control', trafficBasisPoints: 5000 },
        { key: 'treatment', trafficBasisPoints: 5000 },
      ],
    });

    // Stop it: the SDK stops seeing it.
    await page.getByRole('button', { name: 'Stop experiment' }).click();
    await page.getByRole('button', { name: 'Yes, stop' }).click();
    await expect(page.getByText('Stopped', { exact: true })).toBeVisible();
    expect((await datafile()).experiments).toEqual([]);
  });

  test('a draft’s variations can be edited and saved, and over-allocating is refused', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'pricing');
    await signIn(page, owner, `/projects/${project}/environments/production/experiments/pricing`);

    const traffic = page.getByLabel('Variation 1 traffic percent');
    await expect(traffic).toHaveValue('50');
    await expect(page.getByRole('button', { name: 'Save variations' })).toBeDisabled(); // nothing changed yet

    // 80% + 50% is more than everything.
    await traffic.fill('80');
    await expect(page.getByText('more than 100%')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save variations' })).toBeDisabled();

    // 70% + 30% is fine.
    await traffic.fill('70');
    await page.getByLabel('Variation 2 traffic percent').fill('30');
    await page.getByRole('button', { name: 'Save variations' }).click();

    await expect(page.getByText('version 2')).toBeVisible();
    await page.reload();
    await expect(page.getByLabel('Variation 1 traffic percent')).toHaveValue('70');
    await expect(page.getByLabel('Variation 2 traffic percent')).toHaveValue('30');
  });

  test('an experiment with only one variation cannot be started', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'solo', [{ key: 'only', trafficBasisPoints: 10000 }]);

    await signIn(page, owner, `/projects/${project}/environments/production/experiments/solo`);

    await expect(page.getByRole('button', { name: 'Start experiment' })).toBeDisabled();
    await expect(page.getByText('at least 2 variations')).toBeVisible();
  });

  test('if someone else changes the experiment first, the person is told and can reload', async ({ page, request }) => {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'race');
    await signIn(page, owner, `/projects/${project}/environments/production/experiments/race`);
    await expect(page.getByText('version 1')).toBeVisible();

    // A colleague starts it while this page is still open at version 1.
    await api.startExperiment(project, 'production', 'race');

    await page.getByRole('button', { name: 'Start experiment' }).click();
    await page.getByRole('button', { name: 'Yes, start' }).click();

    await expect(page.getByText('This experiment changed since you opened it')).toBeVisible();
    await page.getByRole('button', { name: 'Reload the latest version' }).click();
    await expect(page.getByText('Running', { exact: true })).toBeVisible();
    await expect(page.getByText('version 2')).toBeVisible();
  });
});
