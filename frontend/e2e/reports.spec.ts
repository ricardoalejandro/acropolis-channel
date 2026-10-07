import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { loginQa } from './helpers/mfa';
import {
  type IdentityReport,
  type CatalogReport,
  type SubscriptionReport,
} from '../src/api/reports';
const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
function guard() {
  if (
    !password ||
    !process.env['QA_PROJECT']?.startsWith('acropolis_test_') ||
    !process.env['BASE_URL']?.startsWith('https://qa-')
  )
    throw new Error('Reports E2E requires isolated HTTPS QA and synthetic accounts.');
}
async function accessible(page: Page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
}
test.use({ trace: 'off' });
test('@modernization current reports match real API aggregates and refresh independently', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000);
  guard();
  const account = await loginQa(page.request, ownerEmail, password!);
  expect(account.permissions).toEqual(
    expect.arrayContaining(['Users.Manage', 'Content.Manage', 'Subscriptions.Manage']),
  );
  await page.goto('/admin');
  const reportRequests: { module: string; method: string }[] = [];
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/api/v1/admin/reports/'))
      reportRequests.push({ module: path.split('/').at(-1)!, method: request.method() });
  });
  const identityResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === '/api/v1/admin/reports/identity',
  );
  const catalogResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === '/api/v1/admin/reports/catalog',
  );
  const subscriptionsResponse = page.waitForResponse(
    (response) => new URL(response.url()).pathname === '/api/v1/admin/reports/subscriptions',
  );
  const shortcut = page.getByRole('link', { name: 'Consultar reportes', exact: true });
  await shortcut.focus();
  await shortcut.press('Enter');
  await expect(page).toHaveURL(/\/admin\/reports$/);
  await expect(page.getByRole('heading', { name: 'Reportes', level: 1 })).toBeFocused();
  const [identityHttp, catalogHttp, subscriptionsHttp] = await Promise.all([
    identityResponse,
    catalogResponse,
    subscriptionsResponse,
  ]);
  for (const response of [identityHttp, catalogHttp, subscriptionsHttp]) {
    expect(response.status()).toBe(200);
    expect(response.headers()['cache-control']).toContain('no-store');
  }
  const identity = (await identityHttp.json()) as IdentityReport;
  const catalog = (await catalogHttp.json()) as CatalogReport;
  const subscriptions = (await subscriptionsHttp.json()) as SubscriptionReport;
  const number = new Intl.NumberFormat('es-PE');
  for (const [title, report] of [
    ['Usuarios', identity],
    ['Contenidos', catalog],
    ['Suscripciones', subscriptions],
  ] as const) {
    expect(report.scope).toBe('current');
    const section = page.getByRole('region', { name: title, exact: true });
    await expect(section).toHaveAttribute('aria-busy', 'false');
    await expect(section.locator('dd')).toHaveText(number.format(report.total));
    await expect(section.locator('time')).toHaveAttribute('datetime', report.generatedUtc);
  }
  const users = page.getByRole('table', { name: 'Usuarios por estado', exact: true });
  for (const [index, bucket] of identity.byStatus.entries())
    await expect(users.locator('tbody tr').nth(index).locator('td')).toHaveText(
      number.format(bucket.count),
    );
  const levels = page.getByRole('table', { name: 'Usuarios por nivel institucional', exact: true });
  for (const bucket of identity.byLevel)
    await expect(
      levels
        .getByRole('row')
        .filter({ has: page.getByRole('rowheader', { name: bucket.key, exact: true }) })
        .locator('td'),
    ).toHaveText(number.format(bucket.count));
  const matrix = page.getByRole('table', { name: 'Por categoría', exact: true });
  await expect(matrix.locator('tbody tr')).toHaveCount(6);
  for (const [index, category] of [
    'lecturas',
    'documentales',
    'videos',
    'podcast',
    'charlas-online',
    'cursos',
  ].entries()) {
    const expected = ['draft', 'published', 'archived'].map((status) =>
      number.format(
        catalog.byCategoryAndStatus.find(
          (row) => row.category === category && row.status === status,
        )!.count,
      ),
    );
    await expect(matrix.locator('tbody tr').nth(index).locator('td')).toHaveText(expected);
  }
  const kinds = page.getByRole('table', { name: 'Por tipo de contenido', exact: true });
  await expect(kinds.locator('tbody tr')).toHaveCount(3);
  for (const [index, kind] of ['work', 'course', 'program'].entries()) {
    const expected = ['draft', 'published', 'archived'].map((status) =>
      number.format(
        catalog.byKindAndStatus.find((row) => row.kind === kind && row.status === status)!.count,
      ),
    );
    await expect(kinds.locator('tbody tr').nth(index).locator('td')).toHaveText(expected);
  }
  const statuses = page.getByRole('table', { name: 'Suscripciones por estado registrado', exact: true });
  for (const [index, bucket] of subscriptions.byStatus.entries())
    await expect(statuses.locator('tbody tr').nth(index).locator('td')).toHaveText(
      number.format(bucket.count),
    );
  const effective = page.getByRole('table', { name: 'Acceso por vigencia', exact: true });
  for (const [index, bucket] of subscriptions.byEffectiveState.entries())
    await expect(effective.locator('tbody tr').nth(index).locator('td')).toHaveText(number.format(bucket.count));
  await expect(page.locator('main')).not.toContainText('@example.test');
  await expect(page.locator('main')).not.toContainText(account.id);
  await expect(page.locator('main')).toContainText('Una cuenta puede tener varios niveles');
  await expect(page.locator('main')).toContainText(
    'El acceso también requiere una cuenta activa y confirmada',
  );
  expect(reportRequests).toHaveLength(3);
  expect(reportRequests.every((request) => request.method === 'GET')).toBe(true);
  const refreshed = page.waitForResponse(
    (response) => new URL(response.url()).pathname === '/api/v1/admin/reports/catalog',
  );
  await page.getByRole('button', { name: 'Actualizar Contenidos', exact: true }).click();
  const next = await refreshed;
  expect(next.status()).toBe(200);
  const nextReport = (await next.json()) as CatalogReport;
  await expect(page.getByRole('region', { name: 'Contenidos', exact: true })).toHaveAttribute(
    'aria-busy',
    'false',
  );
  await expect(
    page.getByRole('region', { name: 'Contenidos', exact: true }).locator('time'),
  ).toHaveAttribute('datetime', nextReport.generatedUtc);
  expect(reportRequests.filter((request) => request.module === 'identity')).toHaveLength(1);
  expect(reportRequests.filter((request) => request.module === 'subscriptions')).toHaveLength(1);
  expect(reportRequests.filter((request) => request.module === 'catalog')).toHaveLength(2);
  if (testInfo.project.use.isMobile)
    await page.getByRole('button', { name: 'Menú de administración', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Reportes', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await accessible(page);
  const path = testInfo.outputPath('modernization-reports-' + testInfo.project.name + '.png');
  await page.screenshot({ path, fullPage: true, animations: 'disabled', caret: 'hide' });
  fs.chmodSync(path, 0o600);
});
for (const fixture of [
  {
    email: 'qa-load-000000@example.test',
    permission: 'Users.Manage',
    module: 'identity',
    title: 'Usuarios',
  },
  {
    email: 'qa-load-000080@example.test',
    permission: 'Content.Manage',
    module: 'catalog',
    title: 'Contenidos',
  },
]) {
  test(
    '@modernization report modules enforce the actual ' + fixture.permission + ' session',
    async ({ page }) => {
      test.setTimeout(150_000);
      guard();
      const account = await loginQa(page.request, fixture.email, password!);
      expect(account.permissions).toEqual([fixture.permission]);
      const requests: string[] = [];
      page.on('request', (request) => {
        if (new URL(request.url()).pathname.startsWith('/api/v1/admin/reports/'))
          requests.push(new URL(request.url()).pathname);
      });
      await page.goto('/admin/reports');
      const section = page.getByRole('region', { name: fixture.title, exact: true });
      await expect(section).toHaveAttribute('aria-busy', 'false');
      await expect(section.locator('table').first()).toBeVisible();
      await expect(page.locator('.report-panel')).toHaveCount(1);
      expect(requests).toEqual(['/api/v1/admin/reports/' + fixture.module]);
      for (const module of ['identity', 'catalog', 'subscriptions'].filter(
        (module) => module !== fixture.module,
      ))
        expect((await page.request.get('/api/v1/admin/reports/' + module)).status()).toBe(403);
      await accessible(page);
    },
  );
}
test('@modernization ordinary institutional users cannot load report data', async ({ page }) => {
  test.setTimeout(150_000);
  guard();
  const account = await loginQa(page.request, 'qa-load-000098@example.test', password!);
  expect(account.permissions).toEqual([]);
  const requests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith('/api/v1/admin/reports/'))
      requests.push(request.url());
  });
  await page.goto('/admin/reports');
  await expect(page.getByRole('alert')).toContainText('no tiene permisos para consultar reportes');
  await expect(page.locator('.report-panel')).toHaveCount(0);
  expect(requests).toEqual([]);
  for (const module of ['identity', 'catalog', 'subscriptions'])
    expect((await page.request.get('/api/v1/admin/reports/' + module)).status()).toBe(403);
  if (test.info().project.use.isMobile)
    await page.getByRole('button', { name: 'Menú de administración', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Reportes', exact: true })).toHaveCount(0);
  await accessible(page);
});
