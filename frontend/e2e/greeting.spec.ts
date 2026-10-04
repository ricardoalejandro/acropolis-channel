import { expect, test } from '@playwright/test';

test('desktop and mobile render the greeting obtained from the real API', async ({
  page,
  request,
}) => {
  const unknownApi = await request.get('/api/v1/not-found');
  expect(unknownApi.status()).toBe(404);
  expect(unknownApi.headers()['content-type']).not.toContain('text/html');
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(message.text());
  });
  const greetingResponse = page.waitForResponse((response) =>
    response.url().endsWith('/api/v1/greeting'),
  );
  await page.goto('/');
  const response = await greetingResponse;
  expect(response.status()).toBe(200);
  expect(await response.json()).toEqual({ message: 'Hola mundo' });
  await expect(page.getByRole('heading', { name: /Acrópolis.*Channel/ })).toBeVisible();
  await expect(page.getByRole('status')).toContainText('Hola mundo');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect(errors).toEqual([]);
});

test('an invalid response exposes a safe error and retry reaches the real API', async ({
  page,
}) => {
  let requests = 0;
  await page.route('**/api/v1/greeting', async (route) => {
    requests += 1;
    if (requests === 1)
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ message: { private: 'upstream-secret' } }),
      });
    else await route.continue();
  });
  await page.goto('/');
  await expect(page.getByRole('alert')).toContainText('No pudimos cargar el saludo');
  await expect(page.getByText('upstream-secret')).toHaveCount(0);
  await page.getByRole('button', { name: 'Reintentar' }).click();
  await expect(page.getByRole('status')).toContainText('Hola mundo');
  expect(requests).toBe(2);
});

test('an API 404 never becomes a client-side fallback greeting', async ({ page }) => {
  await page.route('**/api/v1/greeting', (route) =>
    route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ title: 'Not Found' }),
    }),
  );
  await page.goto('/');
  await expect(page.getByRole('alert')).toContainText('No pudimos cargar el saludo');
  await expect(page.getByText('Hola mundo')).toHaveCount(0);
});
