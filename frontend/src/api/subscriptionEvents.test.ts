import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './identity';
import {
  eventDate, shiftEventDate, eventIntervalDays, inclusiveEventInterval,
  subscriptionEventKeys, subscriptionEventReportFrom, subscriptionEvents,
} from './subscriptionEvents';
import { expectedEventKeys, eventInterval, eventReport, makeEventReport } from '../test/subscriptionEventFixtures';
import { json } from '../test/fixtures';

describe('Recorded subscription-event dates, aggregates and transport', () => {
  it('requires exact ASCII calendar dates, including real leap days and four-digit extremes', () => {
    for (const valid of ['0001-01-01', '2024-02-29', '2026-10-06', '9999-12-31']) expect(eventDate(valid)).toBe(true);
    for (const invalid of [null, [], '0000-01-01', '2025-02-29', '2026-04-31', '2026-13-01', '2026-00-01',
      '2026-01-00', '2026-1-01', '2026-01-01 ', ' 2026-01-01', '2026-01-01\n', '2026-01-01\r\n', '２０２６-01-01', '2026-01-01T00:00:00Z']) expect(eventDate(invalid)).toBe(false);
  });
  it('shifts in UTC across leap and year boundaries while rejecting calendar overflow or fractional shifts', () => {
    expect(shiftEventDate('2024-02-28', 1)).toBe('2024-02-29');
    expect(shiftEventDate('2024-02-29', 1)).toBe('2024-03-01');
    expect(shiftEventDate('2024-03-01', -1)).toBe('2024-02-29');
    expect(shiftEventDate('2026-12-31', 1)).toBe('2027-01-01');
    expect(shiftEventDate('9999-12-31', 1)).toBeNull();
    expect(shiftEventDate('0001-01-01', -1)).toBeNull();
    expect(shiftEventDate('bad', 1)).toBeNull();
    expect(shiftEventDate('2026-01-01', 0.5)).toBeNull();
    expect(shiftEventDate('2026-01-01', Number.MAX_SAFE_INTEGER)).toBeNull();
  });
  it('bounds exclusive periods at 1–366 days without treating a leap year as 365', () => {
    expect(eventIntervalDays({ from: '2024-02-29', to: '2024-03-01' })).toBe(1);
    expect(eventIntervalDays({ from: '2024-01-01', to: '2025-01-01' })).toBe(366);
    expect(eventIntervalDays({ from: '2025-01-01', to: '2026-01-01' })).toBe(365);
    for (const interval of [
      { from: '2026-01-01', to: '2026-01-01' }, { from: '2026-01-02', to: '2026-01-01' },
      { from: '2024-01-01', to: '2025-01-02' }, { from: '2025-02-29', to: '2025-03-01' },
    ]) expect(eventIntervalDays(interval)).toBeNull();
  });
  it('converts an included final day to an exclusive UTC boundary exactly once', () => {
    expect(inclusiveEventInterval('2024-02-29', '2024-02-29')).toEqual({ from: '2024-02-29', to: '2024-03-01' });
    expect(inclusiveEventInterval('2024-01-01', '2024-12-31')).toEqual({ from: '2024-01-01', to: '2025-01-01' });
    expect(inclusiveEventInterval('2024-01-01', '2025-01-01')).toBeNull();
    expect(inclusiveEventInterval('2026-01-02', '2026-01-01')).toBeNull();
    expect(inclusiveEventInterval('9999-12-31', '9999-12-31')).toBeNull();
  });
  it('uses the exact eleven keys from the contract, with no raw action or invented renewal bucket', () => {
    expect(subscriptionEventKeys).toEqual(expectedEventKeys);
    expect(subscriptionEventKeys).toHaveLength(11);
    expect(new Set(subscriptionEventKeys).size).toBe(11);
  });
  it('normalizes reordered complete buckets and days while preserving exact .NET UTC precision', () => {
    const value = { ...eventReport, generatedUtc: '2026-10-06T12:34:56.1234567+00:00',
      fromUtc: '2026-01-01T00:00:00.0000000+00:00', toUtc: '2026-01-03T00:00:00+00:00',
      byEvent: [...eventReport.byEvent].reverse(), days: [...eventReport.days].reverse().map((day) => ({ ...day, byEvent: [...day.byEvent].reverse() })),
    };
    expect(subscriptionEventReportFrom(value, eventInterval)).toEqual({ ...eventReport,
      generatedUtc: value.generatedUtc, fromUtc: value.fromUtc, toUtc: value.toUtc,
    });
  });
  it('keeps honest zero events for every calendar day and bucket', () => {
    const value = makeEventReport({ from: '2024-02-28', to: '2024-03-02' });
    expect(subscriptionEventReportFrom(value, { from: '2024-02-28', to: '2024-03-02' })).toEqual(value);
    expect(value.days.map((day) => day.dayUtc)).toEqual(['2024-02-28', '2024-02-29', '2024-03-01']);
    expect(value.days.every((day) => day.totalEvents === 0 && day.byEvent.length === 11)).toBe(true);
  });
  it('discards private fields at report, day and event levels instead of retaining unknown source objects', () => {
    const privateFields = { email: 'private@example.test', actorId: 'private-actor', reason: 'private-reason' };
    const value = { ...eventReport, ...privateFields,
      byEvent: eventReport.byEvent.map((row) => ({ ...row, ...privateFields })),
      days: eventReport.days.map((day) => ({ ...day, ...privateFields, byEvent: day.byEvent.map((row) => ({ ...row, ...privateFields })) })),
    };
    const result = subscriptionEventReportFrom(value, eventInterval);
    expect(result).toEqual(eventReport);
    expect(JSON.stringify(result)).not.toContain('private');
    expect(result).not.toBe(value);
  });
  it.each([
    { name: "null report", value: null },
    { name: "array report", value: [] },
    { name: "current scope", value: { ...eventReport, scope: 'current' } },
    { name: "missing generated", value: { ...eventReport, generatedUtc: null } },
    { name: "invalid calendar timestamp", value: { ...eventReport, generatedUtc: '2026-02-30T12:00:00Z' } },
    { name: "non UTC generated", value: { ...eventReport, generatedUtc: '2026-10-06T12:00:00-05:00' } },
    { name: "generated trailing LF", value: { ...eventReport, generatedUtc: eventReport.generatedUtc + '\n' } },
    { name: "generated trailing CRLF", value: { ...eventReport, generatedUtc: eventReport.generatedUtc + '\r\n' } },
    { name: "generated hour24", value: { ...eventReport, generatedUtc: '2026-10-06T24:00:00Z' } },
    { name: "generated excess precision", value: { ...eventReport, generatedUtc: '2026-10-06T12:00:00.12345678Z' } },
    { name: "start wrong date", value: { ...eventReport, fromUtc: '2025-12-31T00:00:00Z' } },
    { name: "start not midnight", value: { ...eventReport, fromUtc: '2026-01-01T01:00:00Z' } },
    { name: "start not UTC", value: { ...eventReport, fromUtc: '2026-01-01T00:00:00-05:00' } },
    { name: "start trailing LF", value: { ...eventReport, fromUtc: eventReport.fromUtc + '\n' } },
    { name: "end trailing CRLF", value: { ...eventReport, toUtc: eventReport.toUtc + '\r\n' } },
    { name: "end wrong boundary", value: { ...eventReport, toUtc: '2026-01-02T00:00:00Z' } },
    { name: "negative total", value: { ...eventReport, totalEvents: -1 } },
    { name: "fractional total", value: { ...eventReport, totalEvents: 1.5 } },
    { name: "unsafe total", value: { ...eventReport, totalEvents: Number.MAX_SAFE_INTEGER + 1 } },
    { name: "string total", value: { ...eventReport, totalEvents: '7' } },
    { name: "total mismatch", value: { ...eventReport, totalEvents: 8 } },
    { name: "missing buckets", value: { ...eventReport, byEvent: null } },
    { name: "incomplete buckets", value: { ...eventReport, byEvent: eventReport.byEvent.slice(1) } },
    { name: "duplicate bucket", value: { ...eventReport, byEvent: [eventReport.byEvent[0]!, ...eventReport.byEvent.slice(0, -1)] } },
    { name: "unknown bucket", value: { ...eventReport, byEvent: eventReport.byEvent.map((row, index) => index === 0 ? { ...row, key: 'private-action' } : row) } },
    { name: "negative bucket", value: { ...eventReport, byEvent: eventReport.byEvent.map((row, index) => index === 0 ? { ...row, count: -1 } : row) } },
    { name: "missing days", value: { ...eventReport, days: null } },
    { name: "incomplete calendar", value: { ...eventReport, days: eventReport.days.slice(1) } },
    { name: "duplicate day", value: { ...eventReport, days: [eventReport.days[0]!, eventReport.days[0]!] } },
    { name: "outside calendar", value: { ...eventReport, days: eventReport.days.map((row, index) => index === 0 ? { ...row, dayUtc: '2025-12-31' } : row) } },
    { name: "timestamp instead of date", value: { ...eventReport, days: eventReport.days.map((row, index) => index === 0 ? { ...row, dayUtc: row.dayUtc + 'T00:00:00Z' } : row) } },
    { name: "day total mismatch", value: { ...eventReport, days: eventReport.days.map((row, index) => index === 0 ? { ...row, totalEvents: 5 } : row) } },
    { name: "unsafe daily bucket", value: { ...eventReport, days: eventReport.days.map((row, index) => index === 0 ? { ...row, byEvent: row.byEvent.map((bucket, position) => position === 0 ? { ...bucket, count: Number.MAX_SAFE_INTEGER + 1 } : bucket) } : row) } },
    { name: "missing daily bucket", value: { ...eventReport, days: eventReport.days.map((row, index) => index === 0 ? { ...row, byEvent: row.byEvent.slice(1) } : row) } },
    { name: "daily global totals mismatch", value: { ...eventReport, days: makeEventReport().days } },
    { name: "cross-day bucket mismatch with equal totals", value: { ...eventReport, byEvent: eventReport.byEvent.map((row) => row.key === 'activated' ? { ...row, count: 1 } : row.key === 'reactivated' ? { ...row, count: 2 } : row) } },
  ])('rejects malformed or unreconciled $name without partial data', ({ value }) => {
    expect(() => subscriptionEventReportFrom(value, eventInterval)).toThrow(ApiError);
  });
  it('rejects an unsafe sum even when each individual bucket is a safe integer', () => {
    const value = { ...eventReport, totalEvents: Number.MAX_SAFE_INTEGER,
      byEvent: eventReport.byEvent.map((row, index) => ({ ...row, count: index === 0 ? Number.MAX_SAFE_INTEGER : index === 1 ? 1 : 0 })),
    };
    expect(() => subscriptionEventReportFrom(value, eventInterval)).toThrow(ApiError);
  });
  it('sends only whitelisted date parameters in a same-origin no-store GET with signal and no storage or body', async () => {
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return json(eventReport); });
    vi.stubGlobal('fetch', fetch);
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const controller = new AbortController();
    const polluted = { ...eventInterval, email: 'private@example.test', period: 'year' };
    expect(await subscriptionEvents(polluted, controller.signal)).toEqual(eventReport);
    expect(fetch).toHaveBeenCalledTimes(1);
    const [url, options] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/v1/admin/reports/subscriptions/events?from=2026-01-01&to=2026-01-03');
    expect(options).toMatchObject({ method: 'GET', cache: 'no-store', credentials: 'same-origin' });
    expect(options?.body).toBeUndefined();
    expect(options?.signal).toBeInstanceOf(AbortSignal);
    expect(options?.signal?.aborted).toBe(false);
    expect(options?.headers).toEqual({ Accept: 'application/json' });
    expect(storage).not.toHaveBeenCalled();
  });
  it('supports omission of the optional signal and still sends a GET without fetching CSRF', async () => {
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return json(eventReport); });
    vi.stubGlobal('fetch', fetch);
    expect(await subscriptionEvents(eventInterval)).toEqual(eventReport);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0]?.[1]?.method).toBe('GET');
  });
  it.each([
    { from: '2026-01-01', to: '2026-01-01' },
    { from: '2024-01-01', to: '2025-01-02' },
    { from: '2025-02-29', to: '2025-03-01' },
  ])('rejects an invalid interval before any request %#', async (interval) => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch);
    await expect(subscriptionEvents(interval)).rejects.toMatchObject({ code: 'invalid_interval', status: 0 });
    expect(fetch).not.toHaveBeenCalled();
  });
  it.each([
    [403, 'forbidden'], [403, 'mfa_required_for_admin'], [429, 'rate_limited'], [503, 'service_unavailable'],
  ])('propagates HTTP %i/%s in the existing ApiError shape', async (status, code) => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ code, detail: 'private database detail' }, Number(status))));
    await expect(subscriptionEvents(eventInterval)).rejects.toMatchObject({ status, code });
  });
  it('rejects malformed JSON as invalid_response and never returns partial counts', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('not-json', { status: 200 })));
    await expect(subscriptionEvents(eventInterval)).rejects.toMatchObject({ code: 'invalid_response', status: 200 });
  });
  it('sanitizes a network failure through the shared request contract', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('private infrastructure detail'); }));
    await expect(subscriptionEvents(eventInterval)).rejects.toMatchObject({ code: 'unavailable', status: 0 });
  });
  it('does not fetch when the caller signal is already aborted', async () => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); controller.abort();
    await expect(subscriptionEvents(eventInterval, controller.signal)).rejects.toMatchObject({ code: 'timeout' });
    expect(fetch).not.toHaveBeenCalled();
  });
  it('aborts an actual pending request and suppresses a late401 session-expiry event', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => { resolve = done; });
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return pending; });
    vi.stubGlobal('fetch', fetch);
    const expire = vi.fn(); window.addEventListener('acropolis:session-expired', expire);
    try {
      const controller = new AbortController();
      const result = subscriptionEvents(eventInterval, controller.signal);
      expect(fetch).toHaveBeenCalledTimes(1);
      controller.abort();
      expect(fetch.mock.calls[0]?.[1]?.signal?.aborted).toBe(true);
      resolve(json({ code: 'unauthorized' }, 401));
      await expect(result).rejects.toMatchObject({ code: 'timeout' });
      expect(expire).not.toHaveBeenCalled();
    } finally { window.removeEventListener('acropolis:session-expired', expire); }
  });
});
