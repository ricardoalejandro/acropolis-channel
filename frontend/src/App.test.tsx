import { StrictMode } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { clearCsrf, type User } from './api/identity';
import { user, admin, json } from './test/fixtures';
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(
  path: string,
  account: User | null = null,
  override?: Override,
  strict = false,
  navigationState: unknown = null,
) {
  window.history.replaceState({ usr: navigationState, key: 'initial' }, '', path);
  let current = account;
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    if (url.includes('/catalog/content?'))
      return json({ items: [], total: 0, page: 1, pageSize: 3 });
    if (url.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (url.endsWith('/csrf')) return json({ token: 'sample-csrf' });
    if (url.endsWith('/identity/me')) {
      if (init?.method === 'PATCH') {
        current = { ...(current ?? user), displayName: String(body['displayName']) };
        return json(current);
      }
      return current ? json(current) : json({ code: 'unauthorized' }, 401);
    }
    if (url.endsWith('/login')) {
      current = user;
      return json(current);
    }
    if (url.endsWith('/logout')) {
      current = null;
      return new Response(null, { status: 204 });
    }
    if (/\/admin\/users\?/.test(url))
      return json({ items: [user], page: 1, pageSize: 20, total: 1 });
    if (url.endsWith('/admin/users/test-person'))
      return json(init?.method === 'PATCH' ? { ...user, ...body, version: 'version-two' } : user);
    if (
      url.endsWith('/register') ||
      url.endsWith('/forgot-password') ||
      url.endsWith('/resend-confirmation')
    )
      return json({ message: 'Si corresponde, recibirás un correo con las instrucciones.' }, 202);
    return new Response(null, { status: 204 });
  });
  vi.stubGlobal('fetch', fetch);
  render(
    strict ? (
      <StrictMode>
        <App />
      </StrictMode>
    ) : (
      <App />
    ),
  );
  return fetch;
}
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value } });
}
beforeEach(() => {
  clearCsrf();
  vi.stubGlobal('scrollTo', vi.fn());
});
describe('Account journeys with real HTTP-shaped responses', () => {
  it('shows the institutional home without catalogue fixtures and handles anonymous session', async () => {
    mount('/');
    await userEvent.click(screen.getByRole('link', { name: 'Saltar al contenido' }));
    expect(screen.getByRole('main')).toHaveFocus();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Conéctate');
    await screen.findByRole('link', { name: 'Ingresar' });
    expect(screen.getByRole('link', { name: /Crear mi cuenta/ })).toHaveAttribute(
      'href',
      '/register',
    );
    expect(screen.queryByText('Contenido de demostración')).not.toBeInTheDocument();
  });
  it('shows authenticated home actions and prevents login returning to an unsafe external URL', async () => {
    mount('/', user);
    await screen.findByRole('link', { name: 'Mi perfil' });
    expect(screen.getByRole('link', { name: /Ir a mi perfil/ })).toHaveAttribute(
      'href',
      '/profile',
    );
  });
  it('registers allowed fields and carries only the submitted email into the pending confirmation page', async () => {
    const fetch = mount('/register');
    await screen.findByLabelText('Nombre visible');
    fill('Nombre visible', '  Persona Nueva  ');
    fill('Correo electrónico', '  Persona@example.test  ');
    fill('Contraseña', 'Una frase larga para entrar');
    fill('Confirmar contraseña', 'Una frase larga para entrar');
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    await screen.findByRole('heading', { name: 'Revisa tu correo.' });
    const call = fetch.mock.calls.find(([path]) => String(path).endsWith('/register'));
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      displayName: 'Persona Nueva',
      email: 'Persona@example.test',
      password: 'Una frase larga para entrar',
    });
    expect(window.history.state['usr']).toEqual({
      kind: 'registration',
      email: 'Persona@example.test',
    });
    expect(window.location.pathname).toBe('/email-pending');
    expect(window.location.search).toBe('');
    expect(window.location.hash).toBe('');
    expect(JSON.stringify(window.history.state)).not.toMatch(/password|token|Una frase larga/);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(screen.queryByLabelText('Nivel institucional')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Correo electrónico')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Contraseña')).not.toBeInTheDocument();
    expect(screen.getByText(/Correo utilizado:/)).toHaveTextContent('Persona@example.test');
    const resend = await screen.findByRole('button', { name: 'Reenviar enlace' });
    expect(screen.getByText('¿No recibiste el mensaje?')).toBeInTheDocument();
    expect(
      screen.getByText('Puedes solicitar un nuevo enlace una vez por minuto.'),
    ).toBeInTheDocument();
    expect(resend).toHaveClass('button', 'button-outline');
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/resend-confirmation'))).toBe(
      false,
    );
    await userEvent.click(resend);
    await screen.findByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.');
    const resends = fetch.mock.calls.filter(([path]) =>
      String(path).endsWith('/resend-confirmation'),
    );
    expect(resends).toHaveLength(1);
    expect(JSON.parse(String(resends[0]?.[1]?.body))).toEqual({ email: 'Persona@example.test' });
    expect(screen.queryByLabelText('Correo electrónico')).not.toBeInTheDocument();
    expect(screen.getByText(/Correo utilizado:/)).toHaveTextContent('Persona@example.test');
  });
  it('keeps registration failures on the original form and creates navigation context only after an explicit successful retry', async () => {
    let unavailable = true;
    const fetch = mount('/register', null, (path) =>
      path.endsWith('/register') && unavailable
        ? json({ code: 'email_unavailable', detail: 'private server diagnostic' }, 503)
        : undefined,
    );
    await screen.findByLabelText('Nombre visible');
    fill('Nombre visible', 'Persona Nueva');
    fill('Correo electrónico', 'persona@example.test');
    fill('Contraseña', 'Una frase larga para entrar');
    fill('Confirmar contraseña', 'Una frase larga para entrar');
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'El registro y la recuperación por correo no están disponibles temporalmente.',
    );
    expect(window.location.pathname).toBe('/register');
    expect(window.history.state['usr']).toBeNull();
    expect(screen.getByLabelText('Correo electrónico')).toHaveValue('persona@example.test');
    expect(screen.queryByText(/Correo utilizado:/)).not.toBeInTheDocument();
    expect(screen.queryByText('private server diagnostic')).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/resend-confirmation'))).toBe(
      false,
    );
    unavailable = false;
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    await screen.findByRole('heading', { name: 'Revisa tu correo.' });
    expect(window.history.state['usr']).toEqual({
      kind: 'registration',
      email: 'persona@example.test',
    });
    expect(fetch.mock.calls.filter(([path]) => String(path).endsWith('/register'))).toHaveLength(2);
  });
  it.each([
    ['trimmed registration email', '  Persona@example.test  ', 'Persona@example.test'],
    ['maximum-length registration email', 'a'.repeat(247) + '@b.test', 'a'.repeat(247) + '@b.test'],
  ])('uses a valid %s without exposing an editable email field', async (_name, email, expected) => {
    const fetch = mount('/email-pending', null, undefined, false, { kind: 'registration', email });
    await screen.findByRole('button', { name: 'Reenviar enlace' });
    expect(screen.getByText(/Correo utilizado:/)).toHaveTextContent(expected as string);
    expect(screen.queryByLabelText('Correo electrónico')).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/resend-confirmation'))).toBe(
      false,
    );
  });
  it.each([
    ['absent', null],
    ['primitive', 'persona@example.test'],
    ['array', [{ kind: 'registration', email: 'persona@example.test' }]],
    ['missing kind', { email: 'persona@example.test' }],
    ['different kind', { kind: 'recovery', email: 'persona@example.test' }],
    ['missing email', { kind: 'registration' }],
    ['non-string email', { kind: 'registration', email: 123 }],
    ['empty email', { kind: 'registration', email: '   ' }],
    ['malformed email', { kind: 'registration', email: 'persona@example' }],
    ['overlong email', { kind: 'registration', email: 'a'.repeat(248) + '@b.test' }],
  ])('falls back to an empty email form for %s navigation context', async (_name, state) => {
    const fetch = mount('/email-pending', null, undefined, false, state);
    expect(await screen.findByLabelText('Correo electrónico')).toHaveValue('');
    expect(screen.queryByText(/Correo utilizado:/)).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/resend-confirmation'))).toBe(
      false,
    );
  });
  it('allows a direct visit to explicitly request confirmation for the entered address', async () => {
    const fetch = mount('/email-pending');
    expect(await screen.findByLabelText('Correo electrónico')).toHaveValue('');
    fill('Correo electrónico', '  otra@example.test  ');
    await userEvent.click(screen.getByRole('button', { name: 'Reenviar enlace' }));
    await screen.findByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.');
    const call = fetch.mock.calls.find(([path]) => String(path).endsWith('/resend-confirmation'));
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({ email: 'otra@example.test' });
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/register'))).toBe(false);
  });
  it.each([
    {
      name: 'rate limiting',
      fail: () => json({ code: 'rate_limited', detail: 'private diagnostic' }, 429),
      message: 'Has realizado varios intentos. Espera unos minutos y vuelve a intentarlo.',
    },
    {
      name: 'email unavailability',
      fail: () => json({ code: 'email_unavailable', detail: 'private diagnostic' }, 503),
      message: 'El registro y la recuperación por correo no están disponibles temporalmente.',
    },
    {
      name: 'network failure',
      fail: () => Promise.reject(new Error('private diagnostic')),
      message: 'No pudimos conectar. Comprueba tu conexión e inténtalo de nuevo.',
    },
  ])(
    'retains the registration email through $name and an explicit resend retry',
    async (failure) => {
      let rejected = true;
      const fetch = mount(
        '/email-pending',
        null,
        (path) => (path.endsWith('/resend-confirmation') && rejected ? failure.fail() : undefined),
        false,
        { kind: 'registration', email: 'persona@example.test' },
      );
      await userEvent.click(await screen.findByRole('button', { name: 'Reenviar enlace' }));
      expect(await screen.findByRole('alert')).toHaveTextContent(failure.message);
      expect(screen.getByRole('button', { name: 'Reenviar enlace' })).toBeEnabled();
      expect(screen.getByText(/Correo utilizado:/)).toHaveTextContent('persona@example.test');
      expect(screen.queryByLabelText('Correo electrónico')).not.toBeInTheDocument();
      expect(screen.queryByText('private diagnostic')).not.toBeInTheDocument();
      expect(
        screen.queryByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.'),
      ).not.toBeInTheDocument();
      expect(
        fetch.mock.calls.filter(([path]) => String(path).endsWith('/resend-confirmation')),
      ).toHaveLength(1);
      rejected = false;
      await userEvent.click(screen.getByRole('button', { name: 'Reenviar enlace' }));
      await screen.findByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.');
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
      const resends = fetch.mock.calls.filter(([path]) =>
        String(path).endsWith('/resend-confirmation'),
      );
      expect(resends).toHaveLength(2);
      for (const [, options] of resends)
        expect(JSON.parse(String(options?.body))).toEqual({ email: 'persona@example.test' });
    },
  );
  it('blocks duplicate resend submissions while a contextual request is pending', async () => {
    let release: ((response: Response) => void) | undefined;
    const fetch = mount(
      '/email-pending',
      null,
      (path) =>
        path.endsWith('/resend-confirmation')
          ? new Promise<Response>((resolve) => {
              release = resolve;
            })
          : undefined,
      false,
      { kind: 'registration', email: 'persona@example.test' },
    );
    const button = await screen.findByRole('button', { name: 'Reenviar enlace' });
    const form = button.closest('form') as HTMLFormElement;
    fireEvent.submit(form);
    await waitFor(() => expect(release).toBeDefined());
    fireEvent.submit(form);
    expect(form).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('button', { name: 'Un momento…' })).toBeDisabled();
    expect(
      fetch.mock.calls.filter(([path]) => String(path).endsWith('/resend-confirmation')),
    ).toHaveLength(1);
    release?.(json({ message: 'Si corresponde, recibirás un correo.' }, 202));
    await screen.findByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.');
    expect(screen.getByRole('button', { name: 'Reenviar enlace' })).toBeEnabled();
  });
  it('resets resend notices and errors when a new navigation reaches the same pending-email route', async () => {
    let rejected = false;
    mount(
      '/email-pending',
      null,
      (path) =>
        path.endsWith('/resend-confirmation') && rejected
          ? json({ code: 'rate_limited' }, 429)
          : undefined,
      false,
      { kind: 'registration', email: 'primera@example.test' },
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Reenviar enlace' }));
    await screen.findByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.');
    window.history.pushState(
      { usr: { kind: 'registration', email: 'segunda@example.test' }, key: 'second' },
      '',
      '/email-pending',
    );
    fireEvent(window, new PopStateEvent('popstate'));
    await screen.findByRole('button', { name: 'Reenviar enlace' });
    expect(screen.getByText(/Correo utilizado:/)).toHaveTextContent('segunda@example.test');
    expect(screen.queryByText(/primera@example.test/)).not.toBeInTheDocument();
    expect(
      screen.queryByText('Si corresponde, recibirás un nuevo enlace. Revisa tu correo.'),
    ).not.toBeInTheDocument();
    rejected = true;
    await userEvent.click(screen.getByRole('button', { name: 'Reenviar enlace' }));
    await screen.findByRole('alert');
    window.history.pushState({ usr: null, key: 'direct' }, '', '/email-pending');
    fireEvent(window, new PopStateEvent('popstate'));
    expect(await screen.findByLabelText('Correo electrónico')).toHaveValue('');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/Correo utilizado:/)).not.toBeInTheDocument();
  });
  it('reports invalid credentials safely then allows an explicit retry and profile navigation', async () => {
    let rejected = true;
    const fetch = mount('/login', null, (path) =>
      path.endsWith('/login') && rejected
        ? json({ code: 'invalid_credentials', detail: 'secret internals' }, 401)
        : undefined,
    );
    fill('Correo electrónico', 'persona@example.test');
    fill('Contraseña', 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No pudimos ingresar con esos datos',
    );
    expect(screen.queryByText('secret internals')).not.toBeInTheDocument();
    rejected = false;
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }));
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
    expect(fetch.mock.calls.filter(([path]) => String(path).endsWith('/login'))).toHaveLength(2);
    expect(localStorage.length).toBe(0);
  });
  it('redirects an authenticated visit to login into the own profile', async () => {
    mount('/login', user);
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
  });
  it('protects private routes while the session is loading and rejects unauthenticated access', async () => {
    mount('/profile');
    expect(screen.getByRole('status')).toHaveTextContent('Comprobando');
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
  });
  it('shows a retry instruction when session bootstrap is unavailable', async () => {
    mount('/', null, (path) => (path.endsWith('/identity/me') ? json({}, 503) : undefined));
    await screen.findByText(/No pudimos comprobar tu sesión/);
  });
  it('confirms only on user action and removes token fragments even in StrictMode', async () => {
    const fetch = mount(
      '/confirm-email#userId=person-id&token=temporary-token',
      null,
      undefined,
      true,
    );
    expect(window.location.hash).toBe('');
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/confirm-email'))).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar mi correo' }));
    await screen.findByRole('heading', { name: 'Correo confirmado' });
    const call = fetch.mock.calls.find(([path]) => String(path).endsWith('/confirm-email'));
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      userId: 'person-id',
      token: 'temporary-token',
    });
  });
  it('never accepts query tokens and directs an incomplete link to recovery', () => {
    mount('/reset-password?userId=person-id&token=temporary-token');
    expect(screen.getByRole('alert')).toHaveTextContent('Este enlace no es válido');
    expect(window.location.search).toBe('');
  });
  it('handles expired confirmation without revealing raw server details', async () => {
    mount('/confirm-email#userId=u&token=t', null, (path) =>
      path.endsWith('/confirm-email')
        ? json({ code: 'invalid_token', detail: 'private' }, 400)
        : undefined,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar mi correo' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Este enlace no es válido');
    expect(screen.queryByText('private')).not.toBeInTheDocument();
  });
  it('recovers an account with an indistinguishable acknowledgement', async () => {
    const fetch = mount('/forgot-password');
    await screen.findByLabelText('Correo electrónico');
    fill('Correo electrónico', 'unknown@example.test');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar instrucciones' }));
    await screen.findByRole('heading', { name: 'Revisa tu correo' });
    expect(screen.getByRole('status')).toHaveTextContent('Si corresponde');
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/forgot-password'))).toBe(true);
  });
  it('resets the password from a fragment without sending the confirmation-only field', async () => {
    const fetch = mount('/reset-password#userId=u&token=t');
    fill('Nueva contraseña', 'Una frase muy larga y nueva');
    fill('Confirmar contraseña', 'Una frase muy larga y nueva');
    await userEvent.click(screen.getByRole('button', { name: 'Restablecer contraseña' }));
    await screen.findByRole('heading', { name: 'Contraseña actualizada' });
    const call = fetch.mock.calls.find(([path]) => String(path).endsWith('/reset-password'));
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      userId: 'u',
      token: 't',
      newPassword: 'Una frase muy larga y nueva',
    });
  });
  it('updates own name without arbitrary target, level, email or permission changes', async () => {
    const fetch = mount('/profile', user);
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
    fill('Nombre visible', 'Nuevo Nombre');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Guardamos tus cambios.');
    expect(screen.getByRole('heading', { name: 'Hola, Nuevo.' })).toBeInTheDocument();
    const call = fetch.mock.calls.find(
      ([path, options]) => String(path).endsWith('/identity/me') && options?.method === 'PATCH',
    );
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({ displayName: 'Nuevo Nombre' });
  });
  it('keeps the profile and session on an incorrect current password until an explicit successful retry', async () => {
    const dispatched = vi.spyOn(window, 'dispatchEvent');
    const fetch = mount('/profile', user, (path, options) =>
      path.endsWith('/change-password') &&
      JSON.parse(String(options?.body))['currentPassword'] === 'Incorrect current password'
        ? json(
            {
              code: 'current_password_invalid',
              fieldErrors: { currentPassword: ['Private server diagnostic'] },
            },
            400,
          )
        : undefined,
    );
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
    fill('Contraseña actual', 'Incorrect current password');
    fill('Nueva contraseña', 'Una contraseña completamente nueva');
    fill('Confirmar contraseña', 'Una contraseña completamente nueva');
    await userEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'La contraseña actual no es correcta. Revísala e inténtalo de nuevo.',
    );
    expect(screen.getByLabelText('Contraseña actual', { exact: true })).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(screen.getByLabelText('Contraseña actual', { exact: true })).toHaveAccessibleDescription(
      'Revisa el valor de este campo.',
    );
    expect(window.location.pathname).toBe('/profile');
    expect(screen.getByRole('heading', { name: 'Hola, Persona.' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Mi perfil' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Qué bueno verte.' })).not.toBeInTheDocument();
    expect(screen.queryByText('Private server diagnostic')).not.toBeInTheDocument();
    expect(
      dispatched.mock.calls.some(([event]) => event.type === 'acropolis:session-expired'),
    ).toBe(false);
    expect(
      fetch.mock.calls.filter(([path]) => String(path).endsWith('/change-password')),
    ).toHaveLength(1);
    fill('Contraseña actual', 'Correct current password');
    await userEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
    expect(window.location.pathname).toBe('/login');
    expect(screen.getByRole('status')).toHaveTextContent('Contraseña actualizada');
    expect(
      fetch.mock.calls.filter(([path]) => String(path).endsWith('/change-password')),
    ).toHaveLength(2);
  });
  it('password change revokes local session and requires login with the new password', async () => {
    const fetch = mount('/profile', user);
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
    fill('Contraseña actual', 'Current Password');
    fill('Nueva contraseña', 'Una contraseña completamente nueva');
    fill('Confirmar contraseña', 'Una contraseña completamente nueva');
    await userEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
    expect(screen.getByRole('status')).toHaveTextContent('Contraseña actualizada');
    expect(fetch.mock.calls.some(([path]) => String(path).endsWith('/change-password'))).toBe(true);
  });
  it('logs out and redirects with a safe success notice', async () => {
    mount('/profile', user);
    await screen.findByRole('button', { name: 'Cerrar sesión' });
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar sesión' }));
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
    expect(screen.getByRole('status')).toHaveTextContent('Cerraste tu sesión');
  });
  it('preserves the session when logout fails and offers an explicit retry', async () => {
    mount('/profile', user, (path) => (path.endsWith('/logout') ? json({}, 503) : undefined));
    await screen.findByRole('button', { name: 'Cerrar sesión' });
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar sesión' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos cerrar tu sesión');
    expect(screen.getByRole('link', { name: 'Mi perfil' })).toBeInTheDocument();
  });
  it('reacts to server-side revocation with redirect and no protected UI', async () => {
    mount('/profile', user);
    await screen.findByRole('heading', { name: 'Hola, Persona.' });
    fireEvent(window, new Event('acropolis:session-expired'));
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
    expect(
      screen.getByText('Tu sesión ha finalizado. Ingresa de nuevo para continuar.'),
    ).toBeInTheDocument();
  });
  it('does not equate FFVV institutional level with administrator permission', async () => {
    mount('/admin/users', { ...user, levels: ['FFVV'] });
    await screen.findByRole('heading', { name: 'Este espacio requiere autorización.' });
    expect(screen.queryByLabelText('Buscar usuarios')).not.toBeInTheDocument();
  });
  it('renders a safe 404 without inventing a catalogue route', () => {
    mount('/unknown-route');
    expect(screen.getByRole('heading', { name: 'Esta página no está aquí.' })).toBeInTheDocument();
  });
});
describe('User administration', () => {
  it('filters and paginates real user-shaped results', async () => {
    const fetch = mount('/admin/users', admin, (path) =>
      /\/admin\/users\?/.test(path)
        ? json({ items: [user], page: 1, pageSize: 20, total: 21 })
        : undefined,
    );
    await screen.findByRole('link', { name: 'Ver usuario Persona de Prueba' });
    fill('Buscar usuarios', 'Persona');
    fireEvent.change(screen.getByLabelText('Estado'), { target: { value: 'active' } });
    fireEvent.change(screen.getByLabelText('Nivel institucional'), {
      target: { value: 'Externo' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await screen.findByRole('link', { name: 'Ver usuario Persona de Prueba' });
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }));
    await screen.findByText('Página 2');
    await userEvent.click(screen.getByRole('button', { name: 'Anterior' }));
    await screen.findByText('Página 1');
    expect(
      fetch.mock.calls.some(([path]) =>
        String(path).includes('search=Persona&status=active&level=Externo&page=2'),
      ),
    ).toBe(true);
  });
  it('handles empty results and a failed load with retry', async () => {
    let failed = true;
    mount('/admin/users', admin, (path) =>
      /\/admin\/users\?/.test(path)
        ? failed
          ? json({ code: 'forbidden' }, 403)
          : json({ items: [], page: 1, pageSize: 20, total: 0 })
        : undefined,
    );
    await screen.findByRole('alert');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByText('No hay usuarios con estos filtros.');
  });
  it('edits levels and status using the version without granting administrator permission', async () => {
    const fetch = mount('/admin/users/test-person', admin);
    await screen.findByLabelText('Nombre visible');
    fill('Nombre visible', 'Nombre Actualizado');
    fireEvent.change(screen.getByLabelText('Estado de la cuenta'), {
      target: { value: 'disabled' },
    });
    await userEvent.click(screen.getByLabelText('Miembro'));
    await userEvent.click(screen.getByLabelText('Externo'));
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    await screen.findByText('Guardamos los cambios del usuario.');
    const call = fetch.mock.calls.find(
      ([path, options]) =>
        String(path).endsWith('/admin/users/test-person') && options?.method === 'PATCH',
    );
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      version: 'version-one',
      displayName: 'Nombre Actualizado',
      levels: ['Miembro'],
      status: 'disabled',
    });
    expect(screen.queryByLabelText(/Administrador/)).not.toBeInTheDocument();
  });
  it('blocks invalid names before mutating and allows reloading a stale version', async () => {
    let conflict = true;
    const fetch = mount('/admin/users/test-person', admin, (path, init) =>
      path.endsWith('/admin/users/test-person') && init?.method === 'PATCH' && conflict
        ? json({ code: 'concurrency_conflict' }, 409)
        : undefined,
    );
    await screen.findByLabelText('Nombre visible');
    fill('Nombre visible', 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('El nombre debe tener');
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'PATCH')).toBe(false);
    fill('Nombre visible', 'Nombre Válido');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Otra persona actualizó');
    conflict = false;
    await userEvent.click(screen.getByRole('button', { name: 'Recargar datos' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    await screen.findByText('Guardamos los cambios del usuario.');
  });
  it('shows pending confirmation and does not send pending as an editable status', async () => {
    const fetch = mount('/admin/users/test-person', admin, (path, init) =>
      path.endsWith('/admin/users/test-person') && init?.method !== 'PATCH'
        ? json({ ...user, status: 'pending', emailConfirmed: false, permissions: ['Users.Manage'] })
        : undefined,
    );
    await screen.findByText('Correo pendiente de confirmar');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    await screen.findByText('Guardamos los cambios del usuario.');
    const call = fetch.mock.calls.find(
      ([path, options]) =>
        String(path).endsWith('/admin/users/test-person') && options?.method === 'PATCH',
    );
    expect(JSON.parse(String(call?.[1]?.body))).not.toHaveProperty('status');
  });
  it('provides a recoverable missing-user state', async () => {
    mount('/admin/users/test-person', admin, (path) =>
      path.endsWith('/admin/users/test-person') ? json({ code: 'not_found' }, 404) : undefined,
    );
    await screen.findByRole('alert');
    expect(screen.getByRole('button', { name: 'Recargar datos' })).toBeInTheDocument();
  });
  it('clears the current administrator session after editing its own institutional data', async () => {
    mount('/admin/users/test-admin', admin, (path, init) =>
      path.endsWith('/admin/users/test-admin')
        ? json(
            init?.method === 'PATCH'
              ? { ...admin, levels: ['Miembro'], version: 'next-version' }
              : admin,
          )
        : undefined,
    );
    await screen.findByLabelText('Nombre visible');
    await userEvent.click(screen.getByLabelText('Miembro'));
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    await screen.findByRole('heading', { name: 'Qué bueno verte.' });
    expect(screen.getByRole('status')).toHaveTextContent(
      'Actualizamos tu cuenta. Ingresa de nuevo para continuar.',
    );
    expect(screen.queryByRole('link', { name: 'Administración' })).not.toBeInTheDocument();
  });
  it('reflects the server pending state after enabling an unconfirmed account', async () => {
    mount('/admin/users/test-person', admin, (path, init) =>
      path.endsWith('/admin/users/test-person')
        ? json({
            ...user,
            emailConfirmed: false,
            status: init?.method === 'PATCH' ? 'pending' : 'disabled',
          })
        : undefined,
    );
    await screen.findByLabelText('Estado de la cuenta');
    fireEvent.change(screen.getByLabelText('Estado de la cuenta'), { target: { value: 'active' } });
    await userEvent.click(screen.getByRole('button', { name: 'Guardar usuario' }));
    await screen.findByText('Guardamos los cambios del usuario.');
    expect(screen.getByLabelText('Estado de la cuenta')).toHaveValue('pending');
    expect(screen.getByText('Correo pendiente de confirmar')).toBeInTheDocument();
  });
});
