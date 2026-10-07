import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConsumptionReportsPage, AccountConsumptionPage } from './ConsumptionReports';
import { admin, json } from '../../test/fixtures';
import { activityPage, activityReport, accountActivityReport, zeroActivity } from '../../test/consumptionActivityFixtures';
const state = vi.hoisted(() => ({ permissions: ['Content.Manage'] as string[], owner: false }));
vi.mock('../../auth/useSession', () => ({ useSession: () => ({ user: { ...admin, permissions: state.permissions, isOwner: state.owner } }) }));
function mount(account = false, override?: (path: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const path = String(input); const changed = override?.(path, init); if (changed) return changed;
    return json(path.includes('/admin/users/') ? accountActivityReport() : path.includes('/consumption/content?') ? activityPage() : activityReport());
  });
  vi.stubGlobal('fetch', fetch);
  render(<MemoryRouter initialEntries={[account ? '/admin/users/10000000-0000-0000-0000-000000000001/consumption' : '/admin/reports/consumption']}><Routes><Route path="/admin/reports/consumption" element={<ConsumptionReportsPage />} /><Route path="/admin/users/:id/consumption" element={<AccountConsumptionPage />} /></Routes></MemoryRouter>);
  return fetch;
}
function dates() { fireEvent.change(screen.getByLabelText('Desde (UTC)'), { target: { value: '2026-10-06' } }); fireEvent.change(screen.getByLabelText('Hasta (sin incluir, UTC)'), { target: { value: '2026-10-07' } }); }
async function consult() { dates(); await userEvent.click(screen.getByRole('button', { name: 'Consultar consumo' })); return screen.findByRole('heading', { name: 'Resumen del periodo' }); }
beforeEach(() => { state.permissions = ['Content.Manage']; state.owner = false; });
describe('intentional recorded-consumption reports', () => {
  it('does not query at mount or while editing dates', async () => {
    let fetch!: ReturnType<typeof mount>; await act(async () => { fetch = mount(); }); dates(); expect(fetch).not.toHaveBeenCalled(); expect(screen.queryByRole('heading', { name: 'Resumen del periodo' })).not.toBeInTheDocument();
  });
  it('loads only the two protected general snapshots after explicit submission', async () => {
    mount(); await consult(); expect(screen.getByRole('heading', { name: 'Actividad diaria' })).toBeVisible(); expect(screen.getByRole('link', { name: 'Lectura real' })).toHaveAttribute('href', '/admin/content/10000000-0000-0000-0000-000000000001'); expect(screen.getByText(/puede solaparse entre pestañas/)).toBeVisible();
    const requests = vi.mocked(fetch).mock.calls; expect(requests).toHaveLength(2); expect(requests.every(([, init]) => init?.method === 'GET' && init.cache === 'no-store')).toBe(true); expect(requests.map(([path]) => String(path))).toEqual(['/api/v1/admin/reports/catalog/consumption?from=2026-10-06&to=2026-10-07', '/api/v1/admin/reports/catalog/consumption/content?from=2026-10-06&to=2026-10-07&page=1&pageSize=20']);
  });
  it('shows numerator-based sample coverage and distinct-account interpretation separately', async () => {
    mount(); await consult(); const summary = screen.getByRole('heading', { name: 'Resumen del periodo' }).closest('section')!; expect(within(summary).getByText('50%')).toBeVisible(); expect(within(summary).getByText(/1 muestras comparables/)).toBeVisible(); expect(within(summary).getByText(/no se suman entre días u obras/)).toBeVisible(); expect(screen.getByText(/estadísticas generales por obra y día/)).toBeVisible();
  });
  it.each([{ permissions: [] }, { permissions: ['Users.Manage'] }, { permissions: ['Subscriptions.Manage'] }])('does not expose a general query without Content.Manage: %j', ({ permissions }) => {
    state.permissions = permissions; const fetch = mount(); expect(screen.getByRole('alert')).toHaveTextContent('permisos necesarios'); expect(screen.queryByRole('button', { name: 'Consultar consumo' })).not.toBeInTheDocument(); expect(fetch).not.toHaveBeenCalled();
  });
  it('does not infer reporting authority from protected ownership', () => {
    state.permissions = []; state.owner = true; const fetch = mount(); expect(screen.getByRole('alert')).toBeVisible(); expect(fetch).not.toHaveBeenCalled();
  });
  it.each([{ permissions: ['Content.Manage'] }, { permissions: ['Users.Manage'] }])('requires both permissions for account activity: %j', ({ permissions }) => {
    state.permissions = permissions; const fetch = mount(true); expect(screen.getByRole('alert')).toBeVisible(); expect(fetch).not.toHaveBeenCalled();
  });
  it('queries an already selected account once without returning a directory or personal fields', async () => {
    state.permissions = ['Users.Manage', 'Content.Manage']; const fetch = mount(true); await consult(); expect(fetch).toHaveBeenCalledOnce(); expect(String(fetch.mock.calls[0]?.[0])).toContain('/admin/users/10000000-0000-0000-0000-000000000001/consumption?'); expect(screen.getByRole('heading', { name: 'Consumo de esta cuenta' })).toBeVisible(); expect(screen.queryByText(admin.email)).not.toBeInTheDocument();
  });
  it('validates the half-open date interval before making any request', async () => {
    const fetch = mount(); dates(); fireEvent.change(screen.getByLabelText('Hasta (sin incluir, UTC)'), { target: { value: '2026-10-06' } }); await userEvent.click(screen.getByRole('button', { name: 'Consultar consumo' })); expect(screen.getByRole('alert')).toHaveTextContent('1 a 366 días'); expect(fetch).not.toHaveBeenCalled();
  });
  it('blocks duplicate submissions while the protected query is pending', async () => {
    let complete!: (response: Response) => void; const fetch = mount(false, (path) => !path.includes('/content?') ? new Promise((resolve) => { complete = resolve; }) : undefined); dates(); const button = screen.getByRole('button', { name: 'Consultar consumo' }); fireEvent.click(button); fireEvent.click(button); await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2)); expect(button).toBeDisabled(); await act(async () => { complete(json(activityReport())); }); await screen.findByRole('heading', { name: 'Resumen del periodo' });
  });
  it('keeps service failures accessible and retries only by explicit intention', async () => {
    let failed = true; const fetch = mount(false, (path) => !path.includes('/content?') && failed ? json({ code: 'unavailable', message: 'synthetic-secret-that-must-not-appear' }, 503) : undefined); dates(); await userEvent.click(screen.getByRole('button', { name: 'Consultar consumo' })); await screen.findByRole('alert'); expect(screen.getByRole('alert')).toHaveTextContent('No pudimos cargar este reporte'); expect(screen.queryByText('synthetic-secret-that-must-not-appear')).not.toBeInTheDocument(); failed = false; await userEvent.click(screen.getByRole('button', { name: 'Reintentar consulta' })); await screen.findByRole('heading', { name: 'Resumen del periodo' }); expect(fetch).toHaveBeenCalledTimes(4);
  });
  it('escapes editorial labels rather than rendering HTML in the administrative table', async () => {
    mount(false, (path) => path.includes('/content?') ? json({ ...activityPage(), items: [{ ...activityPage().items[0], title: '<script>synthetic()</script>' }] }) : undefined); await consult(); expect(screen.getByRole('link', { name: '<script>synthetic()</script>' })).toBeVisible(); expect(document.querySelector('script')).toBeNull();
  });
});

