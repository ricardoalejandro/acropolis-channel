import { ApiError, request } from './identity';
export const subscriptionEventKeys = [
  'activated',
  'reactivated',
  'cancelled',
  'updated_active_cancelled',
  'updated_active_suspended',
  'updated_cancelled_active',
  'updated_cancelled_suspended',
  'updated_suspended_active',
  'updated_suspended_cancelled',
  'recovery_suspended',
  'unclassified',
] as const;
export type SubscriptionEventKey = (typeof subscriptionEventKeys)[number];
export type EventCount = { key: SubscriptionEventKey; count: number };
export type EventDay = { dayUtc: string; totalEvents: number; byEvent: EventCount[] };
export type SubscriptionEventReport = {
  scope: 'recorded_events';
  generatedUtc: string;
  fromUtc: string;
  toUtc: string;
  totalEvents: number;
  byEvent: EventCount[];
  days: EventDay[];
};
export type EventInterval = { from: string; to: string };
const dayMs = 86400000;
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function invalid(): never {
  throw new ApiError(0, 'invalid_response');
}
export function eventDate(value: unknown): value is string {
  if (
    typeof value !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}(?![\s\S])/.test(value) ||
    value.startsWith('0000-')
  )
    return false;
  const instant = Date.parse(value + 'T00:00:00Z');
  return Number.isFinite(instant) && new Date(instant).toISOString().slice(0, 10) === value;
}
export function shiftEventDate(value: string, days: number): string | null {
  if (!eventDate(value) || !Number.isSafeInteger(days)) return null;
  const instant = Date.parse(value + 'T00:00:00Z') + days * dayMs;
  if (!Number.isFinite(instant) || !Number.isFinite(new Date(instant).getTime())) return null;
  const result = new Date(instant).toISOString().slice(0, 10);
  return eventDate(result) ? result : null;
}
export function eventIntervalDays(interval: EventInterval): number | null {
  if (!eventDate(interval.from) || !eventDate(interval.to)) return null;
  const days =
    (Date.parse(interval.to + 'T00:00:00Z') - Date.parse(interval.from + 'T00:00:00Z')) / dayMs;
  return Number.isInteger(days) && days >= 1 && days <= 366 ? days : null;
}
export function inclusiveEventInterval(from: string, through: string): EventInterval | null {
  const to = shiftEventDate(through, 1);
  if (!to) return null;
  const interval = { from, to };
  return eventIntervalDays(interval) === null ? null : interval;
}
function count(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) invalid();
  return value;
}
function sum(values: readonly number[]): number {
  const result = values.reduce((total, value) => total + value, 0);
  if (!Number.isSafeInteger(result)) invalid();
  return result;
}
function counts(value: unknown, total: number): EventCount[] {
  if (!Array.isArray(value) || value.length !== subscriptionEventKeys.length) invalid();
  const result = subscriptionEventKeys.map((key) => {
    const matching = value.filter((row: unknown) => object(row) && row['key'] === key);
    const row: unknown = matching[0];
    if (matching.length !== 1 || !object(row)) invalid();
    return { key, count: count(row['count']) };
  });
  if (sum(result.map((row) => row.count)) !== total) invalid();
  return result;
}
function boundary(value: unknown, expected: string): string {
  if (
    typeof value !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T00:00:00(?:\.0{1,7})?(?:Z|\+00:00)(?![\s\S])/.test(value) ||
    value.slice(0, 10) !== expected
  )
    invalid();
  return value;
}
function generated(value: unknown): string {
  if (
    typeof value !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)(?![\s\S])/.test(
      value,
    ) ||
    !eventDate(value.slice(0, 10)) ||
    !Number.isFinite(Date.parse(value))
  )
    invalid();
  return value;
}
export function subscriptionEventReportFrom(
  value: unknown,
  interval: EventInterval,
): SubscriptionEventReport {
  const length = eventIntervalDays(interval);
  if (length === null || !object(value) || value['scope'] !== 'recorded_events') invalid();
  const fromUtc = boundary(value['fromUtc'], interval.from);
  const toUtc = boundary(value['toUtc'], interval.to);
  const generatedUtc = generated(value['generatedUtc']);
  const totalEvents = count(value['totalEvents']);
  const byEvent = counts(value['byEvent'], totalEvents);
  if (!Array.isArray(value['days']) || value['days'].length !== length) invalid();
  const rawDays = value['days'];
  const days = Array.from({ length }, (_, offset) => {
    const dayUtc = shiftEventDate(interval.from, offset)!;
    const matches = rawDays.filter((row: unknown) => object(row) && row['dayUtc'] === dayUtc);
    const row: unknown = matches[0];
    if (matches.length !== 1 || !object(row)) invalid();
    const dayTotal = count(row['totalEvents']);
    return { dayUtc, totalEvents: dayTotal, byEvent: counts(row['byEvent'], dayTotal) };
  });
  if (sum(days.map((day) => day.totalEvents)) !== totalEvents) invalid();
  for (const bucket of byEvent) {
    if (
      sum(days.map((day) => day.byEvent.find((row) => row.key === bucket.key)!.count)) !==
      bucket.count
    )
      invalid();
  }
  return { scope: 'recorded_events', generatedUtc, fromUtc, toUtc, totalEvents, byEvent, days };
}
export async function subscriptionEvents(
  interval: EventInterval,
  signal?: AbortSignal,
): Promise<SubscriptionEventReport> {
  if (eventIntervalDays(interval) === null) throw new ApiError(0, 'invalid_interval');
  const query = new URLSearchParams({ from: interval.from, to: interval.to });
  const result = await request(
    '/admin/reports/subscriptions/events?' + query.toString(),
    undefined,
    'GET',
    { ...(signal !== undefined ? { signal } : {}), cache: 'no-store' },
  );
  return subscriptionEventReportFrom(result, interval);
}
