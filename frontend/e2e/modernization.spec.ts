import fs from 'node:fs';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { loginQa } from './helpers/mfa';
const password = process.env['QA_IDENTITY_PASSWORD'];
const ownerEmail = process.env['QA_OWNER_EMAIL'] ?? 'qa-load-000090@example.test';
function guard() {
  if (
    !password ||
    !process.env['QA_PROJECT']?.startsWith('acropolis_test_') ||
    !process.env['BASE_URL']?.startsWith('https://qa-')
  )
    throw new Error(
      'Modernization E2E requires the isolated HTTPS QA project and synthetic owner.',
    );
}
async function write(
  request: APIRequestContext,
  route: string,
  method: 'POST' | 'PUT' | 'PATCH',
  data: object,
) {
  const csrf = await request.get('/api/v1/identity/csrf');
  expect(csrf.status()).toBe(200);
  const token = (await csrf.json()) as { token: string };
  return request.fetch('/api/v1' + route, {
    method,
    headers: { 'X-CSRF-TOKEN': token.token },
    data,
  });
}
type Content = Record<string, unknown> & {
  id: string;
  slug: string;
  title: string;
  version: string;
};
async function createPublished(
  request: APIRequestContext,
  fields: Record<string, unknown>,
): Promise<Content> {
  const body = {
    summary: 'Sinopsis pública de la prueba aislada.',
    body: 'Descripción editorial pública.',
    coverAsset: null,
    durationSeconds: null,
    author: 'Institución QA',
    tags: ['Prueba aislada'],
    workText: null,
    youTubeId: null,
    collectionKind: null,
    itemIds: [],
    ...fields,
  };
  const created = await write(request, '/admin/content', 'POST', body);
  expect(created.status()).toBe(201);
  const draft = (await created.json()) as Content;
  const published = await write(request, '/admin/content/' + draft.id, 'PUT', {
    ...body,
    version: draft.version,
    status: 'published',
  });
  expect(published.status()).toBe(200);
  return (await published.json()) as Content;
}
async function accessible(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
}
async function captureDesign(page: Page, path: string) {
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path, fullPage: true, animations: 'disabled', caret: 'hide' });
  fs.chmodSync(path, 0o600);
}
test.use({ trace: 'off' });
test('@modernization free subscription, protected works, ordered collections, owner delegation and audit are real workflows', async ({
  page,
  browser,
}, testInfo) => {
  test.setTimeout(360_000);
  guard();
  const origin = process.env['BASE_URL']!;
  const mobile = Boolean(testInfo.project.use.isMobile);
  const suffix = testInfo.project.name + '-' + Date.now();
  const viewerEmail = 'qa-load-' + (mobile ? '000095' : '000094') + '@example.test';
  const delegateEmail = 'qa-load-' + (mobile ? '000097' : '000096') + '@example.test';
  const owner = await loginQa(page.request, ownerEmail, password!);
  expect(owner.permissions).toEqual(
    expect.arrayContaining(['Users.Manage', 'Content.Manage', 'Subscriptions.Manage']),
  );
  const viewport = page.viewportSize()!;
  const options = {
    baseURL: origin,
    ignoreHTTPSErrors: false,
    viewport,
    isMobile: mobile,
    hasTouch: testInfo.project.use.hasTouch ?? false,
    deviceScaleFactor: testInfo.project.use.deviceScaleFactor ?? 1,
    ...(testInfo.project.use.userAgent ? { userAgent: testInfo.project.use.userAgent } : {}),
  };
  const viewerContext = await browser.newContext(options);
  const adminContext = await browser.newContext(options);
  try {
    await page.goto('/admin/content');
    await page.getByRole('link', { name: 'Crear contenido', exact: true }).click();
    await expect(page.locator('footer')).toHaveCount(0);
    const readingTitle = 'Lectura completa QA ' + suffix;
    const readingSlug = 'qa-reading-' + suffix;
    const fullText =
      'Primer párrafo protegido de QA.\n\n<script>window.modernizationInjected=true</script>\n\nTercer párrafo completo.';
    await page.getByLabel('Título', { exact: true }).fill(readingTitle);
    await page.getByLabel('Dirección del contenido', { exact: true }).fill(readingSlug);
    await page.getByLabel('Autor o institución', { exact: true }).fill('Autora sintética QA');
    await page.getByLabel('Etiquetas', { exact: true }).fill('Filosofía, Cultura');
    await page.getByLabel('Resumen', { exact: true }).fill('Resumen público de lectura.');
    await page
      .getByLabel('Sinopsis ampliada', { exact: true })
      .fill('Sinopsis pública, diferente de la obra.');
    await page.getByLabel('Lectura completa', { exact: true }).fill(fullText);
    await page.getByRole('button', { name: 'Guardar borrador', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/content\/[a-f0-9-]{36}$/);
    const readingId = new URL(page.url()).pathname.split('/').at(-1)!;
    await page.getByRole('button', { name: 'Publicar contenido', exact: true }).click();
    await page.getByRole('button', { name: 'Confirmar publicación', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Contenido publicado.');
    await accessible(page);
    await captureDesign(
      page,
      testInfo.outputPath('modernization-admin-editor-' + testInfo.project.name + '.png'),
    );
    await page.getByLabel('Lectura completa', { exact: true }).fill('Edición sin guardar de QA.');
    const draftRequests: string[] = [];
    const trackEditorWrites = (request: import('@playwright/test').Request) => {
      if (request.method() === 'PUT' && request.url().endsWith('/admin/content/' + readingId))
        draftRequests.push(request.url());
    };
    page.on('request', trackEditorWrites);
    const cancelledBack = page.waitForEvent('dialog').then(async (dialog) => {
      expect(dialog.message()).toContain('cambios sin guardar');
      await dialog.dismiss();
    });
    await Promise.all([page.goBack(), cancelledBack]);
    await expect(page).toHaveURL(new RegExp('/admin/content/' + readingId + '$'));
    await expect(page.getByLabel('Lectura completa', { exact: true })).toHaveValue(
      'Edición sin guardar de QA.',
    );
    const acceptedBack = page.waitForEvent('dialog').then(async (dialog) => {
      expect(dialog.message()).toContain('cambios sin guardar');
      await dialog.accept();
    });
    await Promise.all([page.goBack(), acceptedBack]);
    await expect(page).toHaveURL(/\/admin\/content$/);
    await expect(
      page.getByRole('link', { name: 'Editar contenido ' + readingTitle, exact: true }),
    ).toBeVisible();
    if (mobile)
      await page.getByRole('button', { name: 'Menú de administración', exact: true }).click();
    const sidebarLink = page
      .locator('.admin-sidebar')
      .getByRole('link', { name: 'Contenidos', exact: true });
    await page.keyboard.press('Tab');
    await sidebarLink.focus();
    await expect(sidebarLink).toBeFocused();
    expect(await sidebarLink.evaluate((link) => link.matches(':focus-visible'))).toBe(true);
    expect(await sidebarLink.evaluate((link) => getComputedStyle(link).outlineColor)).toBe(
      'rgb(255, 255, 255)',
    );
    await captureDesign(
      page,
      testInfo.outputPath('modernization-admin-list-' + testInfo.project.name + '.png'),
    );
    await page.getByRole('link', { name: 'Editar contenido ' + readingTitle, exact: true }).click();
    await expect(page.getByLabel('Lectura completa', { exact: true })).toHaveValue(fullText);
    expect(draftRequests).toEqual([]);
    page.off('request', trackEditorWrites);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: readingTitle, level: 3 })).toBeVisible();
    await captureDesign(
      page,
      testInfo.outputPath('modernization-home-' + testInfo.project.name + '.png'),
    );
    const second = await createPublished(page.request, {
      title: 'W'.repeat(180),
      slug: 'qa-second-' + suffix,
      category: 'lecturas',
      workText: 'Segunda obra completa y protegida.',
    });
    const video = await createPublished(page.request, {
      title: 'Vídeo completo QA ' + suffix,
      slug: 'qa-video-' + suffix,
      category: 'videos',
      youTubeId: 'M7lc1UVf-VE',
    });
    const course = await createPublished(page.request, {
      title: 'Curso QA ' + suffix,
      slug: 'qa-course-' + suffix,
      category: 'cursos',
      collectionKind: 'course',
      itemIds: [readingId, second.id],
    });
    const program = await createPublished(page.request, {
      title: 'Programa QA ' + suffix,
      slug: 'qa-program-' + suffix,
      category: 'cursos',
      collectionKind: 'program',
      itemIds: [course.id],
    });
    const publicResponse = await viewerContext.request.get(
      '/api/v1/catalog/content/' + readingSlug,
    );
    expect(publicResponse.status()).toBe(200);
    expect(await publicResponse.json()).not.toHaveProperty('workText');
    expect(await publicResponse.text()).not.toContain('Primer párrafo protegido');
    expect(
      (await viewerContext.request.get('/api/v1/consumption/content/' + readingSlug)).status(),
    ).toBe(401);
    const viewer = await loginQa(viewerContext.request, viewerEmail, password!);
    expect(viewer.permissions).not.toContain('Users.Manage');
    expect(
      (await viewerContext.request.get('/api/v1/consumption/content/' + readingSlug)).status(),
    ).toBe(403);
    const viewerPage = await viewerContext.newPage();
    const activationPosts: string[] = [];
    viewerPage.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/subscriptions/'))
        activationPosts.push(new URL(request.url()).pathname);
    });
    await viewerPage.goto('/content/' + readingSlug);
    await viewerPage.getByRole('link', { name: 'Suscribirme gratis', exact: true }).click();
    await expect(
      viewerPage.getByRole('button', { name: 'Suscribirme gratis', exact: true }),
    ).toBeVisible();
    expect(activationPosts).toEqual([]);
    await expect(
      viewerPage.getByText(/cualquier contratación requerirá tu aceptación/),
    ).toBeVisible();
    const activate = viewerPage.getByRole('button', { name: 'Suscribirme gratis', exact: true });
    await activate.focus();
    await expect(activate).toBeFocused();
    await viewerPage.keyboard.press('Enter');
    await expect(viewerPage.getByRole('status')).toContainText('Tu acceso gratuito está activo.');
    expect(activationPosts).toEqual(['/api/v1/subscriptions/activate']);
    const mine = await viewerContext.request.get('/api/v1/subscriptions/me');
    const me = (await mine.json()) as {
      subscription: { id: string; version: string; expiresUtc: null; plan: string; status: string };
    };
    expect(me.subscription.expiresUtc).toBeNull();
    expect(me.subscription.plan).toBe('free_beta');
    await viewerPage.getByRole('link', { name: 'Volver al contenido', exact: true }).click();
    await expect(
      viewerPage.getByRole('article', { name: 'Lectura completa', exact: true }),
    ).toContainText('Primer párrafo protegido de QA.');
    await expect(
      viewerPage.getByText('<script>window.modernizationInjected=true</script>', { exact: true }),
    ).toBeVisible();
    expect(await viewerPage.evaluate(() => 'modernizationInjected' in window)).toBe(false);
    await accessible(viewerPage);
    await captureDesign(
      viewerPage,
      testInfo.outputPath('modernization-reading-' + testInfo.project.name + '.png'),
    );
    await viewerPage.goto('/content/' + program.slug);
    await expect(viewerPage.getByRole('status')).toContainText(
      'Tu acceso a este programa está activo.',
    );
    await viewerPage
      .getByRole('link', { name: course.title + ' · Institución QA', exact: true })
      .click();
    const children = viewerPage.locator('.collection-items li');
    await expect(children).toHaveCount(2);
    await expect(children.nth(0)).toContainText(readingTitle);
    await expect(children.nth(1)).toContainText(second.title);
    await captureDesign(
      viewerPage,
      testInfo.outputPath('modernization-long-title-course-' + testInfo.project.name + '.png'),
    );
    await children.nth(0).getByRole('link').click();
    const nextWork = viewerPage.getByRole('link', {
      name: 'Siguiente: ' + second.title,
      exact: true,
    });
    await expect(nextWork).toBeVisible();
    await captureDesign(
      viewerPage,
      testInfo.outputPath('modernization-long-title-sequence-' + testInfo.project.name + '.png'),
    );
    await nextWork.click();
    await expect(viewerPage.getByRole('article')).toContainText(
      'Segunda obra completa y protegida.',
    );
    await expect(
      viewerPage.getByRole('link', { name: 'Anterior: ' + readingTitle, exact: true }),
    ).toBeVisible();
    let youtubeRequests = 0;
    await viewerPage.route('https://www.youtube.com/iframe_api', async (route) => {
      youtubeRequests++;
      await route.fulfill({
        contentType: 'text/javascript',
        body: 'window.YT={Player:function(frame,options){window.qaPlayerError=options.events.onError;this.destroy=function(){}}};window.onYouTubeIframeAPIReady();',
      });
    });
    await viewerPage.route('https://www.youtube-nocookie.com/embed/**', async (route) => {
      youtubeRequests++;
      await route.fulfill({
        contentType: 'text/html',
        body: '<!doctype html><html lang="es"><head><title>Reproductor QA</title></head><body><button aria-label="Reproducir">Reproducir</button></body></html>',
      });
    });
    await viewerPage.goto('/content/' + video.slug);
    await expect(
      viewerPage.getByRole('button', { name: 'Reproducir vídeo', exact: true }),
    ).toBeVisible();
    expect(youtubeRequests).toBe(0);
    await expect(viewerPage.locator('iframe')).toHaveCount(0);
    await viewerPage.getByRole('button', { name: 'Reproducir vídeo', exact: true }).click();
    const iframe = viewerPage.locator('iframe.youtube-player');
    await expect(iframe).toHaveAttribute('referrerpolicy', 'origin');
    await expect(iframe).toHaveAttribute('src', /youtube-nocookie\.com\/embed\/M7lc1UVf-VE/);
    await expect(iframe).toHaveAttribute('src', /autoplay=0/);
    await expect.poll(() => youtubeRequests).toBe(2);
    const box = await iframe.boundingBox();
    expect(box!.width).toBeGreaterThanOrEqual(200);
    expect(box!.height).toBeGreaterThanOrEqual(200);
    await viewerPage.evaluate(() => {
      const qa = window as Window & { qaPlayerError?: (event: { data: number }) => void };
      qa.qaPlayerError?.({ data: 101 });
    });
    await expect(viewerPage.getByRole('alert')).toContainText('Este vídeo no está disponible');
    await viewerPage.goto('/profile/subscription');
    await viewerPage.getByRole('button', { name: 'Cancelar suscripción', exact: true }).click();
    await viewerPage.getByRole('button', { name: 'Confirmar cancelación', exact: true }).click();
    await expect(viewerPage.getByRole('status')).toContainText(
      'Cancelaste tu suscripción gratuita.',
    );
    expect(
      (await viewerContext.request.get('/api/v1/consumption/content/' + readingSlug)).status(),
    ).toBe(403);
    await viewerPage.getByRole('button', { name: 'Suscribirme gratis', exact: true }).click();
    await expect(viewerPage.getByRole('status')).toContainText('Tu acceso gratuito está activo.');
    const subscriptionDetail = await page.request.get(
      '/api/v1/admin/subscriptions/' + me.subscription.id,
    );
    const currentSubscription = (await subscriptionDetail.json()) as { version: string };
    expect(
      (
        await write(page.request, '/admin/subscriptions/' + me.subscription.id, 'PATCH', {
          version: currentSubscription.version,
          status: 'suspended',
          reason: 'Revisión QA aislada',
        })
      ).status(),
    ).toBe(200);
    await viewerPage.reload();
    await expect(viewerPage.getByRole('alert')).toContainText('La suscripción está suspendida.');
    await expect(
      viewerPage.getByRole('button', { name: 'Suscribirme gratis', exact: true }),
    ).toHaveCount(0);
    expect(
      (await write(viewerContext.request, '/subscriptions/activate', 'POST', {})).status(),
    ).toBe(403);
    await page.goto('/admin/subscriptions');
    await page.getByRole('button', { name: 'Buscar una cuenta', exact: true }).click();
    await page.getByLabel('Nombre o correo de la cuenta', { exact: true }).fill(viewerEmail);
    await page.getByRole('button', { name: 'Buscar cuentas', exact: true }).click();
    await page
      .getByRole('button', {
        name: new RegExp('Ver suscripciones de .*' + viewerEmail.replaceAll('.', '\\.')),
      })
      .click();
    await expect(page.locator('main tbody')).toContainText(viewerEmail);
    await page.getByLabel('Estado', { exact: true }).selectOption('suspended');
    await page.getByRole('button', { name: 'Buscar', exact: true }).click();
    await expect(page).toHaveURL(/status=suspended/);
    await page.goBack();
    await expect(page).not.toHaveURL(/status=suspended/);
    await expect(page.getByLabel('Estado', { exact: true })).toHaveValue('');
    await page.getByRole('link', { name: 'Gestionar suscripción', exact: true }).click();
    await expect(page.locator('main')).toContainText(viewerEmail);
    const subscriptionUrl = page.url();
    const subscriptionWrites: string[] = [];
    const trackSubscriptionWrites = (request: import('@playwright/test').Request) => {
      if (
        request.method() === 'PATCH' &&
        request.url().endsWith('/admin/subscriptions/' + me.subscription.id)
      )
        subscriptionWrites.push(request.url());
    };
    page.on('request', trackSubscriptionWrites);
    await page.getByLabel('Estado de la suscripción', { exact: true }).selectOption('active');
    await page.getByLabel('Motivo del cambio', { exact: true }).fill('Acceso revisado en QA');
    const cancelSubscriptionBack = page.waitForEvent('dialog').then(async (dialog) => {
      expect(dialog.message()).toContain('cambios sin guardar');
      await dialog.dismiss();
    });
    await Promise.all([page.goBack(), cancelSubscriptionBack]);
    await expect(page).toHaveURL(subscriptionUrl);
    await expect(page.getByLabel('Estado de la suscripción', { exact: true })).toHaveValue(
      'active',
    );
    await expect(page.getByLabel('Motivo del cambio', { exact: true })).toHaveValue(
      'Acceso revisado en QA',
    );
    expect(subscriptionWrites).toEqual([]);
    const acceptSubscriptionBack = page.waitForEvent('dialog').then(async (dialog) => {
      expect(dialog.message()).toContain('cambios sin guardar');
      await dialog.accept();
    });
    await Promise.all([page.goBack(), acceptSubscriptionBack]);
    await expect(page).toHaveURL(/\/admin\/subscriptions\?/);
    expect(subscriptionWrites).toEqual([]);
    page.off('request', trackSubscriptionWrites);
    await page.getByRole('link', { name: 'Gestionar suscripción', exact: true }).click();
    await page.getByLabel('Estado de la suscripción', { exact: true }).selectOption('active');
    await page.getByLabel('Motivo del cambio', { exact: true }).fill('Acceso revisado en QA');
    await page.getByRole('button', { name: 'Guardar estado', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Guardamos el estado de la suscripción.');
    await page
      .getByRole('link', { name: 'Ver historial de esta suscripción', exact: true })
      .click();
    await expect(page.getByRole('table')).toContainText('Suscripción actualizada');
    await accessible(page);
    await captureDesign(
      page,
      testInfo.outputPath('modernization-admin-audit-' + testInfo.project.name + '.png'),
    );
    await page.goto('/admin/users/' + owner.id);
    await expect(page.getByText('Propietario protegido', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Guardar usuario', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Guardar permisos', exact: true })).toHaveCount(
      0,
    );
    const ownerCurrent = await page.request.get('/api/v1/admin/users/' + owner.id);
    const ownerView = (await ownerCurrent.json()) as { version: string };
    expect(
      (
        await write(page.request, '/admin/users/' + owner.id, 'PATCH', {
          version: ownerView.version,
          status: 'disabled',
        })
      ).status(),
    ).toBe(409);
    await loginQa(adminContext.request, 'qa-load-000000@example.test', password!);
    const targets = await page.request.get(
      '/api/v1/admin/users?search=' + encodeURIComponent(delegateEmail) + '&page=1&pageSize=20',
    );
    const target = ((await targets.json()) as { items: { id: string; version: string }[] })
      .items[0]!;
    expect(
      (
        await write(adminContext.request, '/admin/users/' + target.id + '/permissions', 'PUT', {
          version: target.version,
          permissions: ['Subscriptions.Manage'],
        })
      ).status(),
    ).toBe(403);
    expect((await adminContext.request.get('/api/v1/admin/subscriptions/audit')).status()).toBe(
      403,
    );
    expect((await adminContext.request.get('/api/v1/admin/content/audit')).status()).toBe(403);
    await page.goto('/admin/users/' + target.id);
    await page.getByLabel('Gestionar suscripciones', { exact: true }).check();
    await page.getByRole('button', { name: 'Guardar permisos', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Guardamos los permisos.');
    await page.getByRole('link', { name: 'Historial de la cuenta', exact: true }).click();
    await expect(page.getByRole('table')).toContainText('Permisos actualizados');
    await page.getByLabel('Acción', { exact: true }).selectOption('permissions.updated');
    await page.getByRole('button', { name: 'Filtrar', exact: true }).click();
    await expect(page).toHaveURL(/action=permissions.updated/);
    await expect(page.getByRole('table')).toContainText(target.id);
    await page.goto('/admin/audit?module=content&contentId=' + readingId);
    await expect(page.getByRole('table')).toContainText('Contenido publicado');
    await expect(page.getByRole('table')).not.toContainText(fullText);
    await accessible(page);
    fs.writeFileSync(
      '/artifacts/modernization-consumption.json',
      JSON.stringify({ slug: readingSlug, marker: 'Primer párrafo protegido de QA.' }),
      { mode: 0o600 },
    );
    fs.chmodSync('/artifacts/modernization-consumption.json', 0o600);
  } finally {
    await viewerContext.close();
    await adminContext.close();
  }
});
test('@modernization @youtube-smoke official privacy player can load after deliberate play in isolated QA', async ({
  page,
}, testInfo) => {
  test.setTimeout(120_000);
  guard();
  test.skip(
    process.env['QA_YOUTUBE_SMOKE'] !== '1',
    'Run the real external provider smoke separately from deterministic QA.',
  );
  await loginQa(page.request, ownerEmail, password!);
  const video = await createPublished(page.request, {
    title: 'YouTube official isolated smoke',
    slug: 'qa-youtube-smoke-' + testInfo.project.name + '-' + Date.now(),
    category: 'videos',
    youTubeId: 'M7lc1UVf-VE',
  });
  expect((await write(page.request, '/subscriptions/activate', 'POST', {})).status()).toBe(200);
  await page.goto('/content/' + video.slug);
  await page.getByRole('button', { name: 'Reproducir vídeo', exact: true }).click();
  await expect(page.locator('iframe.youtube-player')).toHaveAttribute('referrerpolicy', 'origin');
  await expect
    .poll(async () =>
      page
        .locator('iframe.youtube-player')
        .evaluate((frame) => Boolean((frame as HTMLIFrameElement).contentWindow)),
    )
    .toBe(true);
  const player = page.frameLocator('iframe.youtube-player');
  await expect(player.locator('.html5-video-player')).toBeVisible({ timeout: 30_000 });
  await page.locator('iframe.youtube-player').scrollIntoViewIfNeeded();
  await player.getByRole('button', { name: 'Play video', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Reproduciendo', { timeout: 30_000 });
  await expect
    .poll(
      async () =>
        player.locator('video').evaluate((video) => (video as HTMLVideoElement).currentTime),
      { timeout: 30_000 },
    )
    .toBeGreaterThan(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});
