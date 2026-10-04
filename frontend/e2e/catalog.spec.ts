import AxeBuilder from '@axe-core/playwright';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { loginQa } from './helpers/mfa';

const base = '/api/v1/admin/content';
const password = process.env['QA_IDENTITY_PASSWORD'];
const editor = 'qa-load-000080@example.test';
test.use({ trace: 'off' });
async function write(
  request: APIRequestContext,
  route: string,
  method: 'POST' | 'PUT',
  body: Record<string, unknown>,
) {
  const csrf = await request.get('/api/v1/identity/csrf');
  expect(csrf.status()).toBe(200);
  const token = (await csrf.json()) as { token: string };
  return request.fetch(route, { method, data: body, headers: { 'X-CSRF-TOKEN': token.token } });
}
function editable(item: Record<string, unknown>) {
  return Object.fromEntries(
    [
      'version',
      'status',
      'slug',
      'title',
      'summary',
      'body',
      'category',
      'coverAsset',
      'durationSeconds',
    ].map((key) => [key, item[key]]),
  );
}

test('@catalog editorial lifecycle requires its own permission, preserves conflicts and immediately withdraws public content', async ({
  page,
  request,
  browser,
}, testInfo) => {
  test.setTimeout(150_000);
  if (!password) throw new Error('Isolated QA credentials are required.');
  expect((await request.get(base)).status()).toBe(401);
  const slug = 'qa-editor-' + testInfo.project.name + '-' + Date.now();
  const title = 'Una idea editorial QA ' + testInfo.project.name;
  const origin = process.env['BASE_URL'];
  if (!origin) throw new Error('Isolated QA origin is required.');
  const viewport = page.viewportSize();
  if (!viewport) throw new Error('Catalog layout requires a fixed QA viewport.');
  const publicContext = await browser.newContext({
    baseURL: origin,
    ignoreHTTPSErrors: false,
    viewport,
    isMobile: testInfo.project.use.isMobile ?? false,
    hasTouch: testInfo.project.use.hasTouch ?? false,
    deviceScaleFactor: testInfo.project.use.deviceScaleFactor ?? 1,
    ...(testInfo.project.use.userAgent ? { userAgent: testInfo.project.use.userAgent } : {}),
  });
  try {
    const user = await loginQa(page.request, editor, password);
    expect(user.permissions).toContain('Content.Manage');
    expect(user.permissions).not.toContain('Users.Manage');
    expect((await page.request.get('/api/v1/admin/users')).status()).toBe(403);
    await page.goto('/admin/content');
    await page.getByRole('link', { name: 'Crear contenido', exact: true }).click();
    await page.getByLabel('Título', { exact: true }).fill(title);
    await page.getByLabel('Dirección del contenido', { exact: true }).fill(slug);
    await page.getByLabel('Categoría del contenido', { exact: true }).selectOption('podcast');
    await page.getByLabel('Imagen editorial', { exact: true }).selectOption('editorial-podcast');
    await page
      .getByLabel('Resumen', { exact: true })
      .fill('Una sinopsis pública para revisar el catálogo real.');
    const synopsis = 'Descripción editorial QA. <script>window.catalogInjected = true</script>';
    await page.getByLabel('Sinopsis ampliada', { exact: true }).fill(synopsis);
    await page.getByLabel('Duración (segundos)', { exact: true }).fill('1200');
    await page.getByRole('button', { name: 'Guardar borrador', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/content\/[a-f0-9-]{36}$/);
    const id = new URL(page.url()).pathname.split('/').at(-1)!;
    expect((await publicContext.request.get('/api/v1/catalog/content/' + slug)).status()).toBe(404);
    const draft = (await (await page.request.get(base + '/' + id)).json()) as Record<
      string,
      unknown
    >;
    expect(draft['status']).toBe('draft');
    expect((await page.request.put(base + '/' + id, { data: editable(draft) })).status()).toBe(400);
    await page.getByRole('button', { name: 'Publicar contenido', exact: true }).click();
    await page.getByRole('button', { name: 'Confirmar publicación', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Contenido publicado.');
    await expect(page.getByLabel('Dirección del contenido', { exact: true })).toHaveAttribute(
      'readonly',
      '',
    );
    const published = await publicContext.request.get('/api/v1/catalog/content/' + slug);
    expect(published.status()).toBe(200);
    expect(published.headers()['cache-control']).toContain('no-store');
    expect((await published.json()) as Record<string, unknown>).not.toHaveProperty('version');
    const publicPage = await publicContext.newPage();
    await publicPage.goto('/content/' + slug);
    await expect(publicPage.getByRole('heading', { level: 1 })).toHaveText(title);
    await expect(publicPage.getByText(synopsis, { exact: true })).toBeVisible();
    expect(publicPage.viewportSize()).toEqual(viewport);
    const cover = publicPage.locator('.catalog-detail-cover');
    await expect(cover).toBeVisible();
    await expect
      .poll(() => cover.evaluate((image) => (image as HTMLImageElement).naturalWidth))
      .toBeGreaterThan(0);
    await expect
      .poll(async () => {
        const box = await cover.boundingBox();
        return box ? box.width / box.height : 0;
      })
      .toBeCloseTo(viewport.width <= 760 ? 16 / 10 : 16 / 7.8, 2);
    expect(
      await publicPage.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    expect(await publicPage.evaluate(() => 'catalogInjected' in window)).toBe(false);
    await expect(publicPage.locator('video, audio, iframe')).toHaveCount(0);
    expect((await new AxeBuilder({ page: publicPage }).analyze()).violations).toEqual([]);
    await publicPage.screenshot({
      path: testInfo.outputPath('catalog-detail-' + testInfo.project.name + '.png'),
      fullPage: true,
    });
    const current = (await (await page.request.get(base + '/' + id)).json()) as Record<
      string,
      unknown
    >;
    expect(
      (
        await write(page.request, base + '/' + id, 'PUT', {
          ...editable(current),
          title: 'Edición concurrente QA',
        })
      ).status(),
    ).toBe(200);
    await page.getByLabel('Título', { exact: true }).fill('Mis cambios sin guardar');
    await page.getByRole('button', { name: 'Guardar cambios', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Otra persona');
    await expect(page.getByLabel('Título', { exact: true })).toHaveValue('Mis cambios sin guardar');
    await page.getByRole('button', { name: 'Recargar datos', exact: true }).click();
    await page.getByRole('button', { name: 'Conservar mis cambios', exact: true }).click();
    await expect(page.getByLabel('Título', { exact: true })).toHaveValue('Mis cambios sin guardar');
    await page.getByRole('button', { name: 'Recargar datos', exact: true }).click();
    await page.getByRole('button', { name: 'Recargar y sustituir cambios', exact: true }).click();
    await expect(page.getByLabel('Título', { exact: true })).toHaveValue('Edición concurrente QA');
    await page.getByRole('button', { name: 'Archivar contenido', exact: true }).click();
    await page.getByRole('button', { name: 'Confirmar archivo', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Contenido archivado.');
    expect((await publicContext.request.get('/api/v1/catalog/content/' + slug)).status()).toBe(404);
    const filtered = await publicContext.request.get(
      '/api/v1/catalog/content?search=' + encodeURIComponent('Edición concurrente QA'),
    );
    expect(
      ((await filtered.json()) as { items: { slug: string }[] }).items.some(
        (item) => item.slug === slug,
      ),
    ).toBe(false);
    await expect(page.getByRole('button', { name: 'Publicar contenido', exact: true })).toHaveCount(
      0,
    );
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  } finally {
    await publicContext.close();
  }
});

test('@catalog real public search, category, pagination and browser history agree', async ({
  page,
}, testInfo) => {
  test.setTimeout(150_000);
  if (!password) throw new Error('Isolated QA credentials are required.');
  await loginQa(page.request, editor, password);
  const prefix = 'qa-pages-' + testInfo.project.name + '-' + Date.now();
  for (let index = 0; index < 21; index += 1) {
    const body = {
      slug: prefix + '-' + index,
      title: prefix + ' lectura ' + index,
      summary: 'Resumen QA de paginación.',
      body: 'Sinopsis editorial QA sin material restringido.',
      category: 'lecturas',
      coverAsset: 'editorial-reading',
      durationSeconds: null,
    };
    const created = await write(page.request, base, 'POST', body);
    expect(created.status()).toBe(201);
    const saved = (await created.json()) as { id: string; version: string };
    expect(
      (
        await write(page.request, base + '/' + saved.id, 'PUT', {
          ...body,
          version: saved.version,
          status: 'published',
        })
      ).status(),
    ).toBe(200);
  }
  await page.goto('/explore');
  await page.getByLabel('Buscar contenido', { exact: true }).fill(prefix);
  await page.getByLabel('Categoría', { exact: true }).selectOption('lecturas');
  await page.getByRole('button', { name: 'Buscar', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('21 contenidos');
  await expect(page.locator('.catalog-card')).toHaveCount(20);
  await page.getByRole('button', { name: 'Siguiente', exact: true }).click();
  await expect(page).toHaveURL(/page=2/);
  await expect(page.locator('.catalog-card')).toHaveCount(1);
  await page.goBack();
  await expect(page.locator('.catalog-card')).toHaveCount(20);
  await expect(page.getByLabel('Buscar contenido', { exact: true })).toHaveValue(prefix);
  await page.goForward();
  await expect(page.locator('.catalog-card')).toHaveCount(1);
  await page.getByLabel('Categoría', { exact: true }).selectOption('podcast');
  await page.getByRole('button', { name: 'Buscar', exact: true }).click();
  await expect(page).not.toHaveURL(/page=2/);
  await expect(page.getByRole('status')).toContainText('0 contenidos');
  await expect(
    page.getByRole('heading', { name: 'No encontramos coincidencias.', exact: true }),
  ).toBeVisible();
});
