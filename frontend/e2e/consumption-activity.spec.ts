import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import {
  expect,
  test,
  type APIRequestContext,
  type APIResponse,
  type Page,
  type Response,
} from '@playwright/test';
import { loginQa } from './helpers/mfa';
import {
  consumptionCapabilitiesFrom,
  consumptionSessionFrom,
  consumptionReceiptFrom,
  consumptionReportFrom,
  consumptionContentPageFrom,
  accountConsumptionReportFrom,
  type ConsumptionReceipt,
} from '../src/api/consumptionActivity';
const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
const generalPath = '/api/v1/admin/reports/catalog/consumption';
function guard() {
  const value = process.env['BASE_URL'];
  if (
    !password ||
    !/^acropolis_test_[a-z0-9_]+(?![\s\S])/.test(process.env['QA_PROJECT'] ?? '') ||
    !/^qa-load-\d{6}@example\.test(?![\s\S])/.test(ownerEmail) ||
    !value
  )
    throw new Error('Consumption E2E requires isolated synthetic QA.');
  const origin = new URL(value);
  if (
    origin.protocol !== 'https:' ||
    !/^qa-[a-z0-9]+\.test(?![\s\S])/.test(origin.hostname) ||
    origin.port !== '8443' ||
    origin.username ||
    origin.password ||
    origin.pathname !== '/' ||
    origin.search ||
    origin.hash
  )
    throw new Error('Consumption E2E requires the isolated HTTPS QA origin.');
  return origin.origin;
}
function date(offset = 0, start = Date.now()) {
  const now = new Date(start);
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + offset))
    .toISOString()
    .slice(0, 10);
}
async function write(
  request: APIRequestContext,
  path: string,
  method: 'POST' | 'PUT',
  data: object,
) {
  const csrf = await request.get('/api/v1/identity/csrf');
  expect(csrf.status()).toBe(200);
  const token = ((await csrf.json()) as { token: string }).token;
  return request.fetch('/api/v1' + path, { method, headers: { 'X-CSRF-TOKEN': token }, data });
}
async function body(response: APIResponse) {
  expect(response.status()).toBe(200);
  expect(response.headers()['cache-control']).toContain('no-store');
  return response.json() as Promise<unknown>;
}
function reportRequests(page: Page) {
  const requests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith(generalPath)) requests.push(request.method());
  });
  return requests;
}
async function queryForm(page: Page, from: string, to: string) {
  const form = page.getByRole('form', { name: 'Consultar consumo', exact: true });
  await form.getByLabel('Desde (UTC)', { exact: true }).fill(from);
  await form.getByLabel('Hasta (sin incluir, UTC)', { exact: true }).fill(to);
  return form;
}
test.use({ trace: 'off' });
test('@modernization visible authorized reading records real pulses and reports retained account activity', async ({
  page,
  browser,
}, testInfo) => {
  test.setTimeout(150_000);
  const origin = guard();
  const initial = Date.now();
  const from = date(0, initial);
  const to = date(2, initial);
  const query = new URLSearchParams({ from, to }).toString();
  const owner = await loginQa(page.request, ownerEmail, password!);
  expect(owner.permissions).toEqual(expect.arrayContaining(['Content.Manage', 'Users.Manage']));
  const capabilities = consumptionCapabilitiesFrom(
    await body(await page.request.get('/api/v1/consumption/capabilities')),
  );
  expect(capabilities.recordingEnabled).toBe(true);
  expect(capabilities.detailRetentionDays).toBe(90);
  expect(capabilities.generalRetentionDays).toBe(365);
  const slug = 'qa-consumption-' + testInfo.project.name + '-' + Date.now();
  const title = 'Lectura registrada QA ' + testInfo.project.name;
  const fields = {
    slug,
    title,
    category: 'lecturas',
    summary: 'Sinopsis pública de QA.',
    body: 'Descripción pública.',
    author: 'Institución QA',
    tags: ['QA aislada'],
    coverAsset: null,
    durationSeconds: null,
    workText: Array.from(
      { length: 24 },
      (_, index) =>
        'Párrafo sintético protegido ' +
        index +
        '. Observación del navegador de QA, sin datos personales.',
    ).join('\n\n'),
    youTubeId: null,
    collectionKind: null,
    itemIds: [],
  };
  const created = await write(page.request, '/admin/content', 'POST', fields);
  expect(created.status()).toBe(201);
  const draft = (await created.json()) as { id: string; version: string };
  const published = await write(page.request, '/admin/content/' + draft.id, 'PUT', {
    ...fields,
    version: draft.version,
    status: 'published',
  });
  const content = (await body(published)) as { id: string; version: string };
  expect(content.version).toMatch(/^[a-f0-9]{32}(?![\s\S])/);
  const memberEmail = testInfo.project.use.isMobile
    ? 'qa-load-000102@example.test'
    : 'qa-load-000101@example.test';
  expect(memberEmail).not.toBe(ownerEmail);
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
  try {
    const anonymous = await memberContext.request.get('/api/v1/consumption/content/' + slug);
    expect(anonymous.status()).toBe(401);
    expect(anonymous.headers()['cache-control']).toContain('no-store');
    const member = await loginQa(memberContext.request, memberEmail, password!);
    expect(member.permissions).toEqual([]);
    const forbidden = await memberContext.request.get(generalPath + '?' + query);
    expect(forbidden.status()).toBe(403);
    expect(forbidden.headers()['cache-control']).toContain('no-store');
    const accountPath = '/api/v1/admin/users/' + member.id + '/consumption';
    const baseline = accountConsumptionReportFrom(
      await body(await page.request.get(accountPath + '?' + query + '&page=1&pageSize=100')),
    );
    const memberPage = await memberContext.newPage();
    const starts: unknown[] = [];
    const pulseBodies: Record<string, unknown>[] = [];
    const pulseResponses: Promise<ConsumptionReceipt>[] = [];
    memberPage.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (
        request.method() === 'POST' &&
        path === '/api/v1/consumption/content/' + slug + '/sessions'
      )
        starts.push(request.postDataJSON() as unknown);
      if (
        request.method() === 'POST' &&
        /^\/api\/v1\/consumption\/sessions\/[^/]+\/pulses$/.test(path)
      )
        pulseBodies.push(request.postDataJSON() as Record<string, unknown>);
    });
    const capturePulseResponse = (response: Response) => {
      if (
        response.request().method() === 'POST' &&
        /^\/api\/v1\/consumption\/sessions\/[^/]+\/pulses$/.test(new URL(response.url()).pathname)
      )
        pulseResponses.push(
          (async () => {
            expect(response.status()).toBe(200);
            expect(response.headers()['cache-control']).toContain('no-store');
            return consumptionReceiptFrom(await response.json());
          })(),
        );
    };
    memberPage.on('response', capturePulseResponse);
    await memberPage.goto('/content/' + slug);
    await expect(
      memberPage.getByRole('heading', { name: 'Activa tu acceso gratuito.', exact: true }),
    ).toBeVisible();
    expect(starts).toEqual([]);
    expect(pulseBodies).toEqual([]);
    await memberPage.goto('/profile/subscription');
    const activation = memberPage.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/v1/subscriptions/activate' &&
        response.request().method() === 'POST',
    );
    await memberPage.getByRole('button', { name: 'Suscribirme gratis', exact: true }).click();
    expect((await activation).status()).toBe(200);
    const startResponse = memberPage.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/v1/consumption/content/' + slug + '/sessions' &&
        response.request().method() === 'POST',
    );
    await memberPage.goto('/content/' + slug);
    const article = memberPage.getByRole('article', { name: 'Lectura completa', exact: true });
    await expect(article).toContainText('Párrafo sintético protegido');
    await memberPage.bringToFront();
    await article
      .getByRole('heading', { name: 'Lectura completa', exact: true })
      .scrollIntoViewIfNeeded();
    await article.getByRole('heading', { name: 'Lectura completa', exact: true }).click();
    await expect
      .poll(
        () =>
          memberPage.evaluate(() => document.hasFocus() && document.visibilityState === 'visible'),
        { timeout: 3000 },
      )
      .toBe(true);
    const opened = await startResponse;
    expect(opened.status()).toBe(200);
    expect(opened.headers()['cache-control']).toContain('no-store');
    const visit = consumptionSessionFrom(await opened.json());
    expect(visit.sourceKind).toBe('reading');
    expect(starts).toHaveLength(1);
    expect(starts[0]).toEqual({
      visitId: expect.stringMatching(/^[a-f0-9-]{36}(?![\s\S])/),
      contentVersion: content.version,
    });
    const firstPulse = memberPage.waitForResponse(
      (response) =>
        /^\/api\/v1\/consumption\/sessions\/[^/]+\/pulses$/.test(
          new URL(response.url()).pathname,
        ) && response.request().method() === 'POST',
      { timeout: 20_000 },
    );
    expect((await firstPulse).status()).toBe(200);
    // Close capture and drain every observed body before navigation releases network resources.
    memberPage.off('response', capturePulseResponse);
    const receipts = await Promise.all(pulseResponses);
    await memberPage.goto('/profile');
    expect(receipts.length).toBeGreaterThanOrEqual(1);
    expect(pulseBodies).toHaveLength(receipts.length);
    let creditedMs = 0;
    for (const [index, receipt] of receipts.entries()) {
      const packet = pulseBodies[index]!;
      expect(packet['sequence']).toBe(index + 1);
      expect(packet).toHaveProperty('reading');
      expect(packet).not.toHaveProperty('media');
      expect(packet).not.toHaveProperty('accountId');
      expect(Number(packet['intervalMs'])).toBeGreaterThan(0);
      expect(Number(packet['intervalMs'])).toBeLessThanOrEqual(15000);
      expect(receipt.sequence).toBe(index + 1);
      expect(receipt.creditedMs).toBeLessThanOrEqual(Number(packet['activeMs']));
      creditedMs += receipt.creditedMs;
    }
    expect(creditedMs).toBeGreaterThan(0);
    const after = accountConsumptionReportFrom(
      await body(await page.request.get(accountPath + '?' + query + '&page=1&pageSize=100')),
    );
    expect(after.summary.starts - baseline.summary.starts).toBe(1);
    expect(after.summary.recordedPulses - baseline.summary.recordedPulses).toBe(receipts.length);
    expect(after.summary.creditedMs - baseline.summary.creditedMs).toBe(creditedMs);
    const own = after.items.find(
      (row) => row.contentId === content.id && row.contentVersion === content.version,
    );
    expect(own).toBeDefined();
    expect(own!.metrics).toMatchObject({
      starts: 1,
      recordedPulses: receipts.length,
      creditedMs,
      accountsWithActivity: 1,
      endedReports: 0,
    });
    const report = consumptionReportFrom(
      await body(await page.request.get(generalPath + '?' + query)),
    );
    const contentPage = consumptionContentPageFrom(
      await body(
        await page.request.get(generalPath + '/content?' + query + '&page=1&pageSize=100'),
      ),
    );
    const general = contentPage.items.find(
      (row) => row.contentId === content.id && row.contentVersion === content.version,
    );
    expect(general?.metrics).toEqual(own!.metrics);
    for (const privateValue of [
      ownerEmail,
      owner.id,
      owner.displayName,
      memberEmail,
      member.id,
      member.displayName,
    ]) {
      expect(JSON.stringify(report)).not.toContain(privateValue);
      expect(JSON.stringify(contentPage)).not.toContain(privateValue);
    }
    const requests = reportRequests(page);
    await page.goto('/admin/reports/consumption');
    await expect(page.getByRole('heading', { name: 'Consumo registrado', level: 1 })).toBeVisible();
    expect(requests).toEqual([]);
    const form = await queryForm(page, from, to);
    expect(requests).toEqual([]);
    const ui = page.waitForResponse((response) => new URL(response.url()).pathname === generalPath);
    const button = form.getByRole('button', { name: 'Consultar consumo', exact: true });
    await button.focus();
    await expect(button).toBeFocused();
    await button.press('Enter');
    expect((await ui).status()).toBe(200);
    await expect(
      page.getByRole('heading', { name: 'Resumen del periodo', exact: true }),
    ).toBeVisible();
    await expect(page.getByRole('link', { name: title, exact: true })).toBeVisible();
    expect(requests).toEqual(['GET', 'GET']);
    await expect(page.getByText(/puede solaparse entre pestañas/)).toBeVisible();
    await expect(page.getByText(/no se suman entre días u obras/)).toBeVisible();
    await page.evaluate(async () => {
      await document.fonts.ready;
    });
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    const screenshot = testInfo.outputPath(
      'consumption-activity-' + testInfo.project.name + '.png',
    );
    await page.screenshot({
      path: screenshot,
      fullPage: true,
      animations: 'disabled',
      caret: 'hide',
    });
    fs.chmodSync(screenshot, 0o600);
    await page.goto('/admin/users/' + member.id + '/consumption');
    const accountForm = await queryForm(page, from, to);
    const accountResponse = page.waitForResponse(
      (response) => new URL(response.url()).pathname === accountPath,
    );
    await accountForm.getByRole('button', { name: 'Consultar consumo', exact: true }).click();
    expect((await accountResponse).status()).toBe(200);
    await expect(
      page.getByRole('heading', { name: 'Resumen del periodo', exact: true }),
    ).toBeVisible();
    await expect(page.getByRole('link', { name: title, exact: true })).toBeVisible();
    expect(date() >= from && date() < to).toBe(true);
    const accountScreenshot = testInfo.outputPath(
      'consumption-account-' + testInfo.project.name + '.png',
    );
    await page.screenshot({
      path: accountScreenshot,
      fullPage: true,
      animations: 'disabled',
      caret: 'hide',
    });
    fs.chmodSync(accountScreenshot, 0o600);
  } finally {
    await memberContext.close();
  }
});
