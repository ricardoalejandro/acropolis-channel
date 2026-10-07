import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, clearCsrf } from './identity';
import {
  consumptionActivity,
  consumptionCapabilitiesFrom,
  consumptionSessionFrom,
  consumptionReceiptFrom,
  consumptionMetricsFrom,
  consumptionReportFrom,
  consumptionContentPageFrom,
  accountConsumptionReportFrom,
  consumptionQuery,
  utcConsumptionWindow,
  utcDate,
} from './consumptionActivity';
import {
  activityNow,
  activityVersion,
  activityContentId,
  activityMetrics,
  activityPage,
  activityReport,
  accountActivityReport,
  zeroActivity,
} from '../test/consumptionActivityFixtures';
import { json } from '../test/fixtures';
const query = { from: '2026-10-06', to: '2026-10-07' };
const session = {
  sessionId: activityContentId,
  startedUtc: activityNow,
  nextSequence: 1,
  sourceKind: 'reading',
};
const receipt = {
  sequence: 1,
  recordedUtc: activityNow,
  creditedMs: 1000,
  progressBasisPoints: 5000,
  coverageIncomplete: false,
  endedReported: false,
};
beforeEach(clearCsrf);
describe('observed consumption response contracts', () => {
  it('accepts availability disabled by the server with its retention policy', () => {
    expect(
      consumptionCapabilitiesFrom({
        recordingEnabled: false,
        detailRetentionDays: 90,
        generalRetentionDays: 365,
      }),
    ).toEqual({ recordingEnabled: false, detailRetentionDays: 90, generalRetentionDays: 365 });
  });
  it.each(
    [
      null,
      [],
      { recordingEnabled: 'yes', detailRetentionDays: 90, generalRetentionDays: 365 },
      { recordingEnabled: true, detailRetentionDays: 30, generalRetentionDays: 365 },
      {
        recordingEnabled: true,
        detailRetentionDays: 90,
        generalRetentionDays: 365,
        secret: 'synthetic',
      },
    ].map((value) => ({ value })),
  )('rejects malformed or unexpected capabilities %#', ({ value }) => {
    expect(() => consumptionCapabilitiesFrom(value)).toThrow(ApiError);
  });
  it('accepts replay-aware session and receipt metadata', () => {
    expect(consumptionSessionFrom({ ...session, nextSequence: 9 })).toMatchObject({
      nextSequence: 9,
    });
    expect(
      consumptionReceiptFrom({
        ...receipt,
        coverageIncomplete: true,
        progressBasisPoints: null,
        endedReported: true,
      }),
    ).toMatchObject({ progressBasisPoints: null, endedReported: true });
  });
  it.each([
    { ...session, sessionId: 'arbitrary' },
    { ...session, nextSequence: 0 },
    { ...session, sourceKind: 'course' },
    { ...session, startedUtc: '2026-02-30T00:00:00Z' },
    { ...session, token: 'synthetic' },
  ])('rejects invalid session metadata %#', (value) => {
    expect(() => consumptionSessionFrom(value)).toThrow(ApiError);
  });
  it.each([
    { ...receipt, sequence: 0 },
    { ...receipt, creditedMs: 15001 },
    { ...receipt, progressBasisPoints: 10001 },
    { ...receipt, coverageIncomplete: true },
    { ...receipt, recordedUtc: '2026-10-06T12:00:00-05:00' },
  ])('rejects invalid receipt contracts %#', (value) => {
    expect(() => consumptionReceiptFrom(value)).toThrow(ApiError);
  });
  it('keeps absent recent distinct-account counts and percentages nullable', () => {
    expect(consumptionMetricsFrom({ ...zeroActivity(), accountsWithActivity: null })).toMatchObject(
      { accountsWithActivity: null, meanProgressBasisPoints: null },
    );
  });
  it('truncates the sum divided by its denominator instead of averaging group means', () => {
    expect(
      consumptionMetricsFrom(
        activityMetrics({
          recordedPulses: 3,
          knownProgressSamples: 3,
          progressBasisPointsSum: 15002,
        }),
      ).meanProgressBasisPoints,
    ).toBe(5000);
  });
  it.each([
    { ...activityMetrics(), creditedMs: -1 },
    { ...activityMetrics(), starts: Number.MAX_SAFE_INTEGER + 1 },
    { ...activityMetrics(), progressBasisPointsSum: 10001 },
    { ...activityMetrics(), meanProgressBasisPoints: 9999 },
    { ...activityMetrics(), accountsWithActivity: 3 },
    { ...activityMetrics(), endedReports: 2 },
    { ...activityMetrics(), email: 'synthetic@example.test' },
  ])('rejects invalid or private metric fields %#', (value) => {
    expect(() => consumptionMetricsFrom(value)).toThrow(ApiError);
  });
  it('reconciles the additive dimensions with the daily summary', () => {
    const report = consumptionReportFrom(activityReport());
    expect(report.summary.starts).toBe(1);
    expect(report.byCategory).toHaveLength(6);
    expect(report.byKind).toHaveLength(2);
    expect(report.detailAvailableFromUtc).toBe('2026-07-09T00:00:00Z');
  });
  it('does not add daily distinct accounts to claim distinct accounts for the whole interval', () => {
    const first = activityReport();
    const second = {
      ...first,
      toUtc: '2026-10-08T00:00:00Z',
      summary: activityMetrics({
        starts: 2,
        recordedPulses: 2,
        creditedMs: 2000,
        knownProgressSamples: 2,
        progressBasisPointsSum: 10000,
      }),
      byKind: first.byKind.map((row) =>
        row.key === 'reading'
          ? {
              ...row,
              metrics: activityMetrics({
                starts: 2,
                recordedPulses: 2,
                creditedMs: 2000,
                knownProgressSamples: 2,
                progressBasisPointsSum: 10000,
              }),
            }
          : row,
      ),
      byCategory: first.byCategory.map((row) =>
        row.key === 'lecturas'
          ? {
              ...row,
              metrics: activityMetrics({
                starts: 2,
                recordedPulses: 2,
                creditedMs: 2000,
                knownProgressSamples: 2,
                progressBasisPointsSum: 10000,
              }),
            }
          : row,
      ),
      daily: [...first.daily, { dayUtc: '2026-10-07', metrics: activityMetrics() }],
    };
    expect(consumptionReportFrom(second).summary.accountsWithActivity).toBe(1);
  });
  it('accepts null distinct accounts when older general metrics remain retained', () => {
    const report = activityReport();
    report.fromUtc = '2026-07-08T00:00:00Z';
    report.toUtc = '2026-07-09T00:00:00Z';
    report.daily[0]!.dayUtc = '2026-07-08';
    report.summary.accountsWithActivity = null;
    for (const row of [...report.byKind, ...report.byCategory, ...report.daily])
      row.metrics.accountsWithActivity = null;
    expect(consumptionReportFrom(report).summary.accountsWithActivity).toBeNull();
  });
  it('rejects missing daily dates and additive mismatches', () => {
    expect(() => consumptionReportFrom({ ...activityReport(), daily: [] })).toThrow(ApiError);
    const report = activityReport();
    report.byKind[0]!.metrics.starts = 2;
    expect(() => consumptionReportFrom(report)).toThrow(ApiError);
  });
  it('rejects duplicate categories and unexpected public or personal data', () => {
    const report = activityReport();
    report.byCategory[1] = report.byCategory[0]!;
    expect(() => consumptionReportFrom(report)).toThrow(ApiError);
    expect(() =>
      consumptionReportFrom({ ...activityReport(), ownerEmail: 'synthetic@example.test' }),
    ).toThrow(ApiError);
  });
  it('accepts editorial metadata grouped by work version without account identifiers', () => {
    expect(consumptionContentPageFrom(activityPage()).items[0]).toMatchObject({
      contentVersion: activityVersion,
      title: 'Lectura real',
    });
    expect(accountConsumptionReportFrom(accountActivityReport()).scope).toBe(
      'recorded_consumption_account',
    );
  });
  it('rejects public payload leakage, duplicate work versions and incompatible source kinds', () => {
    const page = activityPage();
    expect(() =>
      consumptionContentPageFrom({
        ...page,
        items: [{ ...page.items[0], youTubeId: 'M7lc1UVf-VE' }],
      }),
    ).toThrow(ApiError);
    expect(() =>
      consumptionContentPageFrom({ ...page, items: [page.items[0], page.items[0]], total: 2 }),
    ).toThrow(ApiError);
    expect(() =>
      consumptionContentPageFrom({ ...page, items: [{ ...page.items[0], sourceKind: 'youtube' }] }),
    ).toThrow(ApiError);
  });
});
describe('strict dates and explicit consumption requests', () => {
  it.each(['2026-02-30', '٢٠٢٦-10-06', '2026-1-06', '2026-10-06\n'])(
    'rejects a non-ASCII or impossible calendar date %s',
    (value) => {
      expect(() => utcDate(value)).toThrow(ApiError);
    },
  );
  it('encodes an exact half-open date window and bounded page', () => {
    expect(consumptionQuery({ ...query, page: 2, pageSize: 20 }, true)).toBe(
      'from=2026-10-06&to=2026-10-07&page=2&pageSize=20',
    );
    expect(consumptionQuery({ from: '2024-02-29', to: '2025-03-01' })).toBe(
      'from=2024-02-29&to=2025-03-01',
    );
  });
  it.each([
    { from: query.to, to: query.from },
    { from: query.from, to: query.from },
    { from: '2026-01-01', to: '2027-01-03' },
    { ...query, page: 0 },
    { ...query, pageSize: 101 },
  ])('rejects invalid report windows or pagination %#', (value) => {
    expect(() => consumptionQuery(value, true)).toThrow(ApiError);
  });
  it('makes no report request before the explicit API method is called', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () => json(activityReport()));
    vi.stubGlobal('fetch', fetch);
    expect(fetch).not.toHaveBeenCalled();
    await consumptionActivity.report(query);
    expect(fetch).toHaveBeenCalledOnce();
    expect(fetch.mock.calls[0]?.[1]).toMatchObject({
      method: 'GET',
      cache: 'no-store',
      credentials: 'same-origin',
    });
  });
  it('keeps origin CSRF and exclusive reading payloads on both mutations', async () => {
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input);
      return path.endsWith('/csrf')
        ? json({ token: 'csrf-synthetic' })
        : path.endsWith('/sessions')
          ? json(session)
          : (void init, json(receipt));
    });
    vi.stubGlobal('fetch', fetch);
    await consumptionActivity.start('lectura/one', activityContentId, activityVersion);
    await consumptionActivity.pulse(activityContentId, {
      sequence: 1,
      intervalMs: 1000,
      activeMs: 1000,
      reading: { positionBasisPoints: 5000, exposedRanges: [{ from: 0, to: 5000 }] },
    });
    const writes = fetch.mock.calls.filter(([, options]) => options?.method === 'POST');
    expect(writes).toHaveLength(2);
    expect(writes[0]?.[0]).toBe('/api/v1/consumption/content/lectura%2Fone/sessions');
    expect(JSON.parse(String(writes[0]?.[1]?.body))).toEqual({
      visitId: activityContentId,
      contentVersion: activityVersion,
    });
    expect(writes[1]?.[1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-synthetic' });
    expect(JSON.parse(String(writes[1]?.[1]?.body))).not.toHaveProperty('accountId');
    expect(JSON.parse(String(writes[1]?.[1]?.body))).not.toHaveProperty('media');
  });
});

