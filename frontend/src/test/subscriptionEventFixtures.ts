import type {
  EventInterval,
  SubscriptionEventKey,
  SubscriptionEventReport,
} from '../api/subscriptionEvents';
export const expectedEventKeys = [
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
] as const satisfies readonly SubscriptionEventKey[];
export const eventInterval: EventInterval = { from: '2026-01-01', to: '2026-01-03' };
export function makeEventReport(
  interval: EventInterval = eventInterval,
  countsByDay: Partial<Record<SubscriptionEventKey, number>>[] = [],
): SubscriptionEventReport {
  const start = Date.parse(interval.from + 'T00:00:00Z');
  const length = (Date.parse(interval.to + 'T00:00:00Z') - start) / 86400000;
  if (!Number.isInteger(length) || length < 1 || length > 366)
    throw new Error('Invalid synthetic event fixture interval');
  const days = Array.from({ length }, (_, offset) => {
    const byEvent = expectedEventKeys.map((key) => ({
      key,
      count: countsByDay[offset]?.[key] ?? 0,
    }));
    return {
      dayUtc: new Date(start + offset * 86400000).toISOString().slice(0, 10),
      totalEvents: byEvent.reduce((total, row) => total + row.count, 0),
      byEvent,
    };
  });
  return {
    scope: 'recorded_events',
    generatedUtc: '2026-10-06T12:34:56.1234567Z',
    fromUtc: interval.from + 'T00:00:00Z',
    toUtc: interval.to + 'T00:00:00Z',
    totalEvents: days.reduce((total, row) => total + row.totalEvents, 0),
    byEvent: expectedEventKeys.map((key) => ({
      key,
      count: days.reduce(
        (total, day) => total + day.byEvent.find((row) => row.key === key)!.count,
        0,
      ),
    })),
    days,
  };
}
export const eventReport = makeEventReport(eventInterval, [
  { activated: 2, updated_active_suspended: 1, unclassified: 1 },
  { reactivated: 1, cancelled: 1, recovery_suspended: 1 },
]);
