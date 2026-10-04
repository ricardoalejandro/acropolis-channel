import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';

const api = '/api/v1/identity';
const password = process.env['QA_IDENTITY_PASSWORD'];
const mailpit = process.env['MAILPIT_URL'];
const run = process.env['QA_PROJECT'];
const antiEnumeration = { message: 'Si corresponde, recibirás un correo con las instrucciones.' };

async function csrf(request: APIRequestContext): Promise<string> {
  const response = await request.get(`${api}/csrf`);
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { token: string };
  expect(typeof body.token).toBe('string');
  expect(body.token.length).toBeGreaterThan(20);
  return body.token;
}

async function write(
  request: APIRequestContext,
  route: string,
  data?: Record<string, unknown>,
  method: 'POST' | 'PATCH' = 'POST',
) {
  return request.fetch(`${api}/${route}`, {
    method,
    headers: { 'X-CSRF-TOKEN': await csrf(request) },
    data,
  });
}

async function mailLink(request: APIRequestContext, email: string, route: string): Promise<string> {
  if (!mailpit) throw new Error('Mailpit QA URL is required.');
  let link = '';
  await expect(async () => {
    const messages = await request.get(`${mailpit}/api/v1/messages?limit=200`);
    expect(messages.status()).toBe(200);
    const body = (await messages.json()) as {
      messages: { ID: string; To: { Address: string }[] }[];
    };
    for (const message of body.messages) {
      if (!message.To.some((recipient) => recipient.Address === email)) continue;
      const detail = await request.get(`${mailpit}/api/v1/message/${message.ID}`);
      const content = (await detail.json()) as { Text: string; HTML: string };
      const urls =
        `${content.Text} ${content.HTML}`.replaceAll('&amp;', '&').match(/https:\/\/[^\s"'<>]+/g) ??
        [];
      link = urls.find((url) => new URL(url).pathname === route) ?? '';
      if (link) break;
    }
    expect(Boolean(link)).toBe(true);
  }).toPass({ timeout: 30_000 });
  const expected = new URL(process.env['BASE_URL'] ?? '');
  const actual = new URL(link);
  expect(actual.origin).toBe(expected.origin);
  expect(actual.hash.includes('token=')).toBe(true);
  return link;
}

test.use({ trace: 'off' });

test.describe('Identity on the real Production candidate', () => {
  test('registration, confirmation, secure login, own profile, reset and logout', async ({
    page,
    context,
  }, testInfo) => {
    test.setTimeout(90_000);
    if (!password || !run?.startsWith('acropolis_test_')) {
      throw new Error('Identity E2E requires isolated QA credentials and run.');
    }
    const email = `e2e-${testInfo.project.name}-${Date.now()}@acropolis.test`;
    await page.goto('/register');
    await page.getByLabel('Nombre visible', { exact: true }).fill('Persona QA');
    await page.getByLabel('Correo electrónico', { exact: true }).fill(email);
    await page.getByLabel('Contraseña', { exact: true }).fill(password);
    await page.getByLabel('Confirmar contraseña', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Crear cuenta', exact: true }).click();
    await expect(page).toHaveURL(/\/email-pending/);
    const confirmation = await mailLink(page.request, email, '/confirm-email');
    await page.goto(confirmation);
    await page.getByRole('button', { name: 'Confirmar mi correo', exact: true }).click();
    await expect(
      page.getByRole('heading', { name: 'Correo confirmado', exact: true }),
    ).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash).toBe('');
    await expect(page.getByRole('alert')).toHaveCount(0);
    await page.goto('/login');
    await page.getByLabel('Correo electrónico', { exact: true }).fill(email);
    await page.getByLabel('Contraseña', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
    await expect(page).toHaveURL(/\/profile/);
    const me = await page.request.get(`${api}/me`);
    expect(me.status()).toBe(200);
    const account = (await me.json()) as {
      id: string;
      email: string;
      emailConfirmed: boolean;
      permissions: string[];
      levels: string[];
    };
    expect(account.email).toBe(email);
    expect(account.emailConfirmed).toBe(true);
    expect(account.permissions).not.toContain('Users.Manage');
    expect(account.levels).toContain('Externo');
    const cookies = await context.cookies();
    const session = cookies.find((cookie) => cookie.name === '__Host-acropolis-session');
    expect(session).toBeDefined();
    expect(session?.secure).toBe(true);
    expect(session?.httpOnly).toBe(true);
    expect(session?.sameSite).toBe('Lax');
    expect(session?.path).toBe('/');
    expect(session?.expires).toBe(-1);
    expect(session?.domain.startsWith('.')).toBe(false);
    const admin = await page.request.get('/api/v1/admin/users');
    expect(admin.status()).toBe(403);
    const escalation = await write(
      page.request,
      'me',
      {
        displayName: 'Perfil QA',
        permissions: ['Users.Manage'],
        levels: ['Hachado'],
      },
      'PATCH',
    );
    expect(escalation.status()).toBe(400);
    await page.getByLabel('Nombre visible', { exact: true }).fill('Perfil QA');
    const profileSave = page.waitForResponse(
      (response) =>
        response.url().endsWith('/identity/me') && response.request().method() === 'PATCH',
    );
    await page.getByRole('button', { name: 'Guardar cambios', exact: true }).click();
    expect((await profileSave).status()).toBe(200);
    await expect(page.getByLabel('Nombre visible', { exact: true })).toHaveValue('Perfil QA');

    if (testInfo.project.name === 'desktop-chromium') {
      const output = process.env['QA_SESSION_STATE_DIR'];
      if (!output) throw new Error('Private QA session state directory is required.');
      fs.mkdirSync(output, { recursive: true, mode: 0o700 });
      const statePath = path.join(output, 'active.json');
      await context.storageState({ path: statePath });
      fs.chmodSync(statePath, 0o600);
      fs.writeFileSync(
        path.join(output, 'account.json'),
        JSON.stringify({ email, id: account.id }),
        { mode: 0o600 },
      );
    }
    const forgot = await write(page.request, 'forgot-password', { email });
    expect(forgot.status()).toBe(202);
    expect(await forgot.json()).toEqual(antiEnumeration);
    const reset = await mailLink(page.request, email, '/reset-password');
    await page.goto(reset);
    await expect.poll(() => new URL(page.url()).hash).toBe('');
    await page.getByLabel('Nueva contraseña', { exact: true }).fill(password + 'N');
    await page.getByLabel('Confirmar contraseña', { exact: true }).fill(password + 'N');
    await page.getByRole('button', { name: /Restablecer contraseña/ }).click();
    await expect(
      page.getByRole('heading', { name: 'Contraseña actualizada', exact: true }),
    ).toBeVisible();
    expect((await page.request.get(`${api}/me`)).status()).toBe(401);
    await page.goto('/login');
    await page.getByLabel('Correo electrónico', { exact: true }).fill(email);
    await page.getByLabel('Contraseña', { exact: true }).fill(password + 'N');
    await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
    await expect(page).toHaveURL(/\/profile/);
    const currentPassword = page.getByLabel('Contraseña actual', { exact: true });
    await currentPassword.fill(password + 'incorrect');
    await page.getByLabel('Nueva contraseña', { exact: true }).fill(password + 'NN');
    await page.getByLabel('Confirmar contraseña', { exact: true }).fill(password + 'NN');
    const rejectedPasswordChange = page.waitForResponse(
      (response) =>
        response.url().endsWith('/identity/change-password') &&
        response.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Cambiar contraseña', exact: true }).click();
    const rejectedChange = await rejectedPasswordChange;
    expect(rejectedChange.status()).toBe(400);
    expect(await rejectedChange.json()).toMatchObject({
      code: 'current_password_invalid',
      fieldErrors: { currentPassword: expect.any(Array) },
    });
    await expect(
      page.getByText('La contraseña actual no es correcta. Revísala e inténtalo de nuevo.', {
        exact: true,
      }),
    ).toBeVisible();
    await expect(currentPassword).toHaveAttribute('aria-invalid', 'true');
    await expect(currentPassword).toHaveAccessibleDescription('Revisa el valor de este campo.');
    await expect(page).toHaveURL(/\/profile/);
    expect((await page.request.get(`${api}/me`)).status()).toBe(200);
    await currentPassword.fill(password + 'N');
    await page.getByRole('button', { name: 'Cambiar contraseña', exact: true }).click();
    await expect(page).toHaveURL(/\/login/);
    expect((await page.request.get(`${api}/me`)).status()).toBe(401);
    expect((await write(page.request, 'login', { email, password: password + 'N' })).status()).toBe(
      401,
    );
    expect(
      (await write(page.request, 'login', { email, password: password + 'NN' })).status(),
    ).toBe(200);
    await page.goto('/profile');
    if (testInfo.project.name === 'desktop-chromium') {
      const output = process.env['QA_SESSION_STATE_DIR']!;
      await context.storageState({ path: path.join(output, 'revoked.json') });
      fs.chmodSync(path.join(output, 'revoked.json'), 0o600);
    }
    await page.getByRole('button', { name: 'Cerrar sesión', exact: true }).click();
    await expect(page).toHaveURL(/\/login/);
    expect((await page.request.get(`${api}/me`)).status()).toBe(401);
    // A fresh valid session is retained for recreation and recovery checks.
    if (testInfo.project.name === 'desktop-chromium') {
      expect(
        (await write(page.request, 'login', { email, password: password + 'NN' })).status(),
      ).toBe(200);
      await context.storageState({
        path: path.join(process.env['QA_SESSION_STATE_DIR']!, 'active.json'),
      });
      fs.chmodSync(path.join(process.env['QA_SESSION_STATE_DIR']!, 'active.json'), 0o600);
    }
  });

  test('anonymous authorization, CSRF and anti enumeration enforce the HTTP boundary', async ({
    request,
  }) => {
    expect((await request.get(`${api}/me`)).status()).toBe(401);
    expect((await request.get('/api/v1/admin/users')).status()).toBe(401);
    const noCsrf = await request.post(`${api}/login`, {
      data: { email: 'unknown@acropolis.test', password },
    });
    expect(noCsrf.status()).toBe(400);
    const crossOrigin = await request.post(`${api}/login`, {
      headers: { 'X-CSRF-TOKEN': await csrf(request), Origin: 'https://foreign.test' },
      data: { email: 'unknown@acropolis.test', password },
    });
    expect([400, 403]).toContain(crossOrigin.status());
    const forgot = await write(request, 'forgot-password', { email: 'unknown@acropolis.test' });
    expect(forgot.status()).toBe(202);
    expect(await forgot.json()).toEqual(antiEnumeration);
    const resend = await write(request, 'resend-confirmation', { email: 'unknown@acropolis.test' });
    expect(resend.status()).toBe(202);
    expect(await resend.json()).toEqual(antiEnumeration);
    const invalidReset = await write(request, 'reset-password', {
      userId: '00000000-0000-0000-0000-000000000000',
      token: 'invalid',
      newPassword: password,
    });
    expect([400, 422]).toContain(invalidReset.status());
    expect((await request.get('/preview.html')).status()).toBe(404);
    expect((await request.get('/api/v1/identity/demo')).status()).toBe(404);
  });
  test('administration edits institutional levels, rejects stale versions and revokes access immediately', async ({
    page,
    browser,
  }, testInfo) => {
    test.setTimeout(60_000);
    const origin = process.env['BASE_URL'];
    if (!password || !origin) throw new Error('Isolated QA password and origin are required.');
    const number = testInfo.project.name === 'desktop-chromium' ? 90 : 91;
    const email = 'qa-load-' + String(number).padStart(6, '0') + '@example.test';
    const userContext = await browser.newContext({
      baseURL: origin,
      ignoreHTTPSErrors: false,
    });
    try {
      const targetLogin = await write(userContext.request, 'login', { email, password });
      expect(targetLogin.status()).toBe(200);
      const target = (await targetLogin.json()) as { id: string };
      await page.goto('/login');
      await page
        .getByLabel('Correo electrónico', { exact: true })
        .fill('qa-load-000000@example.test');
      await page.getByLabel('Contraseña', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Ingresar', exact: true }).click();
      await page.getByRole('link', { name: 'Administración', exact: true }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Personas que conectan.');
      await page.getByLabel('Buscar usuarios', { exact: true }).fill(email);
      await page.getByRole('button', { name: 'Buscar', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('1 usuario');
      await page.getByRole('link', { name: 'Ver usuario QA User ' + number, exact: true }).click();
      const current = await page.request.get('/api/v1/admin/users/' + target.id);
      expect(current.status()).toBe(200);
      const existing = (await current.json()) as { version: string };
      const concurrent = await page.request.patch('/api/v1/admin/users/' + target.id, {
        headers: { 'X-CSRF-TOKEN': await csrf(page.request) },
        data: { version: existing.version, displayName: 'Cambio concurrente QA' },
      });
      expect(concurrent.status()).toBe(200);
      await page.getByLabel('Nombre visible', { exact: true }).fill('Nombre obsoleto');
      await page.getByRole('button', { name: 'Guardar usuario', exact: true }).click();
      await expect(page.getByRole('alert')).toContainText('Otra persona actualizó este usuario.');
      await page.getByRole('button', { name: 'Recargar datos', exact: true }).click();
      await expect(page.getByLabel('Nombre visible', { exact: true })).toHaveValue(
        'Cambio concurrente QA',
      );
      await page.getByLabel('Hachado', { exact: true }).check();
      const levelSave = page.waitForResponse(
        (response) =>
          response.url().endsWith('/admin/users/' + target.id) &&
          response.request().method() === 'PATCH',
      );
      await page.getByRole('button', { name: 'Guardar usuario', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Guardamos los cambios del usuario.');
      expect((await levelSave).status()).toBe(200);
      expect((await userContext.request.get(api + '/me')).status()).toBe(401);
      const newLogin = await write(userContext.request, 'login', { email, password });
      expect(newLogin.status()).toBe(200);
      const updated = (await newLogin.json()) as { levels: string[]; permissions: string[] };
      expect(updated.levels).toContain('Hachado');
      expect(updated.permissions).not.toContain('Users.Manage');
      expect((await userContext.request.get('/api/v1/admin/users')).status()).toBe(403);
      await page.getByLabel('Estado de la cuenta', { exact: true }).selectOption('disabled');
      const disable = page.waitForResponse(
        (response) =>
          response.url().endsWith('/admin/users/' + target.id) &&
          response.request().method() === 'PATCH',
      );
      await page.getByRole('button', { name: 'Guardar usuario', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Guardamos los cambios del usuario.');
      expect((await disable).status()).toBe(200);
      expect((await userContext.request.get(api + '/me')).status()).toBe(401);
      expect((await write(userContext.request, 'login', { email, password })).status()).toBe(401);
    } finally {
      await userContext.close();
    }
  });
});
