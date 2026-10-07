import { ApiError, request } from './identity';
import { summaryFrom, type ContentSummary } from './catalog';
import { contentVersionPattern } from './consumptionActivity';
export type CollectionKind = 'course' | 'program';
export type ContentWork = {
  id: string;
  version: string;
  slug: string;
  title: string;
  category: string;
  workText: string | null;
  youTubeId: string | null;
  collectionKind: CollectionKind | null;
  items: ContentSummary[];
};
export function workFrom(value: unknown): ContentWork {
  const data = value as Record<string, unknown> | null;
  if (
    typeof data !== 'object' ||
    data === null ||
    Array.isArray(data) ||
    !['id', 'slug', 'title', 'category'].every((key) => typeof data[key] === 'string') ||
    !(typeof data['version'] === 'string' && contentVersionPattern.test(data['version'])) ||
    !(data['workText'] === null || typeof data['workText'] === 'string') ||
    !(
      data['youTubeId'] === null ||
      (typeof data['youTubeId'] === 'string' &&
        data['youTubeId'].length === 11 &&
        /^[A-Za-z0-9_-]{11}$/.test(data['youTubeId']))
    ) ||
    !(
      data['collectionKind'] === null ||
      data['collectionKind'] === 'course' ||
      data['collectionKind'] === 'program'
    ) ||
    !Array.isArray(data['items'])
  )
    throw new ApiError(0, 'invalid_response');
  return { ...(data as Omit<ContentWork, 'items'>), items: data['items'].map(summaryFrom) };
}
export const consumption = {
  content: async (slug: string) =>
    workFrom(await request('/consumption/content/' + encodeURIComponent(slug))),
};
