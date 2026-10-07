import { levels } from '../api/identity';
import { categories } from '../api/catalog';
import {
  catalogStatuses,
  contentKinds,
  type IdentityReport,
  type CatalogReport,
  type SubscriptionReport,
} from '../api/reports';
export const reportTime = '2026-10-06T12:00:00Z';
export const identityReport: IdentityReport = {
  scope: 'current',
  generatedUtc: reportTime,
  total: 7,
  byStatus: [
    { key: 'active', count: 4 },
    { key: 'pending', count: 2 },
    { key: 'disabled', count: 1 },
  ],
  byLevel: levels.map((key) => ({
    key,
    count: key === 'Externo' ? 7 : key === 'Miembro' ? 4 : key === 'FFVV' ? 1 : 0,
  })),
};
export const catalogReport: CatalogReport = {
  scope: 'current',
  generatedUtc: reportTime,
  total: 6,
  byStatus: [
    { key: 'draft', count: 2 },
    { key: 'published', count: 3 },
    { key: 'archived', count: 1 },
  ],
  byCategoryAndStatus: categories.flatMap(({ id }) =>
    catalogStatuses.map((status) => ({
      category: id,
      status,
      count:
        id === 'lecturas' && status === 'draft'
          ? 2
          : id === 'lecturas' && status === 'published'
            ? 1
            : id === 'videos' && status === 'published'
              ? 1
              : id === 'cursos' && status === 'published'
                ? 1
                : id === 'cursos' && status === 'archived'
                  ? 1
                  : 0,
    })),
  ),
  byKindAndStatus: contentKinds.flatMap((kind) =>
    catalogStatuses.map((status) => ({
      kind,
      status,
      count:
        kind === 'work' && status === 'draft'
          ? 2
          : kind === 'work' && status === 'published'
            ? 2
            : kind === 'course' && status === 'published'
              ? 1
              : kind === 'program' && status === 'archived'
                ? 1
                : 0,
    })),
  ),
};
export const subscriptionReport: SubscriptionReport = {
  scope: 'current',
  generatedUtc: reportTime,
  total: 4,
  byStatus: [
    { key: 'active', count: 2 },
    { key: 'cancelled', count: 1 },
    { key: 'suspended', count: 1 },
  ],
};
