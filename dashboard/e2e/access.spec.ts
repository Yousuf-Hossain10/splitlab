import { expect, test } from '@playwright/test';
import { Api, signIn, uniq } from './support';

const NO_ACCESS = 'You do not have access to this, or it does not exist.';

test.describe('who can see and do what', () => {
  async function ownerWithProject(request: Parameters<typeof Api.signedInAs>[0]) {
    const owner = uniq('owner');
    const project = uniq('secret-proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project, 'Confidential Project');
    await api.createEnvironment(project, 'production');
    await api.createExperiment(project, 'production', 'checkout');
    return { owner, project, api };
  }

  test('someone outside a project cannot see it listed or open it', async ({ page, request }) => {
    const { project } = await ownerWithProject(request);
    const outsider = uniq('outsider');
    await signIn(page, outsider);

    // Not in their list.
    await expect(page.getByText('No projects yet.')).toBeVisible();
    await expect(page.getByText('Confidential Project')).toHaveCount(0);

    // Not reachable by guessing the address, at any depth.
    for (const path of [
      `/projects/${project}`,
      `/projects/${project}/environments/production`,
      `/projects/${project}/environments/production/experiments/checkout`,
      `/projects/${project}/environments/production/experiments/checkout/results?metric=purchase`,
      `/projects/${project}/environments/production/keys`,
    ]) {
      await page.goto(path);
      await expect(page.getByRole('alert')).toContainText(NO_ACCESS);
      await expect(page.getByText('Confidential Project')).toHaveCount(0);
    }
  });

  test('a project that does not exist looks exactly like one the person cannot access', async ({ page, request }) => {
    const { project } = await ownerWithProject(request);
    await signIn(page, uniq('outsider'));

    await page.goto(`/projects/${project}`);
    const forbidden = await page.getByRole('alert').textContent();
    await page.goto(`/projects/${uniq('never-existed')}`);
    const missing = await page.getByRole('alert').textContent();

    expect(missing).toBe(forbidden); // project keys cannot be probed
  });

  test('each person sees only their own projects', async ({ page, request }) => {
    const mine = await ownerWithProject(request);
    await ownerWithProject(request); // someone else's, created in between

    await signIn(page, mine.owner);

    await expect(page.getByRole('link', { name: /Confidential Project/ })).toHaveCount(1);
    await expect(page.getByRole('link', { name: new RegExp(mine.project) })).toBeVisible();
  });

  test('a viewer can read but is refused when trying to change something', async ({ page, request }) => {
    const { project, api } = await ownerWithProject(request);
    const viewer = uniq('viewer');
    await api.addMember(project, viewer, 'Viewer');

    await signIn(page, viewer, `/projects/${project}/environments/production`);

    // Reading works.
    await expect(page.getByRole('link', { name: /checkout/i }).first()).toBeVisible();

    // Changing does not, and the person is told why rather than left guessing.
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Sneaky');
    await page.getByRole('textbox', { name: 'Key', exact: true }).fill('sneaky');
    await page.getByRole('button', { name: 'Create experiment' }).click();
    await expect(page.getByRole('alert')).toContainText(NO_ACCESS);
    await expect(page.getByRole('link', { name: /Sneaky/ })).toHaveCount(0);
  });

  test('an editor can run experiments but cannot manage SDK keys', async ({ page, request }) => {
    const { project, api } = await ownerWithProject(request);
    const editor = uniq('editor');
    await api.addMember(project, editor, 'Editor');

    // Can create an experiment...
    await signIn(page, editor, `/projects/${project}/environments/production`);
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Editors idea');
    await page.getByRole('button', { name: 'Create experiment' }).click();
    await expect(page).toHaveURL(/\/experiments\/editors-idea$/);

    // ...but not read the key list or make a key.
    await page.goto(`/projects/${project}/environments/production/keys`);
    await expect(page.getByRole('alert')).toContainText(NO_ACCESS);
  });

  test('removing someone’s access takes effect straight away', async ({ page, request }) => {
    const { project, api } = await ownerWithProject(request);
    const member = uniq('member');
    await api.addMember(project, member, 'Viewer');
    await signIn(page, member, `/projects/${project}`);
    await expect(page.getByRole('heading', { name: 'Confidential Project', level: 1 })).toBeVisible();

    const removal = await request.delete(`/v1/projects/${project}/members/${member}`, { headers: { Authorization: `Bearer ${api.token}` } });
    expect(removal.status()).toBe(204);
    await page.reload();

    await expect(page.getByRole('alert')).toContainText(NO_ACCESS);
  });
});
