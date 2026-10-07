import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test('@preview isolated catalogue is accessible, responsive and explicit about unavailable media', async ({
  page,
}, testInfo) => {
  test.setTimeout(90_000);
  const origin = process.env['PREVIEW_URL'];
  if (!origin || !process.env['QA_PROJECT']?.startsWith('acropolis_test_')) {
    throw new Error('Preview E2E requires its separate QA service.');
  }
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const routes = [
    { name: 'home', hash: '#/' },
    { name: 'explore', hash: '#/explore' },
    { name: 'video', hash: '#/video/filosofia-cotidiana' },
    { name: 'podcast', hash: '#/podcast/arte-de-escuchar' },
  ];
  for (const route of routes) {
    await page.goto(origin + '/preview.html' + route.hash);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.getByText(/Vista previa de diseño/, { exact: false }).first()).toBeVisible();
    await expect(
      page.getByText(/Contenido de demostración/, { exact: false }).first(),
    ).toBeVisible();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await page.evaluate(() => document.fonts.ready);
    const accessibility = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    expect(accessibility.violations).toEqual([]);
    await page.locator('img').evaluateAll(async (images) => {
      await Promise.all(
        images.map((element) => {
          const image = element as HTMLImageElement;
          image.loading = 'eager';
          return image.decode();
        }),
      );
    });
    const screenshot = testInfo.outputPath(
      'preview-' + testInfo.project.name + '-' + route.name + '.png',
    );
    await page.screenshot({ path: screenshot, fullPage: true });
    await testInfo.attach('preview-' + route.name, { path: screenshot, contentType: 'image/png' });
    if (route.name === 'video' || route.name === 'podcast') {
      await expect(
        page.getByRole('button', { name: /Reproducción.*no disponible/ }),
      ).toBeDisabled();
      await page.getByRole('tab', { name: 'Sobre este contenido', exact: true }).focus();
      await page.keyboard.press('ArrowRight');
      await expect(page.getByRole('tab', { name: 'Transcripción', exact: true })).toBeFocused();
      await expect(page.getByRole('tab', { name: 'Transcripción', exact: true })).toHaveAttribute(
        'aria-selected',
        'true',
      );
    }
  }
  // A hash transition keeps the previous document and focus; verify a fresh keyboard entry.
  await page.goto('about:blank');
  await page.goto(origin + '/preview.html#/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Saltar al contenido', exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('main')).toBeFocused();
  await expect(page.getByRole('button', { name: /Ingresar.*aplicación principal/ })).toBeDisabled();
  await page.getByRole('link', { name: 'Explorar contenidos', exact: true }).click();
  await expect(page).toHaveURL(/#\/explore$/);
  await page.getByLabel('Buscar contenido', { exact: true }).fill('filosofía');
  await expect(page.getByRole('status')).toContainText('contenidos de demostración');
  await page.goto(origin + '/preview.html#/memberships');
  await page.getByRole('button', { name: 'Ver referencia', exact: true }).first().click();
  await expect(page.getByRole('status')).toContainText(/referencia|demostración|prototipo/);
  expect(errors).toEqual([]);
});