describe('explicit calendar presets', () => {
  it('fills90 dates including today without querying automatically', async () => {
    const fetch = mount(); await userEvent.click(screen.getByRole('button', { name: '90 días', exact: true }));
    const from = (screen.getByLabelText('Desde (UTC)') as HTMLInputElement).value; const to = (screen.getByLabelText('Hasta (sin incluir, UTC)') as HTMLInputElement).value;
    expect((Date.parse(to) - Date.parse(from)) / 86400000).toBe(90); expect(fetch).not.toHaveBeenCalled();
    const today = new Date().toISOString().slice(0, 10); expect(Date.parse(to)).toBe(Date.parse(today) + 86400000);
  });
  it('does not offer an annual personal-detail preset', () => {
    state.permissions = ['Content.Manage', 'Users.Manage']; mount(true); expect(screen.queryByRole('button', { name: '1 año (365 días)' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: '90 días', exact: true })).toBeEnabled();
  });
});


describe('exact explicit retry snapshots', () => {
  it('retries the newer failed date range instead of an earlier successful query', async () => {
    let failed = true;
    const fetch = mount(false, (path) => {
      if (!path.includes('from=2026-10-05')) return undefined;
      if (failed) return json({ code: 'unavailable' }, 503);
      return path.includes('/content?')
        ? json({ ...activityPage(), fromUtc: '2026-10-05T00:00:00Z' })
        : json({ ...activityReport(), fromUtc: '2026-10-05T00:00:00Z', daily: [{ dayUtc: '2026-10-05', metrics: zeroActivity() }, ...activityReport().daily] });
    });
    await consult();
    fireEvent.change(screen.getByLabelText('Desde (UTC)'), { target: { value: '2026-10-05' } });
    await userEvent.click(screen.getByRole('button', { name: 'Consultar consumo' }));
    await screen.findByRole('alert'); expect(fetch).toHaveBeenCalledTimes(4);
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar consulta' }));
    await screen.findByRole('heading', { name: 'Resumen del periodo' });
    expect(fetch).toHaveBeenCalledTimes(6);
    expect(fetch.mock.calls.slice(-2).every(([url]) => String(url).includes('from=2026-10-05&to=2026-10-07'))).toBe(true);
    expect(screen.getByLabelText('Desde (UTC)')).toHaveValue('2026-10-05');
  });
  it('retries the failed next page rather than the previous successful page', async () => {
    let failed = true;
    const fetch = mount(false, (path) => {
      if (!path.includes('/content?')) return undefined;
      if (path.includes('page=2&') && failed) return json({ code: 'unavailable' }, 503);
      return json({ ...activityPage(), total: 21, page: path.includes('page=2&') ? 2 : 1 });
    });
    await consult(); await userEvent.click(screen.getByRole('button', { name: 'Siguiente', exact: true }));
    await screen.findByRole('alert'); expect(fetch).toHaveBeenCalledTimes(4);
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar consulta' }));
    await screen.findByText('Página 2 · 21 versiones de obras');
    const pages = fetch.mock.calls.filter(([url]) => String(url).includes('/content?')).map(([url]) => new URL(String(url), 'https://qa-consumption.test').searchParams.get('page'));
    expect(pages).toEqual(['1', '2', '2']);
  });
});
