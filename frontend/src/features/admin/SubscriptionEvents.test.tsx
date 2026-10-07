import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderMemoryApp } from '../../test/renderApp';
import { user, json } from '../../test/fixtures';
import { identityReport, catalogReport, subscriptionReport } from '../../test/reportFixtures';
import {
  expectedEventKeys,
  eventReport,
  makeEventReport,
} from '../../test/subscriptionEventFixtures';
import type { User } from '../../api/identity';
const path = '/admin/reports/subscription-events';
const eventPrefix = '/api/v1/admin/reports/subscriptions/events?';
const manager: User = { ...user, permissions: ['Subscriptions.Manage'] };
const labels = [
  'Primera activación',
  'Reactivación por el usuario',
  'Cancelación por el usuario',
  'Activa → cancelada',
  'Activa → suspendida',
  'Cancelada → activa',
  'Cancelada → suspendida',
  'Suspendida → activa',
  'Suspendida → cancelada',
  'Suspensión por recuperación',
  'Otros eventos registrados',
];
type Override = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(account: User | null = manager, override?: Override, location = path) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    if (url.endsWith('/identity/me'))
      return account ? json(account) : json({ code: 'unauthorized' }, 401);
    if (url.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (url.startsWith(eventPrefix)) {
      const query = new URL(url, 'https://qa.example.test').searchParams;
      const interval = { from: query.get('from')!, to: query.get('to')! };
      return json(
        interval.from === '2026-01-01' && interval.to === '2026-01-03'
          ? eventReport
          : makeEventReport(interval),
      );
    }
    if (url.endsWith('/reports/identity')) return json(identityReport);
    if (url.endsWith('/reports/catalog')) return json(catalogReport);
    if (url.endsWith('/reports/subscriptions')) return json(subscriptionReport);
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  return { ...renderMemoryApp(location), fetch };
}
async function readyMount(
  account: User | null = manager,
  override?: Override,
  location = path,
): Promise<ReturnType<typeof mount>> {
  let application: ReturnType<typeof mount> | undefined;
  await act(async () => {
    application = mount(account, override, location);
  });
  if (!application) throw new Error('Missing mounted subscription-events application');
  return application;
}
function calls(fetch: ReturnType<typeof mount>['fetch']) {
  return fetch.mock.calls.filter(([url]) => String(url).split('?')[0] === eventPrefix.slice(0, -1));
}
function period(from = '2026-01-01', through = '2026-01-02') {
  const form = screen.getByRole('form', { name: 'Período de movimientos' });
  const first = within(form).getByLabelText('Desde');
  const last = within(form).getByLabelText('Hasta (incluido)');
  fireEvent.change(first, { target: { value: from } });
  fireEvent.change(last, { target: { value: through } });
  return { form, first, last };
}
async function consult(form: HTMLElement) {
  await act(async () => {
    await userEvent.click(within(form).getByRole('button', { name: 'Consultar período' }));
  });
}
function results() {
  const section = screen.getByText('Cambios registrados', { selector: 'h2' }).closest('section');
  if (!section) throw new Error('Missing recorded-events result region');
  expect(section).toHaveRole('region');
  expect(section).toHaveAccessibleName('Cambios registrados');
  expect(section).toBeVisible();
  return section;
}
function table(section: HTMLElement, caption: string) {
  const element = within(section).getByText(caption, { selector: 'caption' }).closest('table');
  if (!element) throw new Error('Missing recorded-events table');
  expect(element).toHaveRole('table');
  expect(element).toHaveAccessibleName(caption);
  expect(element).toBeVisible();
  return element;
}
beforeEach(() => {
  vi.stubGlobal('scrollTo', vi.fn());
});

