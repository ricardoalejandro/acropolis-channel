import { expect, test } from '@playwright/test';

test('desktop and mobile render the production home with the real API boundary', async ({
  page,
  request,
}) => {
  const unknownApi = await request.get('/api/v1/not-found');
  expect(unknownApi.status()).toBe(404);
  expect(unknownApi.headers()['content-type']).not.toContain('text/html');
  const greeting = await request.get('/api/v1/greeting');
  expect(greeting.status()).toBe(200);
  expect(await greeting.json()).toEqual({ message: 'Hola mundo' });
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().includes('status of 401')) {
      errors.push(message.text());
    }
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Tu mediateca cultural.');
  await expect(page.getByRole('link', { name: 'Crear mi cuenta', exact: true })).toBeVisible();
  expect((await request.get('/api/v1/identity/me')).status()).toBe(401);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect(errors).toEqual([]);
});

test('an invalid response exposes a safe error and retry reaches the real API', async ({
  page,
}) => {
  let requests = 0;
  await page.route('**/api/v1/identity/csrf', async (route) => {
    requests += 1;
    if (requests === 1) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ token: { private: 'upstream-secret' } }),
      });
    } else await route.continue();
  });
  await page.goto('/login');
  await page.getByLabel('Correo electrónico', { exact: true }).fill('unknown@acropolis.test');
  await page.getByLabel('Contraseña', { exact: true }).fill('Invalid-QA-password1!');
  await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('No pudimos leer la respuesta.');
  await expect(page.getByText('upstream-secret')).toHaveCount(0);
  const login = page.waitForResponse((response) => response.url().endsWith('/identity/login'));
  await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
  expect((await login).status()).toBe(401);
  await expect(page.getByRole('alert')).toContainText('No pudimos ingresar con esos datos.');
  expect(requests).toBe(2);
  await expect(page).toHaveURL(/\/login/);
});

test('an API 404 never becomes a client-side authenticated fallback', async ({ page }) => {
  await page.route('**/api/v1/identity/login', (route) =>
    route.fulfill({ status: 404, contentType: 'text/plain', body: 'Not Found' }),
  );
  await page.goto('/login');
  await page.getByLabel('Correo electrónico', { exact: true }).fill('unknown@acropolis.test');
  await page.getByLabel('Contraseña', { exact: true }).fill('Invalid-QA-password1!');
  await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('No pudimos conectar.');
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('link', { name: 'Mi perfil', exact: true })).toHaveCount(0);
});
