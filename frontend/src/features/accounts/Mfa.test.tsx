import { act, fireEvent, render, screen, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { clearCsrf, type User } from '../../api/identity';
import { json, user } from '../../test/fixtures';
import { challenge, enrollment, recoveryCodes } from '../../test/mfaFixtures';
import { MfaLogin } from './Mfa';
import { mfa } from '../../api/mfa';
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(path: string, account: User | null = user, override?: Override) {
  window.history.replaceState({}, '', path);
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    if (url.endsWith('/identity/me'))
      return account ? json(account) : json({ code: 'unauthorized' }, 401);
    if (url.endsWith('/csrf')) return json({ token: 'csrf' });
    if (url.endsWith('/login')) return json(challenge(), 202);
    if (url.endsWith('/mfa'))
      return json({ enabled: false, required: false, recoveryCodesLeft: 0 });
    if (url.endsWith('/enrollment')) return json(enrollment());
    if (url.endsWith('/enable')) return json({ user, recoveryCodes });
    if (url.endsWith('/challenge')) return json(user);
    if (url.endsWith('/recovery-codes')) return json({ recoveryCodes });
    if (url.endsWith('/disable') || url.endsWith('/logout'))
      return new Response(null, { status: 204 });
    if (url.endsWith('/capabilities')) return json({ emailEnabled: true });
    return json({ items: [], total: 0, page: 1, pageSize: 20 });
  });
  vi.stubGlobal('fetch', fetch);
  renderApp();
  return fetch;
}
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value } });
}
async function passwordLogin() {
  fill('Correo electrónico', 'qa@example.test');
  fill('Contraseña', 'Una contraseña larga');
  await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }));
  await screen.findByRole('heading', { name: 'Confirma que eres tú.' });
}
const enabled = (required = false, left = 10) =>
  json({ enabled: true, required, recoveryCodesLeft: left });
