import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { loginQa } from './helpers/mfa';

const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
const adminBase = '/api/v1/admin/content';
const videoId = 'M7lc1UVf-VE';
const pastedUrl = 'https://www.youtube.com/watch?v=' + videoId + '&si=qa-sharing&t=15';
type Content = { id: string; slug: string; youTubeId: string; version: string; status: string };

test.use({ trace: 'off' });
test('@modernization pasted YouTube URL persists only its identifier and reload never writes or loads a player', async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(150_000);
  if (
    !password ||
    !process.env['QA_PROJECT']?.startsWith('acropolis_test_') ||
    !process.env['BASE_URL']?.startsWith('https://qa-') ||
    !/^qa-load-\d{6}@example\.test$/.test(ownerEmail)
  )
    throw new Error('Isolated HTTPS QA and a synthetic owner are required.');
  const origin = new URL(process.env['BASE_URL']).origin;
  let externalRequests = 0;
  await page.route('**/*', async (route) => {
    if (new URL(route.request().url()).origin !== origin) {
      externalRequests++;
      await route.abort();
    } else {
      await route.continue();
    }
  });
  expect((await request.get(adminBase)).status()).toBe(401);
  const owner = await loginQa(page.request, ownerEmail, password);
  expect(owner.permissions).toContain('Content.Manage');
  const writes: string[] = [];
  page.on('request', (value) => {
    const path = new URL(value.url()).pathname;
    if (
      ['POST', 'PUT', 'PATCH', 'DELETE'].includes(value.method()) &&
      (path === adminBase || path.startsWith(adminBase + '/'))
    )
      writes.push(value.method() + ' ' + path);
  });
  const suffix = testInfo.project.name + '-' + Date.now();
  const slug = 'qa-youtube-url-' + suffix;
  await page.goto('/admin/content/new');
  await page.getByLabel('Título', { exact: true }).fill('Vídeo autorizado de QA ' + suffix);
  await page.getByLabel('Dirección del contenido', { exact: true }).fill(slug);
  await page.getByLabel('Categoría del contenido', { exact: true }).selectOption('videos');
  await page.getByLabel('Resumen', { exact: true }).fill('Resumen público sintético de QA.');
  await page
    .getByLabel('Sinopsis ampliada', { exact: true })
    .fill('Sinopsis pública separada de la referencia audiovisual.');
  const field = page.getByLabel('Enlace o identificador de YouTube', { exact: true });
  await expect(field).toHaveAttribute('maxlength', '2048');
  await field.fill(pastedUrl);
  await expect(field).toHaveValue(pastedUrl);
  await expect(page.locator('iframe')).toHaveCount(0);
  expect(writes).toEqual([]);
  expect(externalRequests).toBe(0);

  const createResponse = page.waitForResponse(
    (value) => new URL(value.url()).pathname === adminBase && value.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Guardar borrador', exact: true }).click();
  const created = await createResponse;
  expect(created.status()).toBe(201);
  const createPayload = created.request().postDataJSON() as Record<string, unknown>;
  expect(createPayload).toMatchObject({ category: 'videos', youTubeId: videoId, workText: null });
  expect(JSON.stringify(createPayload)).not.toContain(pastedUrl);
  const draft = (await created.json()) as Content;
  expect(draft).toMatchObject({ slug, status: 'draft', youTubeId: videoId });
  const route = adminBase + '/' + draft.id;
  await expect(page).toHaveURL(new RegExp('/admin/content/' + draft.id + '$'));
  await expect(field).toHaveValue(videoId);
  expect(writes).toEqual(['POST ' + adminBase]);
  const persistedResponse = await page.request.get(route);
  expect(persistedResponse.status()).toBe(200);
  const persisted = (await persistedResponse.json()) as Content;
  expect(persisted).toMatchObject({
    id: draft.id,
    slug,
    youTubeId: videoId,
    status: 'draft',
    version: draft.version,
  });
  expect(JSON.stringify(persisted)).not.toContain(pastedUrl);
  expect((await request.get('/api/v1/catalog/content/' + slug)).status()).toBe(404);

  await page.getByRole('button', { name: 'Publicar contenido', exact: true }).click();
  const publishResponse = page.waitForResponse(
    (value) => new URL(value.url()).pathname === route && value.request().method() === 'PUT',
  );
  await page.getByRole('button', { name: 'Confirmar publicación', exact: true }).click();
  const actualPublished = await publishResponse;
  expect(actualPublished.status()).toBe(200);
  expect(actualPublished.request().postDataJSON()).toMatchObject({
    youTubeId: videoId,
    status: 'published',
    version: draft.version,
  });
  const published = (await actualPublished.json()) as Content;
  expect(published).toMatchObject({ id: draft.id, slug, status: 'published', youTubeId: videoId });
  await expect(page.getByRole('status')).toContainText('Contenido publicado.');
  await expect(page.getByLabel('Dirección del contenido', { exact: true })).toHaveAttribute(
    'readonly',
    '',
  );
  const publicResponse = await request.get('/api/v1/catalog/content/' + slug);
  expect(publicResponse.status()).toBe(200);
  expect(publicResponse.headers()['cache-control']).toContain('no-store');
  const publicMetadata = (await publicResponse.json()) as Record<string, unknown>;
  for (const restricted of ['version', 'workText', 'youTubeId', 'itemIds'])
    expect(publicMetadata).not.toHaveProperty(restricted);
  expect(JSON.stringify(publicMetadata)).not.toContain(videoId);
  expect(JSON.stringify(publicMetadata)).not.toContain(pastedUrl);
  expect((await request.get('/api/v1/consumption/content/' + slug)).status()).toBe(401);

  const readAfterReload = page.waitForResponse(
    (value) => new URL(value.url()).pathname === route && value.request().method() === 'GET',
  );
  await page.reload();
  const reloadedResponse = await readAfterReload;
  expect(reloadedResponse.status()).toBe(200);
  const reloaded = (await reloadedResponse.json()) as Content;
  expect(reloaded).toMatchObject({
    id: draft.id,
    slug,
    status: 'published',
    youTubeId: videoId,
    version: published.version,
  });
  await expect(field).toHaveValue(videoId);
  await expect(page.locator('form.content-editor-form')).toHaveAttribute('aria-busy', 'false');
  const unchangedResponse = await page.request.get(route);
  expect(unchangedResponse.status()).toBe(200);
  expect((await unchangedResponse.json()) as Content).toEqual(reloaded);
  expect(writes).toEqual(['POST ' + adminBase, 'PUT ' + route]);
  await expect(page.locator('iframe, script[src*="youtube"]')).toHaveCount(0);
  expect(externalRequests).toBe(0);
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  const screenshot = testInfo.outputPath('youtube-url-editor-' + testInfo.project.name + '.png');
  await page.screenshot({
    path: screenshot,
    fullPage: true,
    animations: 'disabled',
    caret: 'hide',
  });
  fs.chmodSync(screenshot, 0o600);
});
