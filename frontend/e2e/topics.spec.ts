import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { loginQa } from './helpers/mfa';
import { adminTopicFrom, contentTopicsFrom, publicTopicPageFrom } from '../src/api/topics';
const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
const topicBase = '/api/v1/admin/catalog/topics';
function guard() {
  const value = process.env['BASE_URL'];
  if (
    !password ||
    !/^acropolis_test_[a-z0-9_]+(?![\s\S])/.test(process.env['QA_PROJECT'] ?? '') ||
    !/^qa-load-\d{6}@example\.test(?![\s\S])/.test(ownerEmail) ||
    !value
  )
    throw new Error('Topics E2E requires synthetic isolated QA.');
  const url = new URL(value);
  if (
    url.protocol !== 'https:' ||
    !/^qa-[a-z0-9]+\.test(?![\s\S])/.test(url.hostname) ||
    url.port !== '8443' ||
    url.username ||
    url.password ||
    url.pathname !== '/' ||
    url.search ||
    url.hash
  )
    throw new Error('Topics E2E requires the isolated HTTPS QA origin.');
  return url.origin;
}
async function createTopic(page: Page, name: string, slug: string) {
  await page.goto('/admin/topics/new');
  await page.getByLabel('Nombre del tema', { exact: true }).fill(name);
  await page.getByLabel('Dirección del tema', { exact: true }).fill(slug);
  const saved = page.waitForResponse(
    (http) => new URL(http.url()).pathname === topicBase && http.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Guardar tema', exact: true }).click();
  const response = await saved;
  expect(response.status()).toBe(201);
  expect(response.request().postDataJSON()).toEqual({ slug, name });
  const topic = adminTopicFrom(await response.json());
  await expect(page).toHaveURL(new RegExp('/admin/topics/' + topic.id + '$'));
  return topic;
}
async function state(page: Page, id: string, action: 'archive' | 'restore') {
  await page.goto('/admin/topics/' + id);
  await page
    .getByRole('button', {
      name: action === 'archive' ? 'Archivar tema' : 'Restaurar tema',
      exact: true,
    })
    .click();
  const saved = page.waitForResponse(
    (http) =>
      new URL(http.url()).pathname === topicBase + '/' + id + '/state' &&
      http.request().method() === 'PUT',
  );
  await page
    .getByRole('button', {
      name: action === 'archive' ? 'Confirmar archivo de tema' : 'Confirmar restauración de tema',
      exact: true,
    })
    .click();
  const response = await saved;
  expect(response.status()).toBe(200);
  return adminTopicFrom(await response.json());
}
async function accessible(page: Page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
    ),
  ).toBe(false);
}
test.use({ trace: 'off' });
test('@modernization Topics are maintained, assigned and filtered with real persisted versions without changing formats or tags', async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(180_000);
  const origin = guard();
  let foreign = 0;
  await page.route('**/*', async (route) => {
    if (new URL(route.request().url()).origin !== origin) {
      foreign++;
      await route.abort();
    } else await route.continue();
  });
  expect((await request.get(topicBase)).status()).toBe(401);
  const owner = await loginQa(page.request, ownerEmail, password!);
  expect(owner.permissions).toContain('Content.Manage');
  const writes: string[] = [];
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (
      ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()) &&
      (path.startsWith(topicBase) || path.startsWith('/api/v1/admin/content'))
    )
      writes.push(request.method() + ' ' + path);
  });
  const suffix = testInfo.project.name + '-' + Date.now();
  const first = await createTopic(page, 'Idea QA ' + suffix, 'idea-qa-' + suffix);
  const second = await createTopic(page, 'Otra idea QA ' + suffix, 'otra-idea-qa-' + suffix);
  await page.goto('/admin/topics');
  await page.getByRole('button', { name: 'Subir tema ' + second.name, exact: true }).click();
  await expect(page.getByText('Orden actualizado.', { exact: true })).toBeVisible();
  const persistedFirst = adminTopicFrom(
    await (await page.request.get(topicBase + '/' + first.id)).json(),
  );
  const persistedSecond = adminTopicFrom(
    await (await page.request.get(topicBase + '/' + second.id)).json(),
  );
  expect(persistedSecond.position).toBeLessThan(persistedFirst.position);
  const directoryScreenshot = testInfo.outputPath(
    'topics-directory-' + testInfo.project.name + '.png',
  );
  await page.screenshot({ path: directoryScreenshot, fullPage: true });
  fs.chmodSync(directoryScreenshot, 0o600);
  const title = 'Lectura QA ' + suffix;
  const slug = 'lectura-tema-qa-' + suffix;
  await page.goto('/admin/content/new');
  await page.getByLabel('Título', { exact: true }).fill(title);
  await page.getByLabel('Dirección del contenido', { exact: true }).fill(slug);
  await page.getByLabel('Categoría del contenido', { exact: true }).selectOption('lecturas');
  await expect(
    page.getByLabel('Categoría del contenido', { exact: true }).locator('option'),
  ).toHaveCount(6);
  await page.getByLabel('Etiquetas', { exact: true }).fill('etiqueta libre QA');
  await page.getByLabel('Resumen', { exact: true }).fill('Resumen público sintético.');
  await page.getByLabel('Sinopsis ampliada', { exact: true }).fill('Sinopsis pública preservada.');
  await page
    .getByLabel('Lectura completa', { exact: true })
    .fill('Obra privada sintética separada de los temas.');
  const created = page.waitForResponse(
    (http) =>
      new URL(http.url()).pathname === '/api/v1/admin/content' &&
      http.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Guardar borrador', exact: true }).click();
  const draftResponse = await created;
  expect(draftResponse.status()).toBe(201);
  const draft = (await draftResponse.json()) as { id: string; version: string };
  const contentPath = '/api/v1/admin/content/' + draft.id;
  await page.getByRole('link', { name: 'Gestionar temas del contenido', exact: true }).click();
  await page.getByRole('checkbox', { name: first.name, exact: true }).check();
  await page.getByRole('checkbox', { name: second.name, exact: true }).check();
  const saved = page.waitForResponse(
    (http) =>
      new URL(http.url()).pathname === contentPath + '/topics' && http.request().method() === 'PUT',
  );
  const saveButton = page.getByRole('button', { name: 'Guardar temas del contenido', exact: true });
  await saveButton.focus();
  await page.keyboard.press('Enter');
  const assignmentResponse = await saved;
  expect(assignmentResponse.status()).toBe(200);
  expect(assignmentResponse.request().postDataJSON()).toEqual({
    contentVersion: draft.version,
    topicIds: [first.id, second.id],
  });
  const assignment = contentTopicsFrom(await assignmentResponse.json());
  expect(assignment.contentVersion).not.toBe(draft.version);
  await expect(page.getByText('Temas del contenido guardados.', { exact: true })).toBeVisible();
  await accessible(page);
  const screenshot = testInfo.outputPath('topics-assignment-' + testInfo.project.name + '.png');
  await page.screenshot({ path: screenshot, fullPage: true });
  fs.chmodSync(screenshot, 0o600);
  const beforeReload = [...writes];
  await page.reload();
  await expect(
    page.getByRole('button', { name: 'Retirar tema ' + first.name, exact: true }),
  ).toBeVisible();
  expect(writes).toEqual(beforeReload);
  const actual = (await (await page.request.get(contentPath)).json()) as {
    category: string;
    tags: string[];
    workText: string;
    version: string;
  };
  expect(actual).toMatchObject({
    category: 'lecturas',
    tags: ['etiqueta libre QA'],
    workText: 'Obra privada sintética separada de los temas.',
    version: assignment.contentVersion,
  });
  await page.getByRole('link', { name: 'Volver al contenido', exact: true }).click();
  await page.getByRole('button', { name: 'Publicar contenido', exact: true }).click();
  await page.getByRole('button', { name: 'Confirmar publicación', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Ver ficha pública', exact: true })).toBeVisible();
  await page.goto('/explore?topic=' + first.slug + '&category=lecturas');
  await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible();
  await expect(page.getByLabel('Tema', { exact: true })).toHaveValue(first.slug);
  await accessible(page);
  const publicFilterScreenshot = testInfo.outputPath(
    'topics-public-filter-' + testInfo.project.name + '.png',
  );
  await page.screenshot({ path: publicFilterScreenshot, fullPage: true });
  fs.chmodSync(publicFilterScreenshot, 0o600);
  const publicDirectory = publicTopicPageFrom(
    await (await page.request.get('/api/v1/catalog/topics?pageSize=100')).json(),
  );
  expect(publicDirectory.items.some((x) => x.id === first.id)).toBe(true);
  const archived = await state(page, first.id, 'archive');
  expect(archived.slug).toBe(first.slug);
  const publicFiltered = await page.request.get('/api/v1/catalog/content?topic=' + first.slug);
  expect(publicFiltered.status()).toBe(200);
  expect(((await publicFiltered.json()) as { items: unknown[] }).items).toEqual([]);
  const retained = contentTopicsFrom(
    await (await page.request.get(contentPath + '/topics')).json(),
  );
  expect(retained.items.map((x) => x.id)).toContain(first.id);
  expect(retained.items.find((x) => x.id === first.id)?.status).toBe('archived');
  expect((await page.request.get('/api/v1/catalog/content/' + slug)).status()).toBe(200);
  const archivedScreenshot = testInfo.outputPath(
    'topics-archived-' + testInfo.project.name + '.png',
  );
  await page.screenshot({ path: archivedScreenshot, fullPage: true });
  fs.chmodSync(archivedScreenshot, 0o600);
  const restored = await state(page, first.id, 'restore');
  expect(restored.id).toBe(first.id);
  expect(restored.slug).toBe(first.slug);
  const restoredScreenshot = testInfo.outputPath(
    'topics-restored-' + testInfo.project.name + '.png',
  );
  await page.screenshot({ path: restoredScreenshot, fullPage: true });
  fs.chmodSync(restoredScreenshot, 0o600);
  await page.goto('/explore?topic=' + first.slug + '&category=lecturas');
  await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible();
  expect(foreign).toBe(0);
  expect(writes.some((value) => value.startsWith('DELETE'))).toBe(false);
});
