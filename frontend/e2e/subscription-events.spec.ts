import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type APIResponse, type Page, type Response } from '@playwright/test';
import { loginQa } from './helpers/mfa';
import { subscriptionFrom, subscriptionMeFrom, type Subscription } from '../src/api/subscriptions';
import {
  subscriptionEventKeys,
  subscriptionEventReportFrom,
  type EventInterval,
} from '../src/api/subscriptionEvents';

const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
const reportPath = '/api/v1/admin/reports/subscriptions/events';
const route = '/admin/reports/subscription-events';
const number = new Intl.NumberFormat('es-PE');
const labels = [
  'Primera activación',
  'Asignación manual de plan',
  'Renovación manual de plan',
  'Reactivación por el usuario',
  'Cancelación por el usuario',
  'Activa → cancelada',
  'Activa → suspendida',
  'Cancelada → activa',
  'Cancelada → suspendida',
  'Suspendida → activa',
  'Suspendida → cancelada',
  'Suspensión por recuperación',
  'Otros eventos registrados',
];
function guard() {
  const value = process.env['BASE_URL'];
  if (
    !password ||
    !/^acropolis_test_[a-z0-9_]+(?![\s\S])/.test(process.env['QA_PROJECT'] ?? '') ||
    !/^qa-load-\d{6}@example\.test(?![\s\S])/.test(ownerEmail) ||
    !value
  )
    throw new Error('Subscription events E2E requires isolated synthetic QA.');
  const origin = new URL(value);
  if (
    origin.protocol !== 'https:' ||
    !/^qa-[a-z0-9]+\.test(?![\s\S])/.test(origin.hostname) ||
    origin.username ||
    origin.password ||
    origin.port !== '8443' ||
    origin.pathname !== '/' ||
    origin.search ||
    origin.hash
  )
    throw new Error('Subscription events E2E requires the isolated HTTPS QA origin.');
  return origin.origin;
}
function dayUtc(offset = 0, initial = Date.now()) {
  const date = new Date(initial);
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate() + offset))
    .toISOString()
    .slice(0, 10);
}
function observeReportRequests(page: Page) {
  const requests: { method: string; query: [string, string][] }[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname === reportPath)
      requests.push({ method: request.method(), query: [...url.searchParams.entries()] });
  });
  return requests;
}
function record(value: unknown, keys: string[]): Record<string, unknown> {
  expect(value !== null && typeof value === 'object' && !Array.isArray(value)).toBe(true);
  const result = value as Record<string, unknown>;
  expect(Object.keys(result).sort()).toEqual([...keys].sort());
  return result;
}
async function readReport(
  http: APIResponse | Response,
  interval: EventInterval,
  privateValues: string[],
) {
  expect(http.status()).toBe(200);
  expect(http.headers()['cache-control']).toContain('no-store');
  const raw: unknown = await http.json();
  const root = record(raw, [
    'scope',
    'generatedUtc',
    'fromUtc',
    'toUtc',
    'totalEvents',
    'byEvent',
    'days',
  ]);
  expect(Array.isArray(root['byEvent'])).toBe(true);
  for (const value of root['byEvent'] as unknown[]) record(value, ['key', 'count']);
  expect(Array.isArray(root['days'])).toBe(true);
  for (const value of root['days'] as unknown[]) {
    const day = record(value, ['dayUtc', 'totalEvents', 'byEvent']);
    expect(Array.isArray(day['byEvent'])).toBe(true);
    for (const bucket of day['byEvent'] as unknown[]) record(bucket, ['key', 'count']);
  }
  for (const value of privateValues) expect(JSON.stringify(raw)).not.toContain(value);
  const report = subscriptionEventReportFrom(raw, interval);
  expect(report.scope).toBe('recorded_events');
  expect(report.byEvent.map((row) => row.key)).toEqual([...subscriptionEventKeys]);
  expect(report.byEvent.reduce((sum, row) => sum + row.count, 0)).toBe(report.totalEvents);
  expect(report.days.map((row) => row.dayUtc)).toEqual([
    interval.from,
    dayUtc(1, Date.parse(interval.from + 'T00:00:00Z')),
  ]);
  expect(report.days.reduce((sum, row) => sum + row.totalEvents, 0)).toBe(report.totalEvents);
  for (const day of report.days) {
    expect(day.byEvent.map((row) => row.key)).toEqual([...subscriptionEventKeys]);
    expect(day.byEvent.reduce((sum, row) => sum + row.count, 0)).toBe(day.totalEvents);
  }
  for (const row of report.byEvent)
    expect(
      report.days.reduce(
        (sum, day) => sum + day.byEvent.find((bucket) => bucket.key === row.key)!.count,
        0,
      ),
    ).toBe(row.count);
  return report;
}
async function consult(page: Page, interval: EventInterval, privateValues: string[]) {
  const response = page.waitForResponse(
    (http) => new URL(http.url()).pathname === reportPath && http.request().method() === 'GET',
  );
  await page.getByRole('button', { name: 'Consultar período', exact: true }).click();
  const report = await readReport(await response, interval, privateValues);
  const results = page.getByRole('region', { name: 'Cambios registrados', exact: true });
  await expect(results).toBeVisible();
  await expect(results.locator('dd')).toHaveText(number.format(report.totalEvents));
  return report;
}
async function movement(page: Page, action: 'activate' | 'cancel', previous: Subscription | null) {
  const path = '/api/v1/subscriptions/' + action;
  const response = page.waitForResponse(
    (http) => new URL(http.url()).pathname === path && http.request().method() === 'POST',
  );
  await page
    .getByRole('button', {
      name: action === 'cancel' ? 'Confirmar cancelación' : 'Suscribirme gratis',
      exact: true,
    })
    .click();
  const http = await response;
  expect(http.status()).toBe(200);
  expect(http.headers()['cache-control']).toContain('no-store');
  expect(http.request().postDataJSON()).toEqual(
    action === 'cancel' ? { version: previous!.version } : {},
  );
  const subscription = subscriptionFrom(await http.json());
  expect(subscription.status).toBe(action === 'cancel' ? 'cancelled' : 'active');
  if (previous) {
    expect(subscription.id).toBe(previous.id);
    expect(subscription.userId).toBe(previous.userId);
    expect(subscription.version).not.toBe(previous.version);
  }
  await expect(page.locator('.subscription-status .status-badge')).toHaveText(
    action === 'cancel' ? 'Cancelada' : 'Activa',
  );
  await expect(page.getByRole('status')).toHaveText(
    action === 'cancel' ? 'Cancelaste tu suscripción gratuita.' : 'Tu acceso gratuito está activo.',
  );
  return subscription;
}

