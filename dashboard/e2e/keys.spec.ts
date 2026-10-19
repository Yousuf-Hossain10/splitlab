import { expect, test } from '@playwright/test';
import { Api, signIn, uniq } from './support';

test.describe('SDK keys', () => {
  async function setup(request: Parameters<typeof Api.signedInAs>[0]) {
    const owner = uniq('owner');
    const project = uniq('proj');
    const api = await Api.signedInAs(request, owner);
    await api.createProject(project);
    await api.createEnvironment(project, 'production');
    return { owner, project, api, keysUrl: `/projects/${project}/environments/production/keys` };
  }

  const datafileStatus = async (request: Parameters<typeof Api.signedInAs>[0], key: string) =>
    (await request.get('/v1/config/production', { headers: { Authorization: `Bearer ${key}` } })).status();

  test('the secret is shown once, works, and is gone for good once dismissed', async ({ page, request }) => {
    const { owner, keysUrl } = await setup(request);
    await signIn(page, owner, keysUrl);

    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('web server');
    await page.getByRole('button', { name: 'Create key' }).click();

    const secret = page.getByTestId('secret');
    await expect(secret).toBeVisible();
    await expect(page.getByText('it is shown only once')).toBeVisible();
    const value = (await secret.textContent())!.trim();
    expect(value).toMatch(/^sl_[0-9a-f]{12}_/);

    // It really is a working credential.
    expect(await datafileStatus(request, value)).toBe(200);

    // Dismissing forgets it, and reloading cannot bring it back: the server never stored it.
    await page.getByRole('button', { name: 'I have saved it' }).click();
    await expect(secret).toHaveCount(0);
    await page.reload();
    await expect(page.getByText('web server')).toBeVisible();
    await expect(page.getByTestId('secret')).toHaveCount(0);
    await expect(page.getByText(value)).toHaveCount(0);
    // Only the non-secret prefix is listed.
    await expect(page.getByText(`${value.slice(0, 15)}…`)).toBeVisible();
  });

  test('revoking a key (after confirming) stops it working immediately', async ({ page, request }) => {
    const { owner, project, api, keysUrl } = await setup(request);
    const key = await api.createKey(project, 'production', ['config:read']);
    expect(await datafileStatus(request, key)).toBe(200);
    await signIn(page, owner, keysUrl);

    await page.getByRole('button', { name: 'Revoke e2e' }).click();
    await expect(page.getByRole('button', { name: 'Yes, revoke' })).toBeVisible();
    expect(await datafileStatus(request, key)).toBe(200); // nothing happens until confirmed

    await page.getByRole('button', { name: 'Yes, revoke' }).click();

    await expect(page.getByText('Revoked', { exact: true })).toBeVisible();
    expect(await datafileStatus(request, key)).toBe(401);
  });

  test('rotating swaps in a new working key and retires the old one', async ({ page, request }) => {
    const { owner, project, api, keysUrl } = await setup(request);
    const oldKey = await api.createKey(project, 'production', ['config:read']);
    await signIn(page, owner, keysUrl);

    await page.getByRole('button', { name: 'Rotate e2e' }).click();

    const fresh = (await page.getByTestId('secret').textContent())!.trim();
    expect(fresh).not.toBe(oldKey);
    expect(await datafileStatus(request, fresh)).toBe(200);
    expect(await datafileStatus(request, oldKey)).toBe(401);
  });

  test('a key needs a name and at least one permission', async ({ page, request }) => {
    const { owner, keysUrl } = await setup(request);
    await signIn(page, owner, keysUrl);
    const create = page.getByRole('button', { name: 'Create key' });

    await expect(create).toBeDisabled();
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('x');
    await expect(create).toBeEnabled();
    await page.getByLabel('Read configuration').uncheck();
    await page.getByLabel('Send events').uncheck();
    await expect(create).toBeDisabled();
  });
});
