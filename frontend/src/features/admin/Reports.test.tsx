import { act, fireEvent, isInaccessible, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderMemoryApp } from '../../test/renderApp';
import { user, json } from '../../test/fixtures';
import {
  identityReport,
  catalogReport,
  subscriptionReport,
  reportTime,
} from '../../test/reportFixtures';
import { type User } from '../../api/identity';
const permissions = ['Users.Manage', 'Content.Manage', 'Subscriptions.Manage'];
type Override = (path: string) => Response | Promise<Response> | undefined;
function mount(account: User | null, override?: Override, path = '/admin/reports') {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    void init;
    const url = String(input);
    const changed = override?.(url);
    if (changed) return changed;
    if (url.endsWith('/identity/me'))
      return account ? json(account) : json({ code: 'unauthorized' }, 401);
    if (url.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (url.endsWith('/reports/identity')) return json(identityReport);
    if (url.endsWith('/reports/catalog')) return json(catalogReport);
    if (url.endsWith('/reports/subscriptions')) return json(subscriptionReport);
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  return { ...renderMemoryApp(path), fetch };
}
function calls(fetch: ReturnType<typeof mount>['fetch'], module: string) {
  return fetch.mock.calls.filter(([url]) => String(url).endsWith('/reports/' + module));
}
function table(name: string) {
  const element = screen.getByText(name, { selector: 'caption' }).closest('table');
  if (!element) throw new Error('Missing table for caption: ' + name);
  expect(element).toHaveRole('table');
  expect(element).toHaveAccessibleName(name);
  expect(element).toBeVisible();
  return element;
}
function region(name: string) {
  const element = screen.getByText(name, { selector: 'h2' }).closest('section');
  if (!element) throw new Error('Missing section for heading: ' + name);
  expect(element).toHaveRole('region');
  expect(element).toHaveAccessibleName(name);
  expect(element).toBeVisible();
  return element;
}
async function ready() {
  await waitFor(
    () => {
      table('Usuarios por nivel institucional');
      table('Por categoría');
      table('Suscripciones por estado registrado');
    },
    { timeout: 3000 },
  );
}
beforeEach(() => {
  vi.stubGlobal('scrollTo', vi.fn());
});
describe('Operational reports with real HTTP-shaped responses', () => {
  it('shows exact current totals, all six categories, types, levels and real generation dates without personal data', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    let fetch!: ReturnType<typeof mount>['fetch'];
    await act(async () => {
      fetch = mount({ ...user, permissions }, (url) =>
        url.endsWith('/reports/identity')
          ? json({
              ...identityReport,
              email: 'private-report@example.test',
              people: [{ name: 'Secret person' }],
            })
          : undefined,
      ).fetch;
    });
    const loaded = await waitFor(
      () => {
        const lookup = (name: string) => {
          const element = screen.getByText(name, { selector: 'caption' }).closest('table');
          if (!element || element.closest('section')?.getAttribute('aria-busy') !== 'false')
            throw new Error('Report table is not ready: ' + name);
          return element;
        };
        return {
          levels: lookup('Usuarios por nivel institucional'),
          categories: lookup('Por categoría'),
          subscriptions: lookup('Suscripciones por estado registrado'),
        };
      },
      { timeout: 3000 },
    );
    for (const [name, element] of [
      ['Usuarios por nivel institucional', loaded.levels],
      ['Por categoría', loaded.categories],
      ['Suscripciones por estado registrado', loaded.subscriptions],
    ] as const) {
      expect(element).toHaveRole('table');
      expect(element).toHaveAccessibleName(name);
      expect(element).toBeVisible();
    }
    const users = region('Usuarios');
    const content = region('Contenidos');
    const subscriptions = region('Suscripciones');
    expect(within(users).getByText('7', { selector: 'dd' })).toBeVisible();
    expect(within(content).getByText('6', { selector: 'dd' })).toBeVisible();
    expect(within(subscriptions).getByText('4', { selector: 'dd' })).toBeVisible();
    expect(within(users).getByText(/Una cuenta puede tener varios niveles/)).toBeVisible();
    const categories = loaded.categories;
    expect(within(categories).getAllByRole('row')).toHaveLength(7);
    const visibleRow = (container: HTMLTableElement, label: string, name: string) => {
      const element = within(container).getByText(label, { selector: 'th' }).closest('tr');
      if (!element) throw new Error('Missing row for header: ' + label);
      expect(isInaccessible(element)).toBe(false);
      expect(element).toHaveRole('row');
      expect(element).toHaveAccessibleName(name);
      expect(element).toBeVisible();
    };
    visibleRow(categories, 'Lecturas', 'Lecturas 2 1 0');
    visibleRow(categories, 'Videos', 'Videos 0 1 0');
    visibleRow(table('Por tipo de contenido'), 'Programas', 'Programas 0 0 1');
    expect(document.querySelectorAll('time')).toHaveLength(3);
    for (const date of document.querySelectorAll('time'))
      expect(date).toHaveAttribute('datetime', reportTime);
    expect(document.querySelector('main')).not.toHaveTextContent('private-report@example.test');
    expect(document.querySelector('main')).not.toHaveTextContent('Secret person');
    expect(storage).not.toHaveBeenCalled();
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
    const navigation = screen.getByText('Reportes', { selector: 'a' });
    expect(isInaccessible(navigation)).toBe(false);
    expect(navigation).toHaveRole('link');
    expect(navigation).toHaveAccessibleName('Reportes');
    expect(navigation).toBeVisible();
    expect(navigation).toHaveAttribute('aria-current', 'page');
  }, 10000);
  it.each([
    ['Users.Manage', 'Usuarios', 'identity'],
    ['Content.Manage', 'Contenidos', 'catalog'],
    ['Subscriptions.Manage', 'Suscripciones', 'subscriptions'],
  ])(
    'loads only the authorized section for %s',
    async (permission, title, module) => {
      const { fetch } = mount({ ...user, permissions: [permission] });
      const section = await screen.findByRole('region', { name: title });
      await waitFor(() => expect(section).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
      expect(document.querySelectorAll('.report-panel')).toHaveLength(1);
      expect(calls(fetch, module)).toHaveLength(1);
      expect(
        fetch.mock.calls.filter(([url]) => String(url).includes('/admin/reports/')),
      ).toHaveLength(1);
      for (const other of ['Usuarios', 'Contenidos', 'Suscripciones'].filter(
        (item) => item !== title,
      ))
        expect(screen.queryByRole('region', { name: other })).not.toBeInTheDocument();
    },
    10000,
  );
  it.each([false, true])(
    'does not infer report permissions from owner flag %s or institutional levels',
    async (isOwner) => {
      const { fetch } = mount({
        ...user,
        isOwner,
        levels: ['Hachado', 'Instructor'],
        permissions: [],
      });
      expect(await screen.findByRole('alert')).toHaveTextContent(
        'no tiene permisos para consultar reportes',
      );
      expect(fetch.mock.calls.some(([url]) => String(url).includes('/admin/reports/'))).toBe(false);
      expect(screen.queryByRole('link', { name: 'Reportes' })).not.toBeInTheDocument();
    },
  );
  it('redirects an anonymous visit to login without issuing any report requests', async () => {
    const { fetch, router } = mount(null);
    await waitFor(
      () => {
        expect(router.state.location.pathname).toBe('/login');
        const main = document.querySelector('main');
        if (!main) throw new Error('Login main is not ready.');
        expect(main).toHaveRole('main');
        expect(within(main).getByRole('heading', { name: 'Ingresar', level: 1 })).toBeVisible();
      },
      { timeout: 3000 },
    );
    expect(fetch.mock.calls.some(([url]) => String(url).includes('/admin/reports/'))).toBe(false);
  });
  it('returns to login with an accessible session notice when a report rejects an expired session', async () => {
    mount({ ...user, permissions }, (url) =>
      url.endsWith('/reports/identity') ? json({ code: 'unauthorized' }, 401) : undefined,
    );
    expect(
      await screen.findByRole('heading', { name: 'Ingresar', level: 1 }, { timeout: 3000 }),
    ).toBeVisible();
    expect(
      screen.getByText('Tu sesión ha finalizado. Ingresa de nuevo para continuar.'),
    ).toHaveAttribute('role', 'status');
    expect(screen.queryByRole('table', { name: 'Usuarios por estado' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actualizar Usuarios' })).not.toBeInTheDocument();
  }, 10000);
  it('exposes report navigation and the home shortcut only with administrative permission', async () => {
    mount({ ...user, permissions: ['Content.Manage'] }, undefined, '/admin');
    const shortcut = await screen.findByRole('link', { name: 'Consultar reportes' });
    await userEvent.click(shortcut);
    expect(
      await screen.findByRole('table', { name: 'Por categoría' }, { timeout: 3000 }),
    ).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Reportes', level: 1 })).toHaveFocus();
  }, 10000);
  it('keeps healthy panels usable and retries only the failed section explicitly', async () => {
    let count = 0;
    const { fetch } = mount({ ...user, permissions }, (url) => {
      if (url.endsWith('/reports/catalog')) {
        count++;
        return count === 1
          ? json({ code: 'unavailable', detail: 'Private DB detail' }, 503)
          : json(catalogReport);
      }
      return undefined;
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos cargar este reporte');
    await screen.findByRole('table', { name: 'Usuarios por estado' }, { timeout: 3000 });
    await screen.findByRole('table', { name: 'Suscripciones por estado registrado' }, { timeout: 3000 });
    expect(calls(fetch, 'catalog')).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar Contenidos' }));
    await screen.findByRole('table', { name: 'Por categoría' }, { timeout: 3000 });
    expect(calls(fetch, 'catalog')).toHaveLength(2);
    expect(calls(fetch, 'identity')).toHaveLength(1);
    expect(calls(fetch, 'subscriptions')).toHaveLength(1);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Actualizar Contenidos' })).toBeEnabled();
  }, 10000);
  it.each([
    [403, 'forbidden', 'no tiene permiso'],
    [403, 'mfa_required_for_admin', 'dos pasos'],
    [429, 'rate_limited', 'Espera unos minutos'],
  ])(
    'shows HTTP %i/%s without hiding other valid reports',
    async (status, code, message) => {
      const { fetch } = mount({ ...user, permissions }, (url) =>
        url.endsWith('/reports/identity')
          ? json({ code, detail: 'private detail' }, Number(status))
          : undefined,
      );
      await waitFor(() => expect(calls(fetch, 'identity')).toHaveLength(1), { timeout: 3000 });
      const users = region('Usuarios');
      await waitFor(() => expect(users).toHaveAttribute('aria-busy', 'false'), { timeout: 3000 });
      expect(within(users).getByRole('alert')).toHaveTextContent(String(message));
      await waitFor(
        () => {
          table('Por categoría');
          table('Suscripciones por estado registrado');
        },
        { timeout: 3000 },
      );
      expect(document.querySelector('main')).not.toHaveTextContent('private detail');
    },
    10000,
  );
  it('treats malformed totals as a panel error, rather than invented or partially displayed statistics', async () => {
    mount({ ...user, permissions }, (url) =>
      url.endsWith('/reports/identity') ? json({ ...identityReport, total: -1 }) : undefined,
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos leer los datos');
    expect(screen.queryByRole('table', { name: 'Usuarios por estado' })).not.toBeInTheDocument();
    await screen.findByRole('table', { name: 'Por categoría' }, { timeout: 3000 });
  }, 10000);
  it('handles network failure and an honest zero state after explicit retry', async () => {
    let count = 0;
    const { fetch } = mount({ ...user, permissions: ['Subscriptions.Manage'] }, (url) => {
      if (!url.endsWith('/reports/subscriptions')) return undefined;
      count++;
      if (count === 1) return Promise.reject(new Error('private network detail'));
      return json({
        ...subscriptionReport,
        total: 0,
        byStatus: subscriptionReport.byStatus.map((row) => ({ ...row, count: 0 })),
      });
    });
    const subscriptions = await waitFor(
      () => {
        expect(calls(fetch, 'subscriptions')).toHaveLength(1);
        const section = region('Suscripciones');
        expect(section).toHaveAttribute('aria-busy', 'false');
        return section;
      },
      { timeout: 3000 },
    );
    expect(within(subscriptions).getByRole('alert')).toHaveTextContent('No pudimos cargar');
    await userEvent.click(
      within(subscriptions).getByRole('button', { name: 'Reintentar Suscripciones' }),
    );
    await waitFor(
      () => {
        expect(calls(fetch, 'subscriptions')).toHaveLength(2);
        expect(subscriptions).toHaveAttribute('aria-busy', 'false');
        expect(
          within(subscriptions).getByText('Todavía no hay registros de suscripciones.'),
        ).toBeVisible();
      },
      { timeout: 3000 },
    );
    expect(within(subscriptions).getByText('0', { selector: 'dd' })).toBeVisible();
    expect(within(subscriptions).getByRole('row', { name: 'Activas 0' })).toBeVisible();
    expect(calls(fetch, 'subscriptions')).toHaveLength(2);
  }, 10000);
  it('blocks duplicate refreshes while pending and applies new real totals without refreshing another panel', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let count = 0;
    const { fetch } = mount({ ...user, permissions }, (url) => {
      if (!url.endsWith('/reports/identity')) return undefined;
      count++;
      return count === 1 ? json(identityReport) : pending;
    });
    await ready();
    const button = screen.getByRole('button', { name: 'Actualizar Usuarios' });
    await userEvent.click(button);
    expect(button).toBeDisabled();
    fireEvent.click(button);
    expect(calls(fetch, 'identity')).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Actualizar Suscripciones' })).toBeEnabled();
    await act(async () => {
      resolve(
        json({
          ...identityReport,
          total: 8,
          generatedUtc: '2026-10-06T13:00:00Z',
          byStatus: [{ key: 'active', count: 5 }, ...identityReport.byStatus.slice(1)],
        }),
      );
    });
    const section = screen.getByRole('region', { name: 'Usuarios' });
    await waitFor(() => expect(within(section).getByText('8', { selector: 'dd' })).toBeVisible());
    expect(section.querySelector('time')).toHaveAttribute('datetime', '2026-10-06T13:00:00Z');
    expect(button).toBeEnabled();
    expect(calls(fetch, 'catalog')).toHaveLength(1);
    expect(calls(fetch, 'subscriptions')).toHaveLength(1);
  }, 10000);
  it('ignores a late report response after navigating away', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    const { router } = mount({ ...user, permissions: ['Users.Manage'] }, (url) =>
      url.endsWith('/reports/identity') ? pending : undefined,
    );
    await screen.findByText('Cargando reporte de usuarios…', {}, { timeout: 3000 });
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Usuarios' })).toHaveAttribute('aria-busy', 'true'),
    );
    await act(async () => {
      await router.navigate('/admin');
    });
    expect(screen.getByRole('heading', { name: 'Administración', level: 1 })).toBeVisible();
    await act(async () => {
      resolve(json(identityReport));
    });
    expect(router.state.location.pathname).toBe('/admin');
    expect(screen.queryByRole('table', { name: 'Usuarios por estado' })).not.toBeInTheDocument();
  }, 10000);
});
