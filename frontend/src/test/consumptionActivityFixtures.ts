import { categories } from '../api/catalog';
import type { ConsumptionMetrics } from '../api/consumptionActivity';
export const activityNow = '2026-10-06T12:00:00Z';
export const activityDetailFrom = '2026-07-09T00:00:00Z';
export const activityVersion = '1234567890abcdef1234567890abcdef';
export const activityContentId = '10000000-0000-0000-0000-000000000001';
export function activityMetrics(overrides: Partial<ConsumptionMetrics> = {}): ConsumptionMetrics {
  const data = { accountsWithActivity: 1, starts: 1, recordedPulses: 1, creditedMs: 1000, endedReports: 0, knownProgressSamples: 1, unknownProgressSamples: 0, progressBasisPointsSum: 5000, ...overrides };
  return { ...data, meanProgressBasisPoints: overrides.meanProgressBasisPoints !== undefined ? overrides.meanProgressBasisPoints : data.knownProgressSamples === 0 ? null : Math.floor(data.progressBasisPointsSum / data.knownProgressSamples) };
}
export const zeroActivity = () => activityMetrics({ accountsWithActivity: 0, starts: 0, recordedPulses: 0, creditedMs: 0, knownProgressSamples: 0, progressBasisPointsSum: 0 });
export function activityReport() {
  return { scope: 'recorded_consumption_general', generatedUtc: activityNow, fromUtc: '2026-10-06T00:00:00Z', toUtc: '2026-10-07T00:00:00Z', availableFromUtc: '2025-10-07T00:00:00Z', detailAvailableFromUtc: activityDetailFrom, retentionDays: 365, recordingEnabled: true, summary: activityMetrics(), byKind: [{ key: 'reading', metrics: activityMetrics() }, { key: 'youtube', metrics: zeroActivity() }], byCategory: categories.map((category) => ({ key: category.id, metrics: category.id === 'lecturas' ? activityMetrics() : zeroActivity() })), daily: [{ dayUtc: '2026-10-06', metrics: activityMetrics() }] };
}
export function activityPage() {
  const report = activityReport();
  const header = { scope: report.scope, generatedUtc: report.generatedUtc, fromUtc: report.fromUtc, toUtc: report.toUtc, availableFromUtc: report.availableFromUtc, detailAvailableFromUtc: report.detailAvailableFromUtc, retentionDays: report.retentionDays, recordingEnabled: report.recordingEnabled };
  return { ...header, items: [{ contentId: activityContentId, contentVersion: activityVersion, slug: 'lectura-real', title: 'Lectura real', status: 'published', categoryAtStart: 'lecturas', sourceKind: 'reading', metrics: activityMetrics() }], total: 1, page: 1, pageSize: 20 };
}
export function accountActivityReport() {
  const page = activityPage();
  return { scope: 'recorded_consumption_account', generatedUtc: page.generatedUtc, fromUtc: page.fromUtc, toUtc: page.toUtc, availableFromUtc: activityDetailFrom, retentionDays: 90, recordingEnabled: page.recordingEnabled, items: page.items, total: page.total, page: page.page, pageSize: page.pageSize, summary: activityMetrics(), daily: activityReport().daily };
}
