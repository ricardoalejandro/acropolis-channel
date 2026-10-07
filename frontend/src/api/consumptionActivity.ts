import { ApiError, request } from './identity';
import { categories, type CategoryId } from './catalog';
export type ConsumptionSource = 'reading' | 'youtube';
export type ConsumptionCapabilities = { recordingEnabled: boolean; detailRetentionDays: number; generalRetentionDays: number };
export type ConsumptionRange = { from: number; to: number };
export type MediaObservation = { state: 'playing' | 'paused' | 'buffering' | 'ended'; positionMs: number; durationMs: number | null; playbackRateMilli: number; segments: ConsumptionRange[] };
export type ReadingObservation = { positionBasisPoints: number; exposedRanges: ConsumptionRange[] };
export type ConsumptionPulse = { sequence: number; intervalMs: number; activeMs: number } & ({ media: MediaObservation; reading?: never } | { reading: ReadingObservation; media?: never });
export type ConsumptionSession = { sessionId: string; startedUtc: string; nextSequence: number; sourceKind: ConsumptionSource };
export type ConsumptionReceipt = { sequence: number; recordedUtc: string; creditedMs: number; progressBasisPoints: number | null; coverageIncomplete: boolean; endedReported: boolean };
export type ConsumptionMetrics = { accountsWithActivity: number | null; starts: number; recordedPulses: number; creditedMs: number; endedReports: number; knownProgressSamples: number; unknownProgressSamples: number; progressBasisPointsSum: number; meanProgressBasisPoints: number | null };
export type ConsumptionDay = { dayUtc: string; metrics: ConsumptionMetrics };
export type ConsumptionGroup = { key: string; metrics: ConsumptionMetrics };
type BaseReport = { generatedUtc: string; fromUtc: string; toUtc: string; availableFromUtc: string; detailAvailableFromUtc: string | null; retentionDays: number; recordingEnabled: boolean };
export type ConsumptionReport = BaseReport & { scope: 'recorded_consumption_general'; summary: ConsumptionMetrics; byKind: ConsumptionGroup[]; byCategory: ConsumptionGroup[]; daily: ConsumptionDay[] };
export type ConsumptionContent = { contentId: string; contentVersion: string; slug: string | null; title: string | null; status: 'draft' | 'published' | 'archived' | null; categoryAtStart: CategoryId; sourceKind: ConsumptionSource; metrics: ConsumptionMetrics };
export type ConsumptionContentPage = BaseReport & { scope: 'recorded_consumption_general'; items: ConsumptionContent[]; total: number; page: number; pageSize: number };
export type AccountConsumptionReport = BaseReport & { scope: 'recorded_consumption_account'; summary: ConsumptionMetrics; daily: ConsumptionDay[]; items: ConsumptionContent[]; total: number; page: number; pageSize: number };
export type ConsumptionQuery = { from: string; to: string; page?: number; pageSize?: number };
const metricFields = ['starts', 'recordedPulses', 'creditedMs', 'endedReports', 'knownProgressSamples', 'unknownProgressSamples', 'progressBasisPointsSum'] as const;
const sources = ['reading', 'youtube'] as const;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const contentVersionPattern = /^[0-9a-f]{32}(?![\s\S])/;
function invalid(): never { throw new ApiError(0, 'invalid_response'); }
function object(value: unknown): value is Record<string, unknown> { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function exact(value: unknown, fields: readonly string[]): Record<string, unknown> {
  if (!object(value) || Object.keys(value).length !== fields.length || !fields.every((field) => Object.hasOwn(value, field))) invalid();
  return value;
}
function integer(value: unknown, max = Number.MAX_SAFE_INTEGER, min = 0): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < min || value > max) invalid();
  return value;
}
function boolean(value: unknown): boolean { if (typeof value !== 'boolean') invalid(); return value; }
function text(value: unknown): string { if (typeof value !== 'string') invalid(); return value; }
function id(value: unknown): string { const parsed = text(value); if (parsed.length !== 36 || !uuid.test(parsed)) invalid(); return parsed; }
function version(value: unknown): string { const parsed = text(value); if (!contentVersionPattern.test(parsed)) invalid(); return parsed; }
function source(value: unknown): ConsumptionSource { if (value !== 'reading' && value !== 'youtube') invalid(); return value; }
export function utcDate(value: unknown): string {
  const parsed = text(value);
  if (parsed.length !== 10 || parsed.startsWith('0000') || !/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/.test(parsed)) invalid();
  const milliseconds = Date.parse(parsed + 'T00:00:00Z');
  if (!Number.isFinite(milliseconds) || new Date(milliseconds).toISOString().slice(0, 10) !== parsed) invalid();
  return parsed;
}
function utc(value: unknown): string {
  const parsed = text(value);
  if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:[.][0-9]{1,7})?(?:Z|[+]00:00)(?![\s\S])/.test(parsed) || !Number.isFinite(Date.parse(parsed))) invalid();
  utcDate(parsed.slice(0, 10));
  return parsed;
}
export function utcConsumptionWindow(days: 7 | 30 | 90 | 365, now = new Date()): ConsumptionQuery {
  const midnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  return { from: new Date(midnight - (days - 1) * 86400000).toISOString().slice(0, 10), to: new Date(midnight + 86400000).toISOString().slice(0, 10), page: 1, pageSize: 20 };
}
export function consumptionQuery(query: ConsumptionQuery, paged = false): string {
  const from = utcDate(query.from); const to = utcDate(query.to);
  const days = (Date.parse(to) - Date.parse(from)) / 86400000;
  if (!Number.isInteger(days) || days < 1 || days > 366) throw new ApiError(400, 'validation_error');
  const parameters = new URLSearchParams({ from, to });
  if (paged) { parameters.set('page', String(integer(query.page ?? 1, 1000000, 1))); parameters.set('pageSize', String(integer(query.pageSize ?? 20, 100, 1))); }
  return parameters.toString();
}
export function consumptionCapabilitiesFrom(value: unknown): ConsumptionCapabilities {
  const data = exact(value, ['recordingEnabled', 'detailRetentionDays', 'generalRetentionDays']);
  if (data['detailRetentionDays'] !== 90 || data['generalRetentionDays'] !== 365) invalid();
  return { recordingEnabled: boolean(data['recordingEnabled']), detailRetentionDays: 90, generalRetentionDays: 365 };
}
export function consumptionSessionFrom(value: unknown): ConsumptionSession {
  const data = exact(value, ['sessionId', 'startedUtc', 'nextSequence', 'sourceKind']);
  return { sessionId: id(data['sessionId']), startedUtc: utc(data['startedUtc']), nextSequence: integer(data['nextSequence'], 2147483647, 1), sourceKind: source(data['sourceKind']) };
}
export function consumptionReceiptFrom(value: unknown): ConsumptionReceipt {
  const data = exact(value, ['sequence', 'recordedUtc', 'creditedMs', 'progressBasisPoints', 'coverageIncomplete', 'endedReported']);
  const receipt = { sequence: integer(data['sequence'], 2147483647, 1), recordedUtc: utc(data['recordedUtc']), creditedMs: integer(data['creditedMs'], 15000), progressBasisPoints: data['progressBasisPoints'] === null ? null : integer(data['progressBasisPoints'], 10000), coverageIncomplete: boolean(data['coverageIncomplete']), endedReported: boolean(data['endedReported']) };
  if (receipt.coverageIncomplete && receipt.progressBasisPoints !== null) invalid();
  return receipt;
}
export function consumptionMetricsFrom(value: unknown): ConsumptionMetrics {
  const data = exact(value, ['accountsWithActivity', ...metricFields, 'meanProgressBasisPoints']);
  const values = Object.fromEntries(metricFields.map((field) => [field, integer(data[field])])) as Omit<ConsumptionMetrics, 'meanProgressBasisPoints' | 'accountsWithActivity'>;
  const maximum = values.knownProgressSamples * 10000;
  if (!Number.isSafeInteger(maximum) || values.progressBasisPointsSum > maximum) invalid();
  const mean = values.knownProgressSamples === 0 ? null : Math.floor(values.progressBasisPointsSum / values.knownProgressSamples);
  if (data['meanProgressBasisPoints'] !== mean || values.knownProgressSamples + values.unknownProgressSamples > values.recordedPulses || values.endedReports > values.recordedPulses) invalid();
  const accountsWithActivity = data['accountsWithActivity'] === null ? null : integer(data['accountsWithActivity']);
  if (accountsWithActivity !== null && accountsWithActivity > values.starts + values.recordedPulses) invalid();
  return { ...values, accountsWithActivity, meanProgressBasisPoints: mean };
}
const baseFields = ['scope', 'generatedUtc', 'fromUtc', 'toUtc', 'availableFromUtc', 'retentionDays', 'recordingEnabled'] as const;
function base(data: Record<string, unknown>, account = false): BaseReport {
  if (data['scope'] !== (account ? 'recorded_consumption_account' : 'recorded_consumption_general') || data['retentionDays'] !== (account ? 90 : 365)) invalid();
  const result = { generatedUtc: utc(data['generatedUtc']), fromUtc: utc(data['fromUtc']), toUtc: utc(data['toUtc']), availableFromUtc: utc(data['availableFromUtc']), detailAvailableFromUtc: account ? null : utc(data['detailAvailableFromUtc']), retentionDays: account ? 90 : 365, recordingEnabled: boolean(data['recordingEnabled']) };
  if (Date.parse(result.toUtc) <= Date.parse(result.fromUtc) || Date.parse(result.toUtc) - Date.parse(result.fromUtc) > 366 * 86400000) invalid();
  const midnight = Date.parse(result.generatedUtc.slice(0, 10) + 'T00:00:00Z');
  const detailFrom = midnight - 89 * 86400000;
  const expectedAvailable = account ? detailFrom : midnight - 364 * 86400000;
  if (Date.parse(result.availableFromUtc) !== expectedAvailable || (!account && Date.parse(result.detailAvailableFromUtc!) !== detailFrom)) invalid();
  if (Date.parse(result.fromUtc) !== Date.parse(result.fromUtc.slice(0, 10)) || Date.parse(result.toUtc) !== Date.parse(result.toUtc.slice(0, 10))) invalid();
  return result;
}
function availability(metrics: ConsumptionMetrics, fromUtc: string, header: BaseReport) {
  const available = Date.parse(fromUtc) >= Date.parse(header.detailAvailableFromUtc ?? header.availableFromUtc);
  if ((metrics.accountsWithActivity !== null) !== available) invalid();
}
function reconcile(summary: ConsumptionMetrics, rows: ConsumptionMetrics[]) {
  if (metricFields.some((field) => rows.reduce((sum, row) => sum + row[field], 0) !== summary[field])) invalid();
  if (summary.accountsWithActivity !== null && rows.length) {
    const counts = rows.map((row) => row.accountsWithActivity);
    if (counts.some((count) => count === null) || Math.max(...counts as number[]) > summary.accountsWithActivity || (counts as number[]).reduce((sum, count) => sum + count, 0) < summary.accountsWithActivity) invalid();
  }
}
function groups(value: unknown, keys: readonly string[], summary: ConsumptionMetrics, header: BaseReport): ConsumptionGroup[] {
  if (!Array.isArray(value) || value.length !== keys.length) invalid();
  const parsed = keys.map((key) => {
    const matching = value.filter((row: unknown) => object(row) && row['key'] === key);
    if (matching.length !== 1) invalid();
    const row = exact(matching[0], ['key', 'metrics']);
    const metrics = consumptionMetricsFrom(row['metrics']); availability(metrics, header.fromUtc, header); return { key, metrics };
  });
  reconcile(summary, parsed.map((row) => row.metrics)); return parsed;
}
function days(value: unknown, header: BaseReport, summary: ConsumptionMetrics): ConsumptionDay[] {
  if (!Array.isArray(value)) invalid();
  const total = (Date.parse(header.toUtc) - Date.parse(header.fromUtc)) / 86400000;
  if (value.length !== total) invalid();
  const parsed = value.map((item: unknown, index) => {
    const row = exact(item, ['dayUtc', 'metrics']); const dayUtc = utcDate(row['dayUtc']);
    if (Date.parse(dayUtc) !== Date.parse(header.fromUtc) + index * 86400000) invalid();
    const metrics = consumptionMetricsFrom(row['metrics']); availability(metrics, dayUtc + 'T00:00:00Z', header);
    return { dayUtc, metrics };
  });
  reconcile(summary, parsed.map((row) => row.metrics)); return parsed;
}
export function consumptionReportFrom(value: unknown): ConsumptionReport {
  const data = exact(value, [...baseFields, 'detailAvailableFromUtc', 'summary', 'byKind', 'byCategory', 'daily']); const header = base(data); const summary = consumptionMetricsFrom(data['summary']);
  availability(summary, header.fromUtc, header);
  return { ...header, scope: 'recorded_consumption_general', summary, byKind: groups(data['byKind'], sources, summary, header), byCategory: groups(data['byCategory'], categories.map((category) => category.id), summary, header), daily: days(data['daily'], header, summary) };
}
function items(value: unknown): ConsumptionContent[] {
  if (!Array.isArray(value)) invalid();
  const seen = new Set<string>();
  return value.map((item: unknown) => {
    const row = exact(item, ['contentId', 'contentVersion', 'slug', 'title', 'status', 'categoryAtStart', 'sourceKind', 'metrics']);
    const contentId = id(row['contentId']); const contentVersion = version(row['contentVersion']); const sourceKind = source(row['sourceKind']); const category = text(row['categoryAtStart']);
    if (!categories.some((item) => item.id === category) || category === 'cursos' || (sourceKind === 'reading') !== (category === 'lecturas')) invalid();
    if (row['status'] !== null && !['draft', 'published', 'archived'].includes(text(row['status']))) invalid();
    const key = contentId + ':' + contentVersion; if (seen.has(key)) invalid(); seen.add(key);
    const slug = row['slug'] === null ? null : text(row['slug']); if (slug !== null && !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug)) invalid();
    return { contentId, contentVersion, slug, title: row['title'] === null ? null : text(row['title']), status: row['status'] as ConsumptionContent['status'], categoryAtStart: category as CategoryId, sourceKind, metrics: consumptionMetricsFrom(row['metrics']) };
  });
}
function page(data: Record<string, unknown>) {
  const rows = items(data['items']); const total = integer(data['total']); const page = integer(data['page'], 1000000, 1); const pageSize = integer(data['pageSize'], 100, 1);
  if (rows.length > pageSize || rows.length > total) invalid();
  return { items: rows, total, page, pageSize };
}
export function consumptionContentPageFrom(value: unknown): ConsumptionContentPage {
  const data = exact(value, [...baseFields, 'detailAvailableFromUtc', 'items', 'total', 'page', 'pageSize']); const header = base(data); const parsed = page(data); for (const row of parsed.items) availability(row.metrics, header.fromUtc, header); return { ...header, scope: 'recorded_consumption_general', ...parsed };
}
export function accountConsumptionReportFrom(value: unknown): AccountConsumptionReport {
  const data = exact(value, [...baseFields, 'summary', 'daily', 'items', 'total', 'page', 'pageSize']); const header = base(data, true); const summary = consumptionMetricsFrom(data['summary']);
  availability(summary, header.fromUtc, header); const parsed = page(data); for (const row of parsed.items) availability(row.metrics, header.fromUtc, header);
  return { ...header, scope: 'recorded_consumption_account', summary, daily: days(data['daily'], header, summary), ...parsed };
}
function matchQuery<T extends BaseReport>(report: T, query: ConsumptionQuery, paged = false): T {
  if (report.fromUtc.slice(0, 10) !== query.from || report.toUtc.slice(0, 10) !== query.to) invalid();
  if (paged && 'page' in report && ((report as T & { page: number; pageSize: number }).page !== (query.page ?? 1) || (report as T & { page: number; pageSize: number }).pageSize !== (query.pageSize ?? 20))) invalid();
  return report;
}
export const consumptionActivity = {
  capabilities: async (signal?: AbortSignal) => consumptionCapabilitiesFrom(await request('/consumption/capabilities', undefined, 'GET', { signal, cache: 'no-store' })),
  start: async (slug: string, visitId: string, contentVersion: string, signal?: AbortSignal) => consumptionSessionFrom(await request('/consumption/content/' + encodeURIComponent(slug) + '/sessions', { visitId, contentVersion }, 'POST', { signal })),
  pulse: async (sessionId: string, body: ConsumptionPulse, signal?: AbortSignal) => consumptionReceiptFrom(await request('/consumption/sessions/' + encodeURIComponent(sessionId) + '/pulses', body, 'POST', { signal })),
  report: async (query: ConsumptionQuery, signal?: AbortSignal) => matchQuery(consumptionReportFrom(await request('/admin/reports/catalog/consumption?' + consumptionQuery(query), undefined, 'GET', { signal, cache: 'no-store' })), query),
  content: async (query: ConsumptionQuery, signal?: AbortSignal) => matchQuery(consumptionContentPageFrom(await request('/admin/reports/catalog/consumption/content?' + consumptionQuery(query, true), undefined, 'GET', { signal, cache: 'no-store' })), query, true),
  account: async (accountId: string, query: ConsumptionQuery, signal?: AbortSignal) => matchQuery(accountConsumptionReportFrom(await request('/admin/users/' + encodeURIComponent(accountId) + '/consumption?' + consumptionQuery(query, true), undefined, 'GET', { signal, cache: 'no-store' })), query, true),
};