describe('Explicit administrative recorded-subscription-events consultation', () => {
  it('loads no event data automatically and explains the included UTC dates before any query', async () => {
    const { fetch } = await readyMount();
    expect(
      screen.getByRole('heading', { level: 1, name: 'Movimientos de suscripción' }),
    ).toBeVisible();
    const form = screen.getByRole('form', { name: 'Período de movimientos' });
    expect(within(form).getByText(/Se incluyen ambos días seleccionados/)).toHaveTextContent('UTC');
    expect(within(form).getByLabelText('Desde')).toHaveAttribute('type', 'date');
    expect(within(form).getByLabelText('Hasta (incluido)')).toHaveAttribute(
      'aria-describedby',
      'event-period-help',
    );
    expect(within(form).getByRole('button', { name: 'Consultar período' })).toBeEnabled();
    expect(calls(fetch)).toHaveLength(0);
    expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
    expect(
      screen.getByText(
        'Selecciona un período y pulsa «Consultar período» para ver sus movimientos.',
      ),
    ).toBeVisible();
  }, 10000);
  it('shows all eleven truthful groups, totals and daily dates without personal data or writes', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const { fetch } = await readyMount(manager, (url) =>
      url.startsWith(eventPrefix)
        ? json({
            ...eventReport,
            email: 'private@example.test',
            actorId: 'private-actor',
            byEvent: eventReport.byEvent.map((row) => ({ ...row, reason: 'private-reason' })),
            days: eventReport.days.map((day) => ({ ...day, userId: 'private-user' })),
          })
        : undefined,
    );
    const { form } = period();
    await consult(form);
    const section = results();
    expect(within(section).getByText('7', { selector: 'dd' })).toBeVisible();
    expect(section).toHaveTextContent(
      'no equivalen a personas únicas, suscripciones activas ni renovaciones',
    );
    const ordinary = table(section, 'Activaciones y otros movimientos');
    const administrative = table(section, 'Cambios administrativos de estado');
    for (const [index, key] of expectedEventKeys.entries()) {
      const scope = key.startsWith('updated_') ? administrative : ordinary;
      const label = labels[index]!;
      const row = within(scope).getByText(label, { selector: 'th' }).closest('tr');
      const count = eventReport.byEvent.find((bucket) => bucket.key === key)!.count;
      expect(row).toHaveRole('row');
      expect(row).toHaveAccessibleName(label + ' ' + count);
      expect(row).toBeVisible();
    }
    await userEvent.click(
      within(section).getByText('Ver detalle diario (2 días)', { selector: 'summary' }),
    );
    const daily = table(section, 'Eventos registrados por día UTC');
    expect(within(daily).getAllByRole('row')).toHaveLength(3);
    for (const day of eventReport.days) {
      const time = daily.querySelector('time[datetime="' + day.dayUtc + '"]');
      expect(time).toBeVisible();
      expect(time?.closest('tr')?.querySelector('td')).toHaveTextContent(String(day.totalEvents));
    }
    expect(section.querySelector('.event-updated time')).toHaveAttribute(
      'datetime',
      eventReport.generatedUtc,
    );
    expect(section).toHaveTextContent('UTC. Sólo se cuentan los eventos disponibles');
    expect(document.querySelector('main')).not.toHaveTextContent('private');
    expect(calls(fetch)).toHaveLength(1);
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
    expect(calls(fetch)[0]?.[1]?.cache).toBe('no-store');
    expect(storage).not.toHaveBeenCalled();
  }, 10000);
  it('shows an honest zero state with all eleven groups and two zero calendar days', async () => {
    const { fetch } = await readyMount(manager, (url) =>
      url.startsWith(eventPrefix) ? json(makeEventReport()) : undefined,
    );
    const { form } = period();
    await consult(form);
    const section = results();
    expect(within(section).getByText('0', { selector: 'dd' })).toBeVisible();
    const empty = within(section).getByText('No hay movimientos registrados en este período.');
    expect(empty).toHaveAttribute('role', 'status');
    expect(empty).toBeVisible();
    for (const caption of [
      'Activaciones y otros movimientos',
      'Cambios administrativos de estado',
    ]) {
      const rows = within(table(section, caption)).getAllByRole('row').slice(1);
      expect(rows.every((row) => row.querySelector('td')?.textContent === '0')).toBe(true);
    }
    await userEvent.click(
      within(section).getByText('Ver detalle diario (2 días)', { selector: 'summary' }),
    );
    expect(
      table(section, 'Eventos registrados por día UTC').querySelectorAll('tbody tr'),
    ).toHaveLength(2);
    expect(calls(fetch)).toHaveLength(1);
  }, 10000);
  it('includes a selected leap day by sending the next UTC date as the exclusive endpoint', async () => {
    const { fetch } = await readyMount();
    const { form } = period('2024-02-29', '2024-02-29');
    await consult(form);
    expect(calls(fetch)[0]?.[0]).toBe(eventPrefix + 'from=2024-02-29&to=2024-03-01');
    const section = results();
    await userEvent.click(
      within(section).getByText('Ver detalle diario (1 día)', { selector: 'summary' }),
    );
    expect(table(section, 'Eventos registrados por día UTC').querySelector('time')).toHaveAttribute(
      'datetime',
      '2024-02-29',
    );
  }, 10000);
  it.each([
    ['', '2026-01-02'],
    ['2026-01-02', '2026-01-01'],
    ['2024-01-01', '2025-01-01'],
  ])(
    'rejects an empty, reversed or 367-day included range %s to %s without a GET',
    async (from, through) => {
      const { fetch } = await readyMount();
      const { form } = period(from, through);
      await consult(form);
      expect(within(form).getByRole('alert')).toHaveTextContent(
        'período válido de entre 1 y 366 días',
      );
      expect(calls(fetch)).toHaveLength(0);
      expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
    },
    10000,
  );
  it('guards duplicate submit while the real fetch promise is pending and leaves only an explicit query', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    const { fetch } = await readyMount(manager, (url) =>
      url.startsWith(eventPrefix) ? pending : undefined,
    );
    const { form, first, last } = period();
    const button = within(form).getByRole('button', { name: 'Consultar período' });
    await userEvent.click(button);
    expect(button).toBeDisabled();
    expect(first).toBeDisabled();
    expect(last).toBeDisabled();
    expect(screen.getByText('Consultando los movimientos del período…')).toHaveAttribute(
      'role',
      'status',
    );
    fireEvent.submit(form);
    fireEvent.submit(form);
    await userEvent.keyboard('{Enter}');
    expect(calls(fetch)).toHaveLength(1);
    await act(async () => {
      resolve(json(eventReport));
    });
    results();
    expect(button).toBeEnabled();
    expect(first).toBeEnabled();
    expect(last).toBeEnabled();
    expect(calls(fetch)).toHaveLength(1);
  }, 10000);
  it.each([
    [403, 'forbidden', 'no tiene permiso'],
    [403, 'mfa_required_for_admin', 'dos pasos'],
    [429, 'rate_limited', 'Espera unos minutos'],
    [503, 'service_unavailable', 'No pudimos cargar'],
    [0, 'network', 'No pudimos cargar'],
  ])(
    'shows sanitized %i/%s and retries only after another explicit action',
    async (status, code, message) => {
      let attempts = 0;
      const { fetch } = await readyMount(manager, (url) => {
        if (!url.startsWith(eventPrefix)) return undefined;
        attempts++;
        if (attempts > 1) return json(makeEventReport());
        return status === 0
          ? Promise.reject(new Error('private infrastructure'))
          : json({ code, detail: 'private infrastructure' }, Number(status));
      });
      const { form } = period();
      await consult(form);
      expect(within(form).getByRole('alert')).toHaveTextContent(String(message));
      expect(document.querySelector('main')).not.toHaveTextContent('private infrastructure');
      expect(calls(fetch)).toHaveLength(1);
      expect(within(form).getByRole('button', { name: 'Consultar período' })).toBeEnabled();
      await consult(form);
      expect(within(results()).getByText('0', { selector: 'dd' })).toBeVisible();
      expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
      expect(calls(fetch)).toHaveLength(2);
    },
    10000,
  );
  it('treats an unreconciled response as an accessible error without partial or invented results', async () => {
    const { fetch } = await readyMount(manager, (url) =>
      url.startsWith(eventPrefix) ? json({ ...eventReport, totalEvents: 8 }) : undefined,
    );
    const { form } = period();
    await consult(form);
    expect(within(form).getByRole('alert')).toHaveTextContent('No pudimos leer los datos');
    expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
    expect(calls(fetch)).toHaveLength(1);
  }, 10000);
  it('expires an actual401 through the existing session notice and protected login route', async () => {
    const { fetch } = await readyMount(manager, (url) =>
      url.startsWith(eventPrefix) ? json({ code: 'unauthorized' }, 401) : undefined,
    );
    const { form } = period();
    await consult(form);
    expect(screen.getByRole('heading', { level: 1, name: 'Ingresar' })).toBeVisible();
    expect(
      screen.getByText('Tu sesión ha finalizado. Ingresa de nuevo para continuar.'),
    ).toHaveAttribute('role', 'status');
    expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
    expect(calls(fetch)).toHaveLength(1);
  }, 10000);
  it('clears obsolete displayed data when a date changes without automatically querying the edited range', async () => {
    const { fetch } = await readyMount();
    const { form, last } = period();
    await consult(form);
    results();
    fireEvent.change(last, { target: { value: '2026-01-03' } });
    expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
    expect(calls(fetch)).toHaveLength(1);
    await consult(form);
    expect(calls(fetch)).toHaveLength(2);
    expect(calls(fetch)[1]?.[0]).toBe(eventPrefix + 'from=2026-01-01&to=2026-01-04');
    expect(
      within(results()).getByText('Ver detalle diario (3 días)', { selector: 'summary' }),
    ).toBeVisible();
  }, 10000);
  it('aborts on navigation and ignores a late401 result instead of expiring the unrelated destination', async () => {
    let resolve: (value: Response) => void = () => {};
    let signal: AbortSignal | undefined;
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    const { fetch, router } = await readyMount(manager, (url, init) => {
      if (!url.startsWith(eventPrefix)) return undefined;
      signal = init?.signal ?? undefined;
      return pending;
    });
    const { form } = period();
    await userEvent.click(within(form).getByRole('button', { name: 'Consultar período' }));
    expect(calls(fetch)).toHaveLength(1);
    await act(async () => {
      await router.navigate('/admin');
    });
    expect(signal?.aborted).toBe(true);
    await act(async () => {
      resolve(json({ code: 'unauthorized' }, 401));
    });
    expect(router.state.location.pathname).toBe('/admin');
    expect(screen.getByRole('heading', { level: 1, name: 'Administración' })).toBeVisible();
    expect(
      screen.queryByText('Tu sesión ha finalizado. Ingresa de nuevo para continuar.'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Cambios registrados', { selector: 'h2' })).not.toBeInTheDocument();
  }, 10000);
  it.each([false, true])(
    'does not infer Subscriptions.Manage from owner=%s or institutional levels',
    async (isOwner) => {
      const { fetch } = await readyMount({
        ...user,
        isOwner,
        permissions: ['Users.Manage'],
        levels: ['Instructor', 'Hachado'],
      });
      expect(
        screen.getByRole('heading', { name: 'Este espacio requiere autorización.' }),
      ).toBeVisible();
      expect(
        screen.queryByRole('form', { name: 'Período de movimientos' }),
      ).not.toBeInTheDocument();
      expect(calls(fetch)).toHaveLength(0);
    },
    10000,
  );
  it('redirects an anonymous route to login without issuing an event query', async () => {
    const { fetch } = await readyMount(null);
    expect(screen.getByRole('heading', { level: 1, name: 'Ingresar' })).toBeVisible();
    expect(calls(fetch)).toHaveLength(0);
  }, 10000);
  it.each(['Subscriptions.Manage', 'Users.Manage', 'Content.Manage'])(
    'shows the movements link only for the independent %s capability',
    async (permission) => {
      const { fetch } = await readyMount(
        { ...user, permissions: [permission] },
        undefined,
        '/admin/reports',
      );
      if (permission === 'Subscriptions.Manage') {
        const link = screen.getByRole('link', { name: 'Ver movimientos de suscripción →' });
        expect(link).toHaveAttribute('href', path);
        expect(link).toBeVisible();
        await act(async () => {
          await userEvent.click(link);
        });
        expect(
          screen.getByRole('heading', { level: 1, name: 'Movimientos de suscripción' }),
        ).toBeVisible();
      } else
        expect(
          screen.queryByRole('link', { name: 'Ver movimientos de suscripción →' }),
        ).not.toBeInTheDocument();
      expect(calls(fetch)).toHaveLength(0);
    },
    10000,
  );
});
