import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { freshQaCode, invalidQaCode, mfaWrite, saveQaAuthenticator } from './helpers/mfa';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test('authenticator enrollment, second factor, recovery rotation and revocation @mfa', async ({
  page,
  context,
}, testInfo) => {
  test.setTimeout(180_000);
  const password = process.env['QA_IDENTITY_PASSWORD'];
  if (!password || !process.env['QA_PROJECT']?.startsWith('acropolis_test_'))
    throw new Error('MFA requires guarded QA credentials.');
  const email = `qa-load-${testInfo.project.name === 'desktop-chromium' ? '000120' : '000121'}@example.test`;
  const loginForm = async () => {
    await page.goto('/login');
    await page.getByLabel('Correo electrónico', { exact: true }).fill(email);
    await page.getByLabel('Contraseña', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
  };
  const configure = async () => {
    await page.goto('/profile/security');
    await page.getByLabel('Contraseña actual', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Configurar autenticador', exact: true }).click();
    const key = (
      await page.getByLabel('Clave del autenticador', { exact: true }).inputValue()
    ).replace(/\s/g, '');
    if (!/^[A-Z2-7]{32}$/.test(key))
      throw new Error('QA did not receive a standard authenticator enrollment key.');
    saveQaAuthenticator(email, key);
    await page.getByLabel('Código del autenticador', { exact: true }).fill(freshQaCode(email));
    await page
      .getByRole('button', { name: 'Activar verificación en dos pasos', exact: true })
      .click();
    await expect(
      page.getByRole('heading', { name: 'Códigos de recuperación', exact: true }),
    ).toBeVisible();
    const codes = (await page.locator('code').allTextContents())
      .filter((value) => /^[A-Za-z0-9_-]{22}$/.test(value.trim()))
      .map((value) => value.trim());
    expect(codes.length).toBe(10);
    await page.getByRole('button', { name: 'He guardado mis códigos', exact: true }).click();
    return codes;
  };
  await loginForm();
  await expect(page).toHaveURL(/\/profile$/);
  const codes = await configure();
  expect((await page.request.get('/api/v1/identity/mfa')).status()).toBe(200);
  if (testInfo.project.name === 'mobile-chromium')
    await page.getByRole('button', { name: 'Menú', exact: true }).click();
  await page.getByRole('button', { name: 'Cerrar sesión', exact: true }).click();
  await loginForm();
  await expect(
    page.getByRole('heading', { name: 'Confirma que eres tú.', exact: true }),
  ).toBeVisible();
  expect((await page.request.get('/api/v1/identity/me')).status()).toBe(401);
  await page.getByLabel('Código del autenticador', { exact: true }).fill(invalidQaCode(email));
  await page.getByRole('button', { name: 'Verificar e ingresar', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  expect((await page.request.get('/api/v1/identity/me')).status()).toBe(401);
  await page.getByLabel('Código del autenticador', { exact: true }).fill(freshQaCode(email));
  await page.getByRole('button', { name: 'Verificar e ingresar', exact: true }).click();
  await expect(page).toHaveURL(/\/profile$/);
  const revoked = process.env['QA_SESSION_STATE_DIR'];
  if (!revoked) throw new Error('Private MFA state directory missing.');
  if (testInfo.project.name === 'desktop-chromium') {
    fs.mkdirSync(revoked, { recursive: true, mode: 0o700 });
    await context.storageState({ path: path.join(revoked, 'mfa-revoked.json') });
    fs.chmodSync(path.join(revoked, 'mfa-revoked.json'), 0o600);
  }
  await page.goto('/profile/security');
  await page.getByRole('button', { name: 'Renovar códigos de recuperación', exact: true }).click();
  await page.getByRole('button', { name: 'Usar un código de recuperación', exact: true }).click();
  await page.getByLabel('Contraseña actual', { exact: true }).fill(password);
  await page.getByLabel('Código de recuperación', { exact: true }).fill(codes[0]!);
  await page.getByRole('button', { name: 'Confirmar renovación', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Códigos de recuperación', exact: true }),
  ).toBeVisible();
  const newCodes = (await page.locator('code').allTextContents())
    .filter((value) => /^[A-Za-z0-9_-]{22}$/.test(value.trim()))
    .map((value) => value.trim());
  expect(newCodes.length).toBe(10);
  expect((await page.request.get('/api/v1/identity/me')).status()).toBe(401);
  await page.getByRole('button', { name: 'He guardado mis códigos', exact: true }).click();
  await loginForm();
  await page.getByRole('button', { name: 'Usar un código de recuperación', exact: true }).click();
  await page.getByLabel('Código de recuperación', { exact: true }).fill(codes[1]!);
  await page.getByRole('button', { name: 'Verificar e ingresar', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await page.getByLabel('Código de recuperación', { exact: true }).fill(newCodes[0]!);
  await page.getByRole('button', { name: 'Verificar e ingresar', exact: true }).click();
  await expect(page).toHaveURL(/\/profile$/);
  await page.goto('/profile/security');
  await page
    .getByRole('button', { name: 'Desactivar verificación en dos pasos', exact: true })
    .click();
  await page.getByRole('button', { name: 'Usar un código de recuperación', exact: true }).click();
  await page.getByLabel('Contraseña actual', { exact: true }).fill(password);
  await page.getByLabel('Código de recuperación', { exact: true }).fill(newCodes[1]!);
  await page.getByRole('button', { name: 'Confirmar desactivación', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  await loginForm();
  await expect(page).toHaveURL(/\/profile$/);
  const disabled = await page.request.get('/api/v1/identity/mfa');
  expect(((await disabled.json()) as { enabled: boolean }).enabled).toBe(false);
  await configure();
  if (testInfo.project.name === 'desktop-chromium') {
    await context.storageState({ path: path.join(revoked, 'mfa-active.json') });
    fs.chmodSync(path.join(revoked, 'mfa-active.json'), 0o600);
    fs.writeFileSync(path.join(revoked, 'mfa-account.json'), JSON.stringify({ email }), {
      mode: 0o600,
    });
  }
  const foreign = await mfaWrite(page.request, 'mfa/challenge', {
    challengeToken: 'invalid',
    code: '000000',
  });
  expect(foreign.status()).toBe(400);
});
