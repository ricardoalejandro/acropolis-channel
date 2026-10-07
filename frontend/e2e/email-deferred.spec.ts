import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test('@email-deferred catalogue remains usable and email forms clearly stay unavailable', async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(60_000);
  const capability = await request.get('/api/v1/identity/capabilities');
  expect(capability.status()).toBe(200);
  expect(await capability.json()).toEqual({ emailEnabled: false });
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Tu mediateca cultural.');
  for (const label of [
    'Lecturas',
    'Documentales',
    'Videos',
    'Podcast',
    'Charlas online',
    'Cursos',
  ]) {
    await expect(page.getByRole('link', { name: label, exact: true })).toBeVisible();
  }
  await page.getByRole('link', { name: 'Lecturas', exact: true }).click();
  await expect(page).toHaveURL(/explore\?category=lecturas/);
  await expect(page.getByRole('status')).toContainText('0 contenidos');
  for (const route of ['/register', '/forgot-password', '/email-pending']) {
    await page.goto(route);
    await expect(page.getByRole('status')).toContainText(
      'El registro y la recuperación por correo no están disponibles temporalmente.',
    );
    await expect(page.getByLabel('Correo electrónico', { exact: true })).toHaveCount(0);
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
  }
  await page.screenshot({
    path: testInfo.outputPath('email-deferred-' + testInfo.project.name + '.png'),
    fullPage: true,
  });
  await page.goto('/login');
  await expect(page.getByLabel('Correo electrónico', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ingresar', exact: true })).toBeEnabled();
});
