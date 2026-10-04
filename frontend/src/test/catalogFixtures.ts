import type { AdminContent } from '../api/catalog';
export const publishedContent: AdminContent = {
  id: 'content-one',
  slug: 'filosofia-vida',
  title: 'La filosofía en nuestra vida',
  category: 'videos',
  coverAsset: 'editorial-dialogue',
  durationSeconds: 145,
  summary: 'Una sinopsis editorial para la prueba.',
  body: 'Primera idea.\n\nSegunda idea.',
  publishedUtc: '2026-10-01T09:00:00Z',
  createdUtc: '2026-10-01T08:00:00Z',
  updatedUtc: '2026-10-01T09:00:00Z',
  status: 'published',
  version: 'version-one',
};
export const draftContent: AdminContent = {
  ...publishedContent,
  status: 'draft',
  publishedUtc: null,
  summary: '',
  body: '',
};
