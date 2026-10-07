import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './identity';
import {
  reports,
  identityReportFrom,
  catalogReportFrom,
  subscriptionReportFrom,
  reportMessage,
} from './reports';
import { identityReport, catalogReport, subscriptionReport } from '../test/reportFixtures';
import { json } from '../test/fixtures';
describe('Current operational report contracts', () => {
  it('accepts real aggregate counts, overlapping levels and complete zero buckets without personal data', () => {
    const pii = {
      email: 'private@example.test',
      users: [{ name: 'Private person' }],
      rate: 99,
      period: 'month',
    };
    expect(
      identityReportFrom({
        ...identityReport,
        ...pii,
        byStatus: identityReport.byStatus.map((row) => ({ ...row, ...pii })),
      }),
    ).toEqual(identityReport);
    expect(catalogReportFrom({ ...catalogReport, ...pii })).toEqual(catalogReport);
    expect(subscriptionReportFrom({ ...subscriptionReport, ...pii })).toEqual(subscriptionReport);
    expect(identityReport.byLevel.reduce((sum, row) => sum + row.count, 0)).toBeGreaterThan(
      identityReport.total,
    );
    expect(catalogReportFrom(catalogReport).byCategoryAndStatus).toHaveLength(18);
    expect(catalogReportFrom(catalogReport).byKindAndStatus).toHaveLength(9);
  });
  it('preserves empty-state counts and accepts UTC offset and subsecond precision from .NET', () => {
    expect(
      subscriptionReportFrom({
        ...subscriptionReport,
        total: 0,
        generatedUtc: '2026-10-06T12:00:00.1234567+00:00',
        byStatus: subscriptionReport.byStatus.map((row) => ({ ...row, count: 0 })),
      }).total,
    ).toBe(0);
  });
  it.each([
    null,
    [],
    { ...subscriptionReport, scope: 'monthly' },
    { ...subscriptionReport, generatedUtc: 'bad' },
    { ...subscriptionReport, generatedUtc: '2026-10-06T12:00:00' },
    { ...subscriptionReport, generatedUtc: '2026-10-06T12:00:00-05:00' },
    { ...subscriptionReport, generatedUtc: '2026-99-06T12:00:00Z' },
    { ...subscriptionReport, total: -1 },
    { ...subscriptionReport, total: 0.5 },
    { ...subscriptionReport, total: Number.MAX_SAFE_INTEGER + 1 },
    { ...subscriptionReport, total: '4' },
    { ...subscriptionReport, total: 5 },
    { ...subscriptionReport, byStatus: null },
    { ...subscriptionReport, byStatus: [] },
    {
      ...subscriptionReport,
      byStatus: [
        subscriptionReport.byStatus[0],
        subscriptionReport.byStatus[0],
        subscriptionReport.byStatus[2],
      ],
    },
    {
      ...subscriptionReport,
      byStatus: subscriptionReport.byStatus.map((row) => ({ ...row, count: -1 })),
    },
    {
      ...subscriptionReport,
      byStatus: subscriptionReport.byStatus.map((row) => ({ ...row, count: '1' })),
    },
    {
      ...subscriptionReport,
      byStatus: subscriptionReport.byStatus.map((row) => ({
        ...row,
        count: Number.MAX_SAFE_INTEGER + 1,
      })),
    },
  ])('rejects malformed, ambiguous or inconsistent current report %#', (value) => {
    expect(() => subscriptionReportFrom(value)).toThrow(ApiError);
  });
  it('rejects missing, duplicate, unknown or impossible institutional level counts', () => {
    for (const byLevel of [
      null,
      [],
      identityReport.byLevel.slice(1),
      identityReport.byLevel.map((row) => ({ ...row, key: 'Unknown' })),
      identityReport.byLevel.map((row) => ({ ...row, count: 8 })),
    ])
      expect(() => identityReportFrom({ ...identityReport, byLevel })).toThrow(ApiError);
    expect(() => identityReportFrom(null)).toThrow(ApiError);
  });
  it('rejects incomplete, duplicate, negative and inconsistent category/type matrices', () => {
    for (const field of ['byCategoryAndStatus', 'byKindAndStatus'] as const) {
      const rows = catalogReport[field];
      for (const value of [
        null,
        rows.slice(1),
        rows.map(() => rows[0]),
        rows.map((row) => ({ ...row, count: -1 })),
        rows.map((row) => ({ ...row, count: 0 })),
      ])
        expect(() => catalogReportFrom({ ...catalogReport, [field]: value })).toThrow(ApiError);
    }
    expect(() => catalogReportFrom(null)).toThrow(ApiError);
  });
  it('rejects course/program totals that cannot fit in the Cursos category for the same status', () => {
    const byCategoryAndStatus = catalogReport.byCategoryAndStatus.map((row) =>
      row.status !== 'published'
        ? row
        : row.category === 'cursos'
          ? { ...row, count: 0 }
          : row.category === 'videos'
            ? { ...row, count: 2 }
            : row,
    );
    expect(() => catalogReportFrom({ ...catalogReport, byCategoryAndStatus })).toThrow(ApiError);
  });
  it('calls only the three exact GET routes without filters, bodies or browser storage', async () => {
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      void init;
      return json(
        String(input).endsWith('/identity')
          ? identityReport
          : String(input).endsWith('/catalog')
            ? catalogReport
            : subscriptionReport,
      );
    });
    vi.stubGlobal('fetch', fetch);
    const local = vi.spyOn(Storage.prototype, 'setItem');
    await Promise.all([reports.identity(), reports.catalog(), reports.subscriptions()]);
    expect(fetch.mock.calls.map(([url]) => url)).toEqual([
      '/api/v1/admin/reports/identity',
      '/api/v1/admin/reports/catalog',
      '/api/v1/admin/reports/subscriptions',
    ]);
    for (const call of fetch.mock.calls) {
      const init = call[1]!;
      expect(init.method).toBe('GET');
      expect(init.body).toBeUndefined();
      expect(init.credentials).toBe('same-origin');
    }
    expect(local).not.toHaveBeenCalled();
  });
  it.each([
    [403, 'forbidden', 'no tiene permiso'],
    [403, 'mfa_required_for_admin', 'dos pasos'],
    [401, 'unauthorized', 'Ingresa de nuevo'],
    [429, 'rate_limited', 'Espera unos minutos'],
    [0, 'invalid_response', 'leer los datos'],
    [503, 'private-detail', 'cargar este reporte'],
  ])('provides a safe report error for HTTP %i and %s', (status, code, message) => {
    expect(reportMessage(new ApiError(Number(status), String(code)))).toContain(String(message));
    expect(reportMessage(new Error('SMTP password is private'))).not.toContain('password');
  });
  it('propagates denied/unavailable HTTP responses and network failures without partial data', async () => {
    for (const status of [403, 429, 503]) {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => json({ code: 'forbidden', detail: 'private' }, status)),
      );
      await expect(reports.catalog()).rejects.toMatchObject({ status });
    }
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new Error('private network detail');
      }),
    );
    await expect(reports.identity()).rejects.toMatchObject({ code: 'unavailable' });
  });
});
