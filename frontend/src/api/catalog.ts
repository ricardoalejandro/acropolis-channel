import { ApiError, request } from './identity';
export const categories = [
  { id: 'lecturas', label: 'Lecturas' },
  { id: 'documentales', label: 'Documentales' },
  { id: 'videos', label: 'Videos' },
  { id: 'podcast', label: 'Podcast' },
  { id: 'charlas-online', label: 'Charlas online' },
  { id: 'cursos', label: 'Cursos' },
] as const;
export type CategoryId = (typeof categories)[number]['id'];
export const covers = [
  'hero-acropolis',
  'editorial-reading',
  'editorial-podcast',
  'editorial-dialogue',
  'editorial-nature',
] as const;
export type CoverAsset = (typeof covers)[number];
export type ContentStatus = 'draft' | 'published' | 'archived';
export type ContentSummary = {
  id: string;
  slug: string;
  title: string;
  summary: string;
  category: CategoryId;
  coverAsset: CoverAsset | null;
  durationSeconds: number | null;
  publishedUtc: string | null;
};
export type ContentDetail = ContentSummary & { body: string; updatedUtc: string };
export type AdminSummary = ContentSummary & {
  status: ContentStatus;
  createdUtc: string;
  updatedUtc: string;
  version: string;
};
export type AdminContent = AdminSummary & { body: string };
export type ContentPage<T> = { items: T[]; total: number; page: number; pageSize: number };
export type ContentFields = {
  title: string;
  slug: string;
  summary: string;
  body: string;
  category: CategoryId;
  coverAsset: CoverAsset | null;
  durationSeconds: number | null;
};
export type ContentQuery = {
  search?: string;
  category?: string;
  status?: string;
  page?: number;
  pageSize?: number;
};
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function fail(): never {
  throw new ApiError(0, 'invalid_response');
}
function date(value: unknown, nullable = false) {
  return (
    (nullable && value === null) ||
    (typeof value === 'string' && Number.isFinite(Date.parse(value)))
  );
}
export function summaryFrom(value: unknown): ContentSummary {
  if (
    !object(value) ||
    !['id', 'slug', 'title', 'summary'].every((key) => typeof value[key] === 'string') ||
    !categories.some((item) => item.id === value['category']) ||
    !(value['coverAsset'] === null || covers.includes(value['coverAsset'] as CoverAsset)) ||
    !(
      value['durationSeconds'] === null ||
      (Number.isInteger(value['durationSeconds']) &&
        Number(value['durationSeconds']) >= 1 &&
        Number(value['durationSeconds']) <= 86400)
    ) ||
    !date(value['publishedUtc'], true)
  )
    fail();
  return value as ContentSummary;
}
export function detailFrom(value: unknown): ContentDetail {
  const summary = summaryFrom(value);
  if (!object(value) || typeof value['body'] !== 'string' || !date(value['updatedUtc'])) fail();
  return { ...summary, body: value['body'], updatedUtc: value['updatedUtc'] as string };
}
export function adminFrom(value: unknown): AdminSummary {
  summaryFrom(value);
  if (
    !object(value) ||
    !['draft', 'published', 'archived'].includes(String(value['status'])) ||
    !date(value['createdUtc']) ||
    !date(value['updatedUtc']) ||
    typeof value['version'] !== 'string' ||
    !value['version']
  )
    fail();
  return value as AdminSummary;
}
export function adminDetailFrom(value: unknown): AdminContent {
  const summary = adminFrom(value);
  if (!object(value) || typeof value['body'] !== 'string') fail();
  return { ...summary, body: value['body'] };
}
function pageFrom<T>(value: unknown, item: (value: unknown) => T): ContentPage<T> {
  if (
    !object(value) ||
    !Array.isArray(value['items']) ||
    !Number.isInteger(value['total']) ||
    Number(value['total']) < 0 ||
    !Number.isInteger(value['page']) ||
    Number(value['page']) < 1 ||
    !Number.isInteger(value['pageSize']) ||
    Number(value['pageSize']) < 1 ||
    Number(value['pageSize']) > 100
  )
    fail();
  return {
    items: value['items'].map(item),
    total: value['total'] as number,
    page: value['page'] as number,
    pageSize: value['pageSize'] as number,
  };
}
function queryString(query: ContentQuery) {
  const params = new URLSearchParams();
  for (const name of ['search', 'category', 'status'] as const)
    if (query[name]) params.set(name, query[name]);
  params.set('page', String(query.page ?? 1));
  params.set('pageSize', String(query.pageSize ?? 20));
  return '?' + params;
}
export function catalogMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return 'No pudimos cargar los contenidos. Inténtalo de nuevo.';
  const messages: Record<string, string> = {
    concurrency_conflict:
      'Otra persona modificó este contenido. Conservamos tus cambios; recarga los datos antes de guardar.',
    slug_immutable: 'La dirección se conserva después de la primera publicación.',
    slug_conflict: 'Esa dirección ya pertenece a otro contenido. Elige una diferente.',
    invalid_transition:
      'Ese cambio de estado no está permitido. Vuelve a borrador antes de publicar.',
  };
  return messages[error.code] ?? error.message;
}
export function categoryLabel(id: CategoryId) {
  return categories.find((category) => category.id === id)?.label ?? id;
}
export function durationLabel(seconds: number) {
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return minutes ? minutes + ' min' + (remainder ? ' ' + remainder + ' s' : '') : seconds + ' s';
}
function editableFields(fields: ContentFields): ContentFields {
  return {
    title: fields.title,
    slug: fields.slug,
    summary: fields.summary,
    body: fields.body,
    category: fields.category,
    coverAsset: fields.coverAsset,
    durationSeconds: fields.durationSeconds,
  };
}
export const catalog = {
  categories: async () => {
    const data = await request('/catalog/categories');
    if (
      !object(data) ||
      !Array.isArray(data['items']) ||
      data['items'].length !== categories.length
    )
      fail();
    const result = data['items'];
    if (
      !categories.every(
        (category) =>
          result.filter(
            (item) =>
              object(item) && item['id'] === category.id && item['label'] === category.label,
          ).length === 1,
      )
    )
      fail();
    return categories;
  },
  list: async (query: ContentQuery = {}) =>
    pageFrom(await request('/catalog/content' + queryString(query)), summaryFrom),
  detail: async (slug: string) =>
    detailFrom(await request('/catalog/content/' + encodeURIComponent(slug))),
  adminList: async (query: ContentQuery = {}) =>
    pageFrom(await request('/admin/content' + queryString(query)), adminFrom),
  adminDetail: async (id: string) =>
    adminDetailFrom(await request('/admin/content/' + encodeURIComponent(id))),
  create: async (fields: ContentFields) =>
    adminDetailFrom(await request('/admin/content', editableFields(fields))),
  update: async (id: string, fields: ContentFields, status: ContentStatus, version: string) =>
    adminDetailFrom(
      await request(
        '/admin/content/' + encodeURIComponent(id),
        { ...editableFields(fields), status, version },
        'PUT',
      ),
    ),
};

export function contentQuery(params: URLSearchParams): ContentQuery {
  const rawPage = params.get('page');
  const value = rawPage && /^\d+$/.test(rawPage) ? Number(rawPage) : 1;
  return {
    search: (params.get('search') ?? '').slice(0, 100),
    category: categories.some((item) => item.id === params.get('category'))
      ? (params.get('category') as string)
      : '',
    page: value >= 1 && value <= 1000000 ? value : 1,
  };
}
