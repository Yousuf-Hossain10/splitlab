import { expect, test } from '@playwright/test';
import { signIn, uniq } from './support';

test.describe('signing in', () => {
  test('sends a signed-out visitor to sign-in and brings them back after', async ({ page }) => {
    await page.goto('/projects');

    await expect(page).toHaveURL(/\/sign-in\?returnUrl=%2Fprojects$/);

    await page.getByLabel('Your name').fill(uniq('alice'));
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible();
  });

  test('shows who is signed in, and signing out locks the app again', async ({ page }) => {
    const name = uniq('bob');
    await signIn(page, name);

    await expect(page.getByText(`Signed in as ${name}`)).toBeVisible();

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/sign-in$/);

    await page.goto('/projects');
    await expect(page).toHaveURL(/\/sign-in/);
  });

  test('stays signed in across a page reload within the tab', async ({ page }) => {
    await signIn(page, uniq('carol'));
    await expect(page).toHaveURL(/\/projects$/);

    await page.reload();

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible();
  });

  test('will not follow a return link that leaves the site', async ({ page }) => {
    await signIn(page, uniq('dave'), 'https://evil.example/phish');

    await expect(page).toHaveURL(/localhost:4200\/projects$/);
  });

  test('can sign in with a pasted token', async ({ page, request }) => {
    const name = uniq('erin');
    const { token } = (await (await request.post('/dev/token', { data: { subject: name } })).json()) as { token: string };
    await page.goto('/sign-in');

    await page.getByRole('textbox', { name: 'Token', exact: true }).fill(token);
    await page.getByRole('button', { name: 'Use token' }).click();

    await expect(page.getByText(`Signed in as ${name}`)).toBeVisible();
  });

  test('a token the server rejects is discarded and sends the person back to sign-in', async ({ page }) => {
    await page.goto('/sign-in');
    // Structurally a token, but not one this server issued.
    const forged = `${btoa('{"alg":"none"}')}.${btoa(JSON.stringify({ sub: 'mallory', exp: 4_000_000_000 }))}.sig`;
    await page.getByRole('textbox', { name: 'Token', exact: true }).fill(forged);

    // The page accepts it at first (it cannot verify a signature), then the server refuses it.
    const refused = page.waitForResponse((r) => r.url().includes('/v1/projects') && r.status() === 401);
    await page.getByRole('button', { name: 'Use token' }).click();
    await refused;

    await expect(page).toHaveURL(/\/sign-in/);
    expect(await page.evaluate(() => sessionStorage.getItem('splitlab.token'))).toBeNull();
  });
});