describe('calendar retention boundaries and honest recent counts', () => {
  it('selects exactly90 UTC dates including today across local offsets', () => {
    expect(utcConsumptionWindow(90, new Date('2026-10-06T23:59:59-05:00'))).toEqual({
      from: '2026-07-10',
      to: '2026-10-08',
      page: 1,
      pageSize: 20,
    });
    expect(utcConsumptionWindow(90, new Date(activityNow))).toEqual({
      from: '2026-07-09',
      to: '2026-10-07',
      page: 1,
      pageSize: 20,
    });
  });
  it('uses365 dates instead of inventing a366-day annual availability', () => {
    expect(utcConsumptionWindow(365, new Date('2024-03-01T12:00:00Z'))).toEqual({
      from: '2023-03-03',
      to: '2024-03-02',
      page: 1,
      pageSize: 20,
    });
  });
  it('keeps distinct counts available from the exact calendar boundary, not a rolling noon cutoff', () => {
    const report = activityReport();
    report.fromUtc = '2026-07-09T00:00:00Z';
    report.toUtc = '2026-07-10T00:00:00Z';
    report.daily[0]!.dayUtc = '2026-07-09';
    expect(consumptionReportFrom(report).summary.accountsWithActivity).toBe(1);
    expect(accountConsumptionReportFrom(accountActivityReport()).availableFromUtc).toBe(
      '2026-07-09T00:00:00Z',
    );
  });
  it('rejects a rolling boundary or a fabricated recent unavailable count', () => {
    expect(() =>
      consumptionReportFrom({
        ...activityReport(),
        detailAvailableFromUtc: '2026-07-08T12:00:00Z',
      }),
    ).toThrow(ApiError);
    const report = activityReport();
    report.summary.accountsWithActivity = null;
    expect(() => consumptionReportFrom(report)).toThrow(ApiError);
  });
  it('rejects LF timestamps and noncanonical work versions without trimming', () => {
    expect(() => consumptionSessionFrom({ ...session, startedUtc: activityNow + '\n' })).toThrow(
      ApiError,
    );
    for (const contentVersion of [activityVersion + '\n', activityVersion.toUpperCase(), 'v1']) {
      const page = activityPage();
      expect(() =>
        consumptionContentPageFrom({ ...page, items: [{ ...page.items[0], contentVersion }] }),
      ).toThrow(ApiError);
    }
  });
});