test.use({ trace: 'off' });
test('@modernization subscription event periods reflect three real QA transitions without exposing accounts', async ({
  page,
  browser,
}, testInfo) => {
  test.setTimeout(180_000);
  const origin = guard();
  const started = Date.now();
  const interval = { from: dayUtc(0, started), to: dayUtc(2, started) };
  const through = dayUtc(1, started);
  const memberEmail = testInfo.project.use.isMobile
    ? 'qa-load-000100@example.test'
    : 'qa-load-000099@example.test';
  expect(memberEmail).not.toBe(ownerEmail);
  const owner = await loginQa(page.request, ownerEmail, password!);
  expect(owner.permissions).toContain('Subscriptions.Manage');
  const options = {
    baseURL: origin,
    ignoreHTTPSErrors: false,
    viewport: page.viewportSize()!,
    isMobile: Boolean(testInfo.project.use.isMobile),
    hasTouch: testInfo.project.use.hasTouch ?? false,
    deviceScaleFactor: testInfo.project.use.deviceScaleFactor ?? 1,
    ...(testInfo.project.use.userAgent ? { userAgent: testInfo.project.use.userAgent } : {}),
  };
  const memberContext = await browser.newContext(options);
  const anonymousContext = await browser.newContext(options);
  try {
    const member = await loginQa(memberContext.request, memberEmail, password!);
    expect(member.permissions).toEqual([]);
    const memberPage = await memberContext.newPage();
    const memberReports = observeReportRequests(memberPage);
    const anonymousPage = await anonymousContext.newPage();
    const anonymousReports = observeReportRequests(anonymousPage);
    const query = new URLSearchParams(interval).toString();
    const anonymous = await anonymousContext.request.get(reportPath + '?' + query);
    expect(anonymous.status()).toBe(401);
    expect(anonymous.headers()['cache-control']).toContain('no-store');
    expect((await anonymous.json()) as unknown).toMatchObject({ code: 'invalid_credentials' });
    await anonymousPage.goto(route);
    await expect(anonymousPage).toHaveURL(/\/login$/);
    expect(anonymousReports).toEqual([]);
    const forbidden = await memberContext.request.get(reportPath + '?' + query);
    expect(forbidden.status()).toBe(403);
    expect(forbidden.headers()['cache-control']).toContain('no-store');
    expect((await forbidden.json()) as unknown).toMatchObject({ code: 'forbidden' });
    await memberPage.goto(route);
    await expect(
      memberPage.getByRole('heading', { name: 'Este espacio requiere autorización.', level: 1 }),
    ).toBeVisible();
    await expect(memberPage.getByRole('form', { name: 'Período de movimientos' })).toHaveCount(0);
    expect(memberReports).toEqual([]);

    const requests = observeReportRequests(page);
    await page.goto(route);
    await expect(
      page.getByRole('heading', { name: 'Movimientos de suscripción', level: 1 }),
    ).toBeVisible();
    const form = page.getByRole('form', { name: 'Período de movimientos', exact: true });
    await expect(form).toBeVisible();
    expect(requests).toEqual([]);
    await form.getByLabel('Desde', { exact: true }).fill(interval.from);
    await form.getByLabel('Hasta (incluido)', { exact: true }).fill(through);
    expect(requests).toEqual([]);
    const privateValues = [
      ownerEmail,
      owner.id,
      owner.displayName,
      memberEmail,
      member.id,
      member.displayName,
    ];
    const baseline = await consult(page, interval, privateValues);
    expect(requests).toEqual([
      {
        method: 'GET',
        query: [
          ['from', interval.from],
          ['to', interval.to],
        ],
      },
    ]);
    await form.getByLabel('Hasta (incluido)', { exact: true }).fill(interval.from);
    await expect(
      page.getByRole('region', { name: 'Cambios registrados', exact: true }),
    ).toHaveCount(0);
    await form.getByLabel('Hasta (incluido)', { exact: true }).fill(through);
    expect(requests).toHaveLength(1);

    const writes: { path: string; method: string }[] = [];
    memberPage.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (
        path.startsWith('/api/v1/subscriptions/') &&
        ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())
      )
        writes.push({ path, method: request.method() });
    });
    const initialResponse = memberPage.waitForResponse(
      (http) =>
        new URL(http.url()).pathname === '/api/v1/subscriptions/me' &&
        http.request().method() === 'GET',
    );
    await memberPage.goto('/profile/subscription');
    const initialHttp = await initialResponse;
    expect(initialHttp.status()).toBe(200);
    const initial = subscriptionMeFrom(await initialHttp.json());
    expect(initial.subscription).toBeNull();
    expect(initial.eligibleToActivate).toBe(true);
    await expect(memberPage.locator('.subscription-status .status-badge')).toHaveText(
      'Sin suscripción',
    );
    const activated = await movement(memberPage, 'activate', null);
    expect(activated.userId).toBe(member.id);
    await memberPage.getByRole('button', { name: 'Cancelar suscripción', exact: true }).click();
    await expect(memberPage.getByRole('alert')).toContainText('Al cancelar perderás el acceso');
    expect(writes).toHaveLength(1);
    const cancelled = await movement(memberPage, 'cancel', activated);
    const reactivated = await movement(memberPage, 'activate', cancelled);
    expect(reactivated.cancelledUtc).toBeNull();
    expect(writes).toEqual([
      { path: '/api/v1/subscriptions/activate', method: 'POST' },
      { path: '/api/v1/subscriptions/cancel', method: 'POST' },
      { path: '/api/v1/subscriptions/activate', method: 'POST' },
    ]);
    expect(requests).toHaveLength(1);
    const final = await consult(page, interval, privateValues);
    expect(final.totalEvents - baseline.totalEvents).toBe(3);
    for (const [index, key] of subscriptionEventKeys.entries()) {
      const delta = ['activated', 'reactivated', 'cancelled'].includes(key) ? 1 : 0;
      expect(final.byEvent[index]!.count - baseline.byEvent[index]!.count).toBe(delta);
      const row = page
        .getByRole('row')
        .filter({ has: page.getByRole('rowheader', { name: labels[index]!, exact: true }) });
      await expect(row).toHaveCount(1);
      await expect(row).toBeVisible();
      await expect(row.locator('td')).toHaveText(number.format(final.byEvent[index]!.count));
      for (const [dayIndex, day] of final.days.entries())
        expect(
          day.byEvent[index]!.count - baseline.days[dayIndex]!.byEvent[index]!.count,
        ).toBeGreaterThanOrEqual(0);
    }
    await page.getByText('Ver detalle diario (2 días)', { exact: true }).click();
    const daily = page.getByRole('table', { name: 'Eventos registrados por día UTC', exact: true });
    await expect(daily).toBeVisible();
    await expect(daily.locator('tbody tr')).toHaveCount(2);
    for (const [index, day] of final.days.entries()) {
      const row = daily.locator('tbody tr').nth(index);
      await expect(row.locator('time')).toHaveAttribute('datetime', day.dayUtc);
      await expect(row.locator('td')).toHaveText(number.format(day.totalEvents));
      expect(day.totalEvents - baseline.days[index]!.totalEvents).toBeGreaterThanOrEqual(0);
    }
    const main = page.locator('main');
    for (const value of privateValues) await expect(main).not.toContainText(value);
    await expect(main).toContainText('las fechas se consultan en UTC');
    await expect(main).toContainText('no equivalen a personas únicas ni suscripciones vigentes');
    await expect(main).toContainText('sin reconstruir períodos anteriores');
    const results = page.getByRole('region', { name: 'Cambios registrados', exact: true });
    await expect(results.locator('.event-updated time')).toHaveAttribute(
      'datetime',
      final.generatedUtc,
    );
    expect(requests).toEqual([
      {
        method: 'GET',
        query: [
          ['from', interval.from],
          ['to', interval.to],
        ],
      },
      {
        method: 'GET',
        query: [
          ['from', interval.from],
          ['to', interval.to],
        ],
      },
    ]);
    const finishedDay = dayUtc();
    expect(finishedDay >= interval.from && finishedDay < interval.to).toBe(true);
    for (const subscription of [activated, cancelled, reactivated])
      expect(
        subscription.updatedUtc.slice(0, 10) >= interval.from &&
          subscription.updatedUtc.slice(0, 10) < interval.to,
      ).toBe(true);
    await page.evaluate(async () => {
      await document.fonts.ready;
    });
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    const screenshot = testInfo.outputPath('subscription-events-' + testInfo.project.name + '.png');
    await page.screenshot({
      path: screenshot,
      fullPage: true,
      animations: 'disabled',
      caret: 'hide',
    });
    fs.chmodSync(screenshot, 0o600);
  } finally {
    await Promise.all([memberContext.close(), anonymousContext.close()]);
  }
});
