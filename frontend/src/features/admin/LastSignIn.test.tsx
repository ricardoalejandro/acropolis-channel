import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderMemoryApp } from '../../test/renderApp';
import { admin, user, json } from '../../test/fixtures';
const first = '10000000-0000-0000-0000-000000000001';
const second = '10000000-0000-0000-0000-000000000002';
const instant = '2026-10-06T12:34:56.1234567Z';
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(
  path = '/admin/users/' + first,
  override?: Override,
  permissions = ['Users.Manage'],
) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    if (url.endsWith('/identity/me')) return json({ ...admin, permissions });
    if (url.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (url.endsWith('/access')) return json({ lastSignInUtc: instant });
    if (url.includes('/admin/users?'))
      return json({ items: [{ ...user, id: first }], total: 1, page: 1, pageSize: 20 });
    if (url.endsWith('/admin/users/' + first)) return json({ ...user, id: first });
    if (url.endsWith('/admin/users/' + second))
      return json({ ...user, id: second, displayName: 'Segunda persona' });
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  return { ...renderMemoryApp(path), fetch };
}
function requests(fetch: ReturnType<typeof mount>['fetch']) {
  return fetch.mock.calls.filter(([url]) => String(url).endsWith('/access'));
}
async function panel() {
  const heading = await screen.findByText(
    'Último ingreso registrado',
    { selector: 'h3' },
    { timeout: 3000 },
  );
  const section = heading.closest('section');
  if (!section) throw new Error('Missing last-sign-in section');
  expect(section).toHaveRole('region');
  expect(section).toHaveAccessibleName('Último ingreso registrado');
  return section;
}
beforeEach(() => {
  vi.stubGlobal('scrollTo', vi.fn());
  vi.spyOn(window, 'confirm').mockReturnValue(false);
});
describe('Administrative last authenticated-session record', () => {
  it('shows only the actual returned timestamp with accurate help and a single GET for the authorized detail', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const { fetch } = mount();
    const section = await panel();
    await waitFor(() => expect(section).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
    expect(section.querySelector('time')).toHaveAttribute('datetime', instant);
    expect(within(section).getByText('Hora del Perú.')).toBeVisible();
    expect(section).toHaveTextContent('no corresponde a la última visita ni reproducción');
    expect(requests(fetch)).toHaveLength(1);
    expect(requests(fetch)[0]?.[1]?.method).toBe('GET');
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
    expect(storage).not.toHaveBeenCalled();
  }, 10000);
  it('treats null honestly without a date or an inference that the user never signed in', async () => {
    mount(undefined, (url) =>
      url.endsWith('/access') ? json({ lastSignInUtc: null }) : undefined,
    );
    const section = await panel();
    expect(await within(section).findByText('Sin registro disponible')).toBeVisible();
    expect(section.querySelector('time')).toBeNull();
    expect(section).not.toHaveTextContent(/nunca|jamás/i);
  }, 10000);
  it.each([
    [403, 'forbidden', 'no tiene permiso'],
    [403, 'mfa_required_for_admin', 'dos pasos'],
    [503, 'service_unavailable', 'No pudimos consultar'],
    [200, 'malformed', 'No pudimos leer'],
  ])(
    'shows an accessible sanitized error for %i/%s',
    async (status, code, message) => {
      mount(undefined, (url) =>
        url.endsWith('/access')
          ? json(
              code === 'malformed'
                ? { lastSignInUtc: 'bad', email: 'private@example.test' }
                : { code, detail: 'private details' },
              Number(status),
            )
          : undefined,
      );
      const section = await panel();
      await waitFor(() => expect(section).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
      expect(within(section).getByRole('alert')).toHaveTextContent(String(message));
      expect(section).not.toHaveTextContent('private');
      expect(
        within(section).getByRole('button', { name: 'Reintentar último ingreso' }),
      ).toBeEnabled();
    },
    10000,
  );
  it('retries by keyboard without losing edits, generating a write or duplicating an in-flight request', async () => {
    let count = 0;
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    const { fetch } = mount(undefined, (url) => {
      if (!url.endsWith('/access')) return undefined;
      count++;
      return count === 1 ? json({ code: 'unavailable' }, 503) : pending;
    });
    const section = await panel();
    await waitFor(() => expect(section).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
    const name = screen.getByLabelText('Nombre visible');
    fireEvent.change(name, { target: { value: 'Edición conservada' } });
    const retry = within(section).getByRole('button', { name: 'Reintentar último ingreso' });
    retry.focus();
    expect(retry).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(requests(fetch)).toHaveLength(2));
    fireEvent.click(retry);
    expect(requests(fetch)).toHaveLength(2);
    expect(section).toHaveAttribute('aria-busy', 'true');
    expect(retry).toHaveFocus();
    expect(retry).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(requests(fetch)).toHaveLength(2);
    await act(async () => {
      resolve(json({ lastSignInUtc: instant }));
    });
    await waitFor(
      () => expect(section.querySelector('time')).toHaveAttribute('datetime', instant),
      { timeout: 3000 },
    );
    expect(retry).toHaveFocus();
    expect(retry).toHaveAttribute('aria-disabled', 'false');
    expect(retry).toHaveTextContent('Actualizar último ingreso');
    expect(name).toHaveValue('Edición conservada');
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
  }, 10000);
  it('keeps keyboard focus through a failed retry and does not steal focus moved to an editor field', async () => {
    let count = 0;
    let resolveError: (value: Response) => void = () => {};
    let resolveSuccess: (value: Response) => void = () => {};
    const failed = new Promise<Response>((done) => {
      resolveError = done;
    });
    const successful = new Promise<Response>((done) => {
      resolveSuccess = done;
    });
    const { fetch } = mount(undefined, (url) => {
      if (!url.endsWith('/access')) return undefined;
      count++;
      return count === 1
        ? json({ code: 'service_unavailable' }, 503)
        : count === 2
          ? failed
          : successful;
    });
    const section = await panel();
    await waitFor(() => expect(section).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
    const retry = within(section).getByRole('button', { name: 'Reintentar último ingreso' });
    retry.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(requests(fetch)).toHaveLength(2));
    expect(retry).toHaveFocus();
    expect(retry).toHaveAttribute('aria-disabled', 'true');
    await act(async () => {
      resolveError(json({ code: 'service_unavailable' }, 503));
    });
    await waitFor(() => expect(retry).toHaveAttribute('aria-disabled', 'false'), { timeout: 3000 });
    expect(retry).toHaveFocus();
    expect(within(section).getByRole('alert')).toHaveTextContent('No pudimos consultar');
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(requests(fetch)).toHaveLength(3));
    const name = screen.getByLabelText('Nombre visible');
    name.focus();
    expect(name).toHaveFocus();
    await act(async () => {
      resolveSuccess(json({ lastSignInUtc: instant }));
    });
    await waitFor(
      () => expect(section.querySelector('time')).toHaveAttribute('datetime', instant),
      { timeout: 3000 },
    );
    expect(name).toHaveFocus();
    expect(retry).not.toHaveFocus();
    expect(retry).toHaveAttribute('aria-disabled', 'false');
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
  }, 10000);
  it('expires an actual 401 into the existing login and accessible session notice', async () => {
    await act(async () => {
      mount(undefined, (url) =>
        url.endsWith('/access') ? json({ code: 'unauthorized' }, 401) : undefined,
      );
    });
    expect(
      await screen.findByRole('heading', { name: 'Ingresar', level: 1 }, { timeout: 3000 }),
    ).toBeVisible();
    expect(
      screen.getByText('Tu sesión ha finalizado. Ingresa de nuevo para continuar.'),
    ).toHaveAttribute('role', 'status');
    expect(screen.queryByText('Último ingreso registrado')).not.toBeInTheDocument();
  }, 10000);
  it.each([false, true])(
    'makes no access request on a forbidden detail even when owner=%s',
    async (owner) => {
      const { fetch } = mount(
        undefined,
        (url) =>
          url.endsWith('/identity/me')
            ? json({ ...admin, isOwner: owner, permissions: [] })
            : undefined,
        [],
      );
      expect(
        await screen.findByRole('heading', { name: 'Este espacio requiere autorización.' }),
      ).toBeVisible();
      expect(requests(fetch)).toHaveLength(0);
    },
  );
  it('does not fetch access records for the list or introduce N+1 requests', async () => {
    const { fetch } = mount('/admin/users');
    await screen.findByRole('link', { name: 'Ver usuario ' + user.displayName }, { timeout: 3000 });
    expect(requests(fetch)).toHaveLength(0);
  }, 10000);
  it('aborts the old detail and ignores its late timestamp after the ID changes', async () => {
    let resolve: (value: Response) => void = () => {};
    let signal: AbortSignal | undefined;
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    const { router, fetch } = mount(undefined, (url, init) => {
      if (url.endsWith('/users/' + first + '/access')) {
        signal = init?.signal as AbortSignal;
        return pending;
      }
      return url.endsWith('/users/' + second + '/access')
        ? json({ lastSignInUtc: null })
        : undefined;
    });
    await panel();
    await waitFor(() => expect(requests(fetch)).toHaveLength(1));
    await act(async () => {
      await router.navigate('/admin/users/' + second);
    });
    const section = await panel();
    expect(await within(section).findByText('Sin registro disponible')).toBeVisible();
    expect(signal?.aborted).toBe(true);
    await act(async () => {
      resolve(json({ lastSignInUtc: instant }));
    });
    expect(section.querySelector('time')).toBeNull();
    expect(router.state.location.pathname).toBe('/admin/users/' + second);
    expect(requests(fetch)).toHaveLength(2);
  }, 10000);
});
