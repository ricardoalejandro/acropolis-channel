import { ApiError, levels, request, type Level } from './identity';
import { categories, type CategoryId } from './catalog';
export const identityStatuses = ['active', 'pending', 'disabled'] as const;
export const catalogStatuses = ['draft', 'published', 'archived'] as const;
export const subscriptionStatuses = ['active', 'cancelled', 'suspended'] as const;
export const contentKinds = ['work', 'course', 'program'] as const;
export type ReportCount<K extends string = string> = { key: K; count: number };
type CurrentReport = { scope: 'current'; generatedUtc: string; total: number };
export type IdentityReport = CurrentReport & {
  byStatus: ReportCount<(typeof identityStatuses)[number]>[];
  byLevel: ReportCount<Level>[];
};
export type CatalogReport = CurrentReport & {
  byStatus: ReportCount<(typeof catalogStatuses)[number]>[];
  byCategoryAndStatus: {
    category: CategoryId;
    status: (typeof catalogStatuses)[number];
    count: number;
  }[];
  byKindAndStatus: {
    kind: (typeof contentKinds)[number];
    status: (typeof catalogStatuses)[number];
    count: number;
  }[];
};
export type SubscriptionReport = CurrentReport & {
  byStatus: ReportCount<(typeof subscriptionStatuses)[number]>[];
};
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function invalid(): never {
  throw new ApiError(0, 'invalid_response');
}
function count(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) invalid();
  return value;
}
function base(value: unknown): CurrentReport {
  if (
    !object(value) ||
    value['scope'] !== 'current' ||
    typeof value['generatedUtc'] !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value['generatedUtc'],
    ) ||
    !Number.isFinite(Date.parse(value['generatedUtc']))
  )
    invalid();
  return { scope: 'current', generatedUtc: value['generatedUtc'], total: count(value['total']) };
}
function buckets<K extends string>(value: unknown, keys: readonly K[]): ReportCount<K>[] {
  if (!Array.isArray(value) || value.length !== keys.length) invalid();
  return keys.map((key) => {
    const matches = value.filter((row: unknown) => object(row) && row['key'] === key);
    const row: unknown = matches[0];
    if (matches.length !== 1 || !object(row)) invalid();
    return { key, count: count(row['count']) };
  });
}
function states<K extends string>(value: unknown, keys: readonly K[], total: number) {
  const parsed = buckets(value, keys);
  if (parsed.reduce((sum, row) => sum + row.count, 0) !== total) invalid();
  return parsed;
}
function matrix<K extends string, F extends 'category' | 'kind'>(
  value: unknown,
  keys: readonly K[],
  field: F,
  byStatus: CatalogReport['byStatus'],
): ({ count: number; status: (typeof catalogStatuses)[number] } & Record<F, K>)[] {
  if (!Array.isArray(value) || value.length !== keys.length * catalogStatuses.length) invalid();
  const parsed = keys.flatMap((key) =>
    catalogStatuses.map((status) => {
      const matches = value.filter(
        (row: unknown) => object(row) && row[field] === key && row['status'] === status,
      );
      const row: unknown = matches[0];
      if (matches.length !== 1 || !object(row)) invalid();
      return { [field]: key, status, count: count(row['count']) } as {
        count: number;
        status: typeof status;
      } & Record<F, K>;
    }),
  );
  if (
    byStatus.some(
      (bucket) =>
        parsed
          .filter((row) => row.status === bucket.key)
          .reduce((sum, row) => sum + row.count, 0) !== bucket.count,
    )
  )
    invalid();
  return parsed;
}
export function identityReportFrom(value: unknown): IdentityReport {
  const current = base(value);
  if (!object(value)) invalid();
  const byLevel = buckets(value['byLevel'], levels);
  if (byLevel.some((row) => row.count > current.total)) invalid();
  return {
    ...current,
    byStatus: states(value['byStatus'], identityStatuses, current.total),
    byLevel,
  };
}
export function catalogReportFrom(value: unknown): CatalogReport {
  const current = base(value);
  if (!object(value)) invalid();
  const byStatus = states(value['byStatus'], catalogStatuses, current.total);
  const byCategoryAndStatus = matrix(
    value['byCategoryAndStatus'],
    categories.map((item) => item.id),
    'category',
    byStatus,
  );
  const byKindAndStatus = matrix(value['byKindAndStatus'], contentKinds, 'kind', byStatus);
  if (
    catalogStatuses.some(
      (status) =>
        byKindAndStatus
          .filter((row) => row.kind !== 'work' && row.status === status)
          .reduce((sum, row) => sum + row.count, 0) >
        byCategoryAndStatus.find((row) => row.category === 'cursos' && row.status === status)!
          .count,
    )
  )
    invalid();
  return { ...current, byStatus, byCategoryAndStatus, byKindAndStatus };
}
export function subscriptionReportFrom(value: unknown): SubscriptionReport {
  const current = base(value);
  if (!object(value)) invalid();
  return { ...current, byStatus: states(value['byStatus'], subscriptionStatuses, current.total) };
}
export function reportMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'mfa_required_for_admin')
      return 'Completa la verificación en dos pasos para consultar este reporte.';
    if (error.status === 403) return 'Tu cuenta no tiene permiso para consultar este reporte.';
    if (error.status === 401) return 'Ingresa de nuevo para consultar este reporte.';
    if (error.code === 'invalid_response')
      return 'No pudimos leer los datos de este reporte. Inténtalo de nuevo.';
    if (error.status === 429)
      return 'Espera unos minutos antes de volver a consultar este reporte.';
  }
  return 'No pudimos cargar este reporte. Inténtalo de nuevo.';
}
export const reports = {
  identity: async () => identityReportFrom(await request('/admin/reports/identity')),
  catalog: async () => catalogReportFrom(await request('/admin/reports/catalog')),
  subscriptions: async () => subscriptionReportFrom(await request('/admin/reports/subscriptions')),
};
