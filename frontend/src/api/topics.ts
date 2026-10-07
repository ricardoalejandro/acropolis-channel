import { ApiError, request } from './identity';
export type PublicTopic = { id: string; slug: string; name: string; position: number };
export type AdminTopic = PublicTopic & { status: 'active' | 'archived'; version: string };
export type TopicPage<T> = { items: T[]; total: number; page: number; pageSize: number };
export type TopicDirectory = TopicPage<AdminTopic> & { directoryVersion: string };
export type ContentTopics = { contentId: string; contentVersion: string; items: AdminTopic[] };
const guid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}(?![\s\S])/i;
const version = /^[a-f0-9]{32}(?![\s\S])/;
export function validTopicSlug(value: string) { return value.length >= 2 && value.length <= 160 && /^[a-z0-9]+(-[a-z0-9]+)*(?![\s\S])/.test(value); }
export function validTopicName(value: string) { return value.trim().length >= 2 && value.length <= 180 && !/[\u0000-\u001f\u007f-\u009f]/u.test(value); }
function object(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value); }
function fail(): never { throw new ApiError(0, 'invalid_response'); }
function id(value: unknown): value is string { return typeof value === 'string' && guid.test(value) && value !== '00000000-0000-0000-0000-000000000000'; }
function revision(value: unknown): value is string { return typeof value === 'string' && version.test(value); }
export function publicTopicFrom(value: unknown): PublicTopic {
  if (!object(value) || !id(value['id']) || typeof value['slug'] !== 'string' || !validTopicSlug(value['slug']) || typeof value['name'] !== 'string' || !validTopicName(value['name']) || !Number.isSafeInteger(value['position']) || Number(value['position']) < 0 || Number(value['position']) > 2147483647) fail();
  return { id: value['id'].toLowerCase(), slug: value['slug'], name: value['name'], position: Number(value['position']) };
}
export function adminTopicFrom(value: unknown): AdminTopic {
  const topic = publicTopicFrom(value);
  if (!object(value) || !(value['status'] === 'active' || value['status'] === 'archived') || !revision(value['version'])) fail();
  return { ...topic, status: value['status'], version: value['version'] };
}
function pageFrom<T extends PublicTopic>(value: unknown, parse: (value: unknown) => T): TopicPage<T> {
  if (!object(value) || !Array.isArray(value['items']) || !Number.isSafeInteger(value['total']) || Number(value['total']) < 0 || !Number.isInteger(value['page']) || Number(value['page']) < 1 || Number(value['page']) > 1000000 || !Number.isInteger(value['pageSize']) || Number(value['pageSize']) < 1 || Number(value['pageSize']) > 100 || value['items'].length > Number(value['pageSize'])) fail();
  const items = value['items'].map(parse);
  if (new Set(items.map(x => x.id)).size !== items.length || new Set(items.map(x => x.slug)).size !== items.length || Number(value['total']) < items.length) fail();
  return { items, total: Number(value['total']), page: Number(value['page']), pageSize: Number(value['pageSize']) };
}
export function publicTopicPageFrom(value: unknown) { return pageFrom(value, publicTopicFrom); }
export function directoryFrom(value: unknown): TopicDirectory {
  const page = pageFrom(value, adminTopicFrom);
  if (!object(value) || !revision(value['directoryVersion'])) fail();
  return { ...page, directoryVersion: value['directoryVersion'] };
}
export function contentTopicsFrom(value: unknown): ContentTopics {
  if (!object(value) || !id(value['contentId']) || !revision(value['contentVersion']) || !Array.isArray(value['items']) || value['items'].length > 12) fail();
  const items = value['items'].map(adminTopicFrom);
  if (new Set(items.map(x => x.id)).size !== items.length) fail();
  return { contentId: value['contentId'].toLowerCase(), contentVersion: value['contentVersion'], items };
}
export function topicMessage(error: unknown) {
  if (!(error instanceof ApiError)) return 'No pudimos completar la acción. Inténtalo de nuevo.';
  const messages: Record<string, string> = {
    concurrency_conflict: 'Los datos cambiaron mientras editabas. Conservamos tus cambios; recarga antes de guardar.',
    topic_slug_conflict: 'Esta dirección ya pertenece a otro tema, incluso si está archivado.',
    topic_unavailable: 'Un tema seleccionado ya no está activo. Conservamos tu selección; recarga los temas.',
    topic_order_limit: 'No pudimos añadir otra posición al directorio.',
  };
  return messages[error.code] ?? error.message;
}
const options = (signal?: AbortSignal) => ({ ...(signal ? { signal } : {}), cache: 'no-store' as const });
const adminPath = '/admin/catalog/topics';
function topicPath(value: string) { if (!id(value)) throw new ApiError(400, 'validation_error'); return adminPath + '/' + encodeURIComponent(value); }
function contentPath(value: string) { if (!id(value)) throw new ApiError(400, 'validation_error'); return '/admin/content/' + encodeURIComponent(value) + '/topics'; }
export const topics = {
  publicList: async (page = 1, signal?: AbortSignal) => publicTopicPageFrom(await request('/catalog/topics?page=' + page + '&pageSize=100', undefined, 'GET', options(signal))),
  list: async (query: { page?: number; search?: string; status?: string } = {}, signal?: AbortSignal) => {
    const params = new URLSearchParams({ page: String(query.page ?? 1), pageSize: '20' });
    if (query.search) params.set('search', query.search);
    if (query.status) params.set('status', query.status);
    return directoryFrom(await request(adminPath + '?' + params, undefined, 'GET', options(signal)));
  },
  detail: async (value: string, signal?: AbortSignal) => adminTopicFrom(await request(topicPath(value), undefined, 'GET', options(signal))),
  create: async (slug: string, name: string) => adminTopicFrom(await request(adminPath, { slug, name })),
  update: async (value: string, currentVersion: string, name: string) => adminTopicFrom(await request(topicPath(value), { version: currentVersion, name }, 'PUT')),
  state: async (value: string, currentVersion: string, status: AdminTopic['status']) => adminTopicFrom(await request(topicPath(value) + '/state', { version: currentVersion, status }, 'PUT')),
  move: async (directoryVersion: string, value: string, beforeId: string | null) => adminTopicFrom(await request(adminPath + '/order', { directoryVersion, id: value, beforeId }, 'PUT')),
  assigned: async (value: string, signal?: AbortSignal) => contentTopicsFrom(await request(contentPath(value), undefined, 'GET', options(signal))),
  assign: async (value: string, contentVersion: string, topicIds: string[]) => contentTopicsFrom(await request(contentPath(value), { contentVersion, topicIds: [...topicIds] }, 'PUT')),
};
