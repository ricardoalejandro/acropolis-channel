import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { loginQa } from './helpers/mfa';
const password = process.env['QA_IDENTITY_PASSWORD'];
const email = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
test.use({ trace: 'off' });
test('@modernization last sign-in displays the real new authenticated QA session only on its user detail', async ({
  page,
}, testInfo) => {
  test.setTimeout(150_000);
  if (
    !password ||
    !process.env['QA_PROJECT']?.startsWith('acropolis_test_') ||
    !process.env['BASE_URL']?.startsWith('https://qa-')
  )
    throw new Error('Isolated synthetic QA required.');
  const signInStartedUtcMs = Date.now();
  const account = await loginQa(page.request, email, password);
  const signInCompletedUtcMs = Date.now();
  expect(account.permissions).toContain('Users.Manage');
  const route = '/api/v1/admin/users/' + account.id + '/access';
  const requests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.endsWith('/access')) requests.push(request.method());
  });
  await page.goto('/admin/users');
  await expect(page.getByRole('heading', { name: 'Usuarios', level: 1 })).toBeVisible();
  expect(requests).toEqual([]);
  const response = page.waitForResponse((value) => new URL(value.url()).pathname === route);
  await page.goto('/admin/users/' + account.id);
  const actual = await response;
  expect(actual.status()).toBe(200);
  expect(actual.headers()['cache-control']).toContain('no-store');
  const record = (await actual.json()) as { lastSignInUtc: string | null };
  expect(Object.keys(record)).toEqual(['lastSignInUtc']);
  expect(typeof record.lastSignInUtc).toBe('string');
  const lastSignInUtcMs = Date.parse(record.lastSignInUtc!);
  expect(Number.isFinite(lastSignInUtcMs)).toBe(true);
  expect(lastSignInUtcMs).toBeGreaterThanOrEqual(signInStartedUtcMs);
  expect(lastSignInUtcMs).toBeLessThanOrEqual(signInCompletedUtcMs);
  const section = page.getByRole('region', { name: 'Último ingreso registrado', exact: true });
  await expect(section).toHaveAttribute('aria-busy', 'false');
  await expect(section.locator('time')).toHaveAttribute('datetime', record.lastSignInUtc!);
  await expect(section).toContainText('no corresponde a la última visita ni reproducción');
  expect(requests).toEqual(['GET']);
  await page.route(
    '**' + route,
    async (intercept) => {
      await intercept.fulfill({
        status: 503,
        contentType: 'application/json',
        headers: { 'Cache-Control': 'no-store' },
        body: JSON.stringify({ code: 'service_unavailable', detail: 'synthetic private detail' }),
      });
    },
    { times: 1 },
  );
  await page.reload();
  await expect(section).toHaveAttribute('aria-busy', 'false');
  await expect(section.getByRole('alert')).toContainText('No pudimos consultar');
  await expect(section).not.toContainText('synthetic private detail');
  const retryResponse = page.waitForResponse((value) => new URL(value.url()).pathname === route);
  const retry = section.getByRole('button', { name: 'Reintentar último ingreso', exact: true });
  await retry.focus();
  await expect(retry).toBeFocused();
  await retry.press('Enter');
  const retried = await retryResponse;
  expect(retried.status()).toBe(200);
  const next = (await retried.json()) as { lastSignInUtc: string | null };
  await expect(section).toHaveAttribute('aria-busy', 'false');
  await expect(section.locator('time')).toHaveAttribute('datetime', next.lastSignInUtc!);
  expect(next.lastSignInUtc).toBe(record.lastSignInUtc);
  expect(requests).toEqual(['GET', 'GET', 'GET']);
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  const path = testInfo.outputPath('last-sign-in-' + testInfo.project.name + '.png');
  await page.screenshot({ path, fullPage: true, animations: 'disabled', caret: 'hide' });
  fs.chmodSync(path, 0o600);
});