beforeEach(() => {
  clearCsrf();
  vi.stubGlobal('scrollTo', vi.fn());
});
describe('Two-factor account journeys', () => {
  it('blocks conflicting controls while verifying the pending second factor', async () => {
    let release: ((value: User) => void) | undefined;
    const verify = vi.spyOn(mfa, 'verify').mockImplementation(
      () =>
        new Promise<User>((resolve) => {
          release = resolve;
        }),
    );
    const authenticated = vi.fn();
    const cancel = vi.fn();
    const view = render(
      <MfaLogin challenge={challenge()} authenticated={authenticated} cancel={cancel} />,
    );
    const controls = within(view.container);
    fireEvent.change(controls.getByLabelText('Código del autenticador'), {
      target: { value: '123456' },
    });
    await userEvent.click(controls.getByRole('button', { name: 'Verificar e ingresar' }));
    await waitFor(() => expect(verify).toHaveBeenCalledOnce());
    expect(controls.getByRole('button', { name: 'Volver a ingresar' })).toBeDisabled();
    expect(controls.getByRole('button', { name: 'Usar un código de recuperación' })).toBeDisabled();
    expect(authenticated).not.toHaveBeenCalled();
    expect(cancel).not.toHaveBeenCalled();
    view.unmount();
    await act(async () => {
      release?.(user);
    });
    expect(authenticated).not.toHaveBeenCalled();
  }, 10_000);
  it('ignores successful verification after leaving the challenge route', async () => {
    let release: ((response: Response) => void) | undefined;
    mount('/login', null, (path) =>
      path.endsWith('/challenge')
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : undefined,
    );
    await passwordLogin();
    const main = within(document.querySelector('main')!);
    fill('Código del autenticador', '123456');
    await userEvent.click(main.getByRole('button', { name: 'Verificar e ingresar' }));
    await waitFor(() => expect(release).toBeTypeOf('function'));
    const header = within(document.querySelector('header')!);
    await userEvent.click(header.getByRole('link', { name: 'Inicio' }));
    const home = within(document.querySelector('main')!);
    await home.findByText('Tu mediateca cultural.', { selector: 'h1' });
    await act(async () => {
      release?.(json(user));
    });
    expect(window.location.pathname).toBe('/');
    expect(header.queryByRole('link', { name: 'Mi perfil' })).not.toBeInTheDocument();
  }, 10_000);
  it('invalidates a pending verification at the challenge deadline without authenticating from its late response', async () => {
    vi.useFakeTimers();
    let release: ((value: User) => void) | undefined;
    vi.spyOn(mfa, 'verify').mockImplementation(
      () =>
        new Promise<User>((resolve) => {
          release = resolve;
        }),
    );
    const authenticated = vi.fn();
    const cancel = vi.fn();
    render(
      <MfaLogin
        challenge={{ ...challenge(), expiresUtc: new Date(Date.now() + 1000).toISOString() }}
        authenticated={authenticated}
        cancel={cancel}
      />,
    );
    fill('Código del autenticador', '123456');
    fireEvent.click(screen.getByRole('button', { name: 'Verificar e ingresar' }));
    expect(screen.getByRole('button', { name: 'Volver a ingresar' })).toBeDisabled();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
    expect(cancel).toHaveBeenCalledWith(
      'Esta verificación ha caducado. Ingresa de nuevo para continuar.',
    );
    await act(async () => {
      release?.(user);
    });
    expect(authenticated).not.toHaveBeenCalled();
  });
  it('does not restore an abandoned security view from a late enrollment-enable response', async () => {
    let release: ((response: Response) => void) | undefined;
    mount('/profile/security', user, (path) =>
      path.endsWith('/enable')
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : undefined,
    );
    await screen.findByRole(
      'heading',
      { name: 'Configura tu autenticador.', level: 2 },
      { timeout: 3000 },
    );
    await screen.findByLabelText('Contraseña actual');
    fill('Contraseña actual', 'Correct Password');
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    await screen.findByLabelText('Clave del autenticador');
    fill('Código del autenticador', '123456');
    await userEvent.click(
      screen.getByRole('button', { name: 'Activar verificación en dos pasos' }),
    );
    await userEvent.click(screen.getByRole('link', { name: 'Mi perfil' }));
    await screen.findByRole('heading', { name: 'Mi perfil' });
    await act(async () => {
      release?.(json({ user: { ...user, displayName: 'Late User' }, recoveryCodes }));
    });
    expect(screen.getByRole('heading', { name: 'Mi perfil' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: user.displayName, level: 2 })).toBeInTheDocument();
    expect(screen.queryByText('Late User')).not.toBeInTheDocument();
    expect(screen.queryByText(recoveryCodes[0] as string)).not.toBeInTheDocument();
    expect(window.location.pathname).toBe('/profile');
  }, 10_000);

  it('does not create a local session before the second factor, then verifies authenticator or recovery explicitly', async () => {
    let wrong = true;
    const fetch = mount('/login', null, (path) =>
      path.endsWith('/challenge') && wrong
        ? json({ code: 'mfa_invalid_code', fieldErrors: { code: ['private'] } }, 400)
        : undefined,
    );
    await passwordLogin();
    const main = within(document.querySelector('main')!);
    expect(screen.queryByRole('link', { name: 'Mi perfil' })).not.toBeInTheDocument();
    fill('Código del autenticador', '123456');
    await userEvent.click(main.getByRole('button', { name: 'Verificar e ingresar' }));
    expect(await main.findByRole('alert')).toHaveTextContent('El código no es válido');
    expect(main.getByLabelText('Código del autenticador', { exact: true })).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(window.location.pathname).toBe('/login');
    await userEvent.click(main.getByRole('button', { name: 'Usar un código de recuperación' }));
    await userEvent.click(main.getByRole('button', { name: 'Usar mi autenticador' }));
    await userEvent.click(main.getByRole('button', { name: 'Usar un código de recuperación' }));
    fill('Código de recuperación', 'QA-RECOVERY-0');
    wrong = false;
    await userEvent.click(main.getByRole('button', { name: 'Verificar e ingresar' }));
    await screen.findByRole('heading', { name: 'Mi perfil' });
    expect(
      fetch.mock.calls.some(([, options]) => String(options?.body).includes('recoveryCode')),
    ).toBe(true);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  }, 10_000);
  it('completes administrative enrollment before granting the full user and shows codes exactly once', async () => {
    let failed = true;
    mount('/login', null, (path) =>
      path.endsWith('/login')
        ? json(challenge(true), 202)
        : path.endsWith('/enrollment') && failed
          ? json({}, 503)
          : undefined,
    );
    await passwordLogin();
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    await screen.findByRole('alert');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    await screen.findByLabelText('Clave del autenticador');
    expect(
      screen
        .getByRole('img', { name: 'Código QR para tu aplicación de autenticación' })
        .tagName.toLowerCase(),
    ).toBe('svg');
    fill('Código del autenticador', '123456');
    await userEvent.click(
      screen.getByRole('button', { name: 'Activar verificación en dos pasos' }),
    );
    await screen.findByRole('heading', { name: 'Códigos de recuperación' });
    expect(screen.getAllByRole('listitem')).toHaveLength(10);
    expect(screen.queryByRole('link', { name: 'Mi perfil' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'He guardado mis códigos' }));
    await screen.findByRole('heading', { name: 'Mi perfil' });
    expect(screen.queryByText(recoveryCodes[0] as string)).not.toBeInTheDocument();
  }, 10_000);
  it('allows cancellation and expiration of a pending challenge without retaining secrets', async () => {
    mount('/login', null);
    await passwordLogin();
    await userEvent.click(screen.getByRole('button', { name: 'Volver a ingresar' }));
    await screen.findByRole('heading', { name: 'Ingresar' }, { timeout: 3000 });
    expect(screen.queryByLabelText('Código del autenticador')).not.toBeInTheDocument();
  }, 10_000);
  it('expires a challenge using its actual deadline and returns to password login', async () => {
    mount('/login', null, (path) =>
      path.endsWith('/login')
        ? json({ ...challenge(), expiresUtc: new Date(Date.now() - 1).toISOString() }, 202)
        : undefined,
    );
    fill('Correo electrónico', 'qa@example.test');
    fill('Contraseña', 'Una contraseña larga');
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }));
    await screen.findByText('Esta verificación ha caducado. Ingresa de nuevo para continuar.');
    expect(screen.getByRole('heading', { name: 'Ingresar' })).toBeInTheDocument();
  });
  it('loads the own security route, handles an unavailable status, and enables with manual and local QR provisioning', async () => {
    let failed = true;
    const fetch = mount('/profile/security', user, (path) =>
      path.endsWith('/mfa') && failed ? json({}, 503) : undefined,
    );
    await screen.findByRole('alert');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByLabelText('Contraseña actual');
    fill('Contraseña actual', 'Correct Password');
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    await screen.findByLabelText('Clave del autenticador');
    expect(screen.getByLabelText('Clave del autenticador')).toHaveValue(enrollment().sharedKey);
    fill('Código del autenticador', '123456');
    await userEvent.click(
      screen.getByRole('button', { name: 'Activar verificación en dos pasos' }),
    );
    await screen.findByRole('heading', { name: 'Códigos de recuperación' });
    expect(screen.getAllByRole('listitem')).toHaveLength(10);
    await userEvent.click(screen.getByRole('button', { name: 'He guardado mis códigos' }));
    await screen.findByRole('heading', { name: 'Mi perfil' });
    expect(fetch.mock.calls.filter(([path]) => String(path).endsWith('/enable'))).toHaveLength(1);
  }, 10_000);
  it('preserves the security session and marks the password field when reauthentication is incorrect', async () => {
    mount('/profile/security', user, (path) =>
      path.endsWith('/enrollment')
        ? json(
            {
              code: 'current_password_invalid',
              fieldErrors: { currentPassword: ['Private diagnostic'] },
            },
            400,
          )
        : undefined,
    );
    await screen.findByLabelText('Contraseña actual');
    fill('Contraseña actual', 'Wrong Password');
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'La contraseña actual no es correcta',
    );
    expect(screen.getByLabelText('Contraseña actual')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('link', { name: 'Mi perfil' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/profile/security');
    expect(screen.queryByText('Private diagnostic')).not.toBeInTheDocument();
  });
  it('renews codes with reauthentication, clears the revoked session, and preserves only the one-time codes until acknowledgment', async () => {
    mount('/profile/security', user, (path) => (path.endsWith('/mfa') ? enabled() : undefined));
    await screen.findByRole('heading', { name: 'Tu verificación está activa.' });
    await userEvent.click(screen.getByRole('button', { name: 'Renovar códigos de recuperación' }));
    fill('Contraseña actual', 'Correct Password');
    fill('Código del autenticador', '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar renovación' }));
    await screen.findByRole('heading', { name: 'Códigos de recuperación' });
    expect(screen.queryByRole('link', { name: 'Mi perfil' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(10);
    await userEvent.click(screen.getByRole('button', { name: 'He guardado mis códigos' }));
    await screen.findByRole('heading', { name: 'Ingresar' }, { timeout: 3000 });
    expect(screen.queryByText(recoveryCodes[0] as string)).not.toBeInTheDocument();
  }, 10_000);
  it('allows recovery reauthentication and cancellation, then disables and signs out on success', async () => {
    const fetch = mount('/profile/security', user, (path) =>
      path.endsWith('/mfa') ? enabled(false, 1) : undefined,
    );
    const content = within(document.querySelector('main')!);
    const count = await content.findByText(
      /Conservas 1 código de recuperación/,
      {},
      { timeout: 3000 },
    );
    expect(count).toBeVisible();
    expect(
      fetch.mock.calls.some(
        ([path, init]) => String(path).endsWith('/identity/mfa') && init?.method === 'GET',
      ),
    ).toBe(true);
    const surface = count.closest<HTMLDivElement>('.mfa-surface');
    if (!surface) throw new Error('Loaded MFA security surface is missing.');
    const security = within(surface);
    await userEvent.click(
      security.getByRole('button', { name: 'Desactivar verificación en dos pasos' }),
    );
    await userEvent.click(security.getByRole('button', { name: 'Cancelar' }));
    expect(
      screen.queryByRole('button', { name: 'Confirmar desactivación' }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      security.getByRole('button', { name: 'Desactivar verificación en dos pasos' }),
    );
    await userEvent.click(security.getByRole('button', { name: 'Usar un código de recuperación' }));
    await userEvent.click(security.getByRole('button', { name: 'Usar mi autenticador' }));
    await userEvent.click(security.getByRole('button', { name: 'Usar un código de recuperación' }));
    fireEvent.change(security.getByLabelText('Contraseña actual', { exact: true }), {
      target: { value: 'Correct Password' },
    });
    fireEvent.change(security.getByLabelText('Código de recuperación', { exact: true }), {
      target: { value: 'QA-RECOVERY-0' },
    });
    await userEvent.click(security.getByRole('button', { name: 'Confirmar desactivación' }));
    await screen.findByRole('heading', { name: 'Ingresar' }, { timeout: 3000 });
    const call = fetch.mock.calls.find(([path]) => String(path).endsWith('/disable'));
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      currentPassword: 'Correct Password',
      recoveryCode: 'QA-RECOVERY-0',
    });
  }, 10_000);
  it('does not offer disabling to administrators and does not reveal protected security to anonymous users', async () => {
    await act(async () => {
      mount('/profile/security', user, (path) =>
        path.endsWith('/mfa') ? enabled(true) : undefined,
      );
    });
    await screen.findByText('Las cuentas administrativas deben mantener esta protección.');
    expect(
      screen.queryByRole('button', { name: 'Desactivar verificación en dos pasos' }),
    ).not.toBeInTheDocument();
  });
  it('protects the security route while loading and redirects anonymous access', async () => {
    mount('/profile/security', null);
    expect(screen.getByRole('status')).toHaveTextContent('Comprobando tu sesión');
    await screen.findByRole('heading', { name: 'Ingresar' }, { timeout: 3000 });
  });
  it('clears expired enrollment secrets and allows starting again', async () => {
    mount('/profile/security', user, (path) =>
      path.endsWith('/enrollment')
        ? json({ ...enrollment(), expiresUtc: new Date(Date.now() + 20).toISOString() })
        : undefined,
    );
    await screen.findByLabelText('Contraseña actual');
    fill('Contraseña actual', 'Correct Password');
    await userEvent.click(screen.getByRole('button', { name: 'Configurar autenticador' }));
    await screen.findByText('La configuración ha caducado. Iníciala de nuevo.');
    await waitFor(() =>
      expect(screen.queryByLabelText('Clave del autenticador')).not.toBeInTheDocument(),
    );
  });
});
