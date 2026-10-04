import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  adminDetailFrom,
  adminFrom,
  catalog,
  catalogMessage,
  categories,
  categoryLabel,
  contentQuery,
  detailFrom,
  durationLabel,
  summaryFrom,
} from './catalog';
import { ApiError, clearCsrf } from './identity';
import { json } from '../test/fixtures';
import { draftContent, publishedContent as item } from '../test/catalogFixtures';
beforeEach(clearCsrf);
describe('Real editorial catalogue contracts', () => {
  it('accepts safe metadata and validates every field including only local covers', () => {
    expect(summaryFrom(item)).toEqual(item);
    expect(
      summaryFrom({ ...item, coverAsset: null, durationSeconds: null, publishedUtc: null }),
    ).toBeTruthy();
    for (const value of [
      null,
      [],
      {},
      { ...item, id: 1 },
      { ...item, slug: 1 },
      { ...item, title: null },
      { ...item, summary: 1 },
      { ...item, category: 'invented' },
      { ...item, coverAsset: 'https://example.test/image' },
      { ...item, durationSeconds: 0 },
      { ...item, durationSeconds: 86401 },
      { ...item, durationSeconds: 1.5 },
      { ...item, publishedUtc: 'invalid' },
    ])
      expect(() => summaryFrom(value)).toThrow(ApiError);
    expect(detailFrom(item).body).toBe(item.body);
    expect(adminFrom(draftContent).publishedUtc).toBeNull();
    expect(adminDetailFrom(item).status).toBe('published');
    for (const value of [
      { ...item, body: null },
      { ...item, updatedUtc: null },
    ])
      expect(() => detailFrom(value)).toThrow(ApiError);
    for (const value of [
      { ...item, status: 'other' },
      { ...item, createdUtc: null },
      { ...item, updatedUtc: null },
      { ...item, version: null },
      { ...item, version: '' },
    ])
      expect(() => adminFrom(value)).toThrow(ApiError);
    expect(() => adminDetailFrom({ ...item, body: [] })).toThrow(ApiError);
  });
  it('requires the exact six categories without missing, duplicate or invented entries', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({ items: categories }))
      .mockResolvedValueOnce(json({ items: categories.slice(1) }))
      .mockResolvedValueOnce(json({ items: categories.map(() => categories[0]) }))
      .mockResolvedValueOnce(json(null));
    vi.stubGlobal('fetch', fetch);
    expect(await catalog.categories()).toEqual(categories);
    for (let i = 0; i < 3; i++)
      await expect(catalog.categories()).rejects.toMatchObject({ code: 'invalid_response' });
  });
  it('maps public and administrative endpoints, escapes filters, and sends CSRF on create and versioned PUT', async () => {
    const fetch = vi.fn((path: string) =>
      Promise.resolve(
        path.endsWith('/csrf')
          ? json({ token: 'csrf' })
          : path.includes('?')
            ? json({ items: [item], total: 1, page: 1, pageSize: 20 })
            : json(item),
      ),
    );
    vi.stubGlobal('fetch', fetch);
    expect(
      (await catalog.list({ search: '100% _\\', category: 'videos', page: 2 })).items[0],
    ).toEqual(item);
    await catalog.adminList({ status: 'draft', pageSize: 100 });
    await catalog.detail('slug/escaped');
    await catalog.adminDetail('id/escaped');
    const fields = {
      title: item.title,
      slug: item.slug,
      category: item.category,
      coverAsset: item.coverAsset,
      durationSeconds: null,
      summary: item.summary,
      body: item.body,
    };
    await catalog.create(fields);
    await catalog.update(item.id, fields, 'published', 'exact-version');
    expect(fetch.mock.calls[0]?.[0]).toContain(
      'search=100%25+_%5C&category=videos&page=2&pageSize=20',
    );
    expect(fetch.mock.calls[1]?.[0]).toContain('status=draft&page=1&pageSize=100');
    expect(fetch.mock.calls[2]?.[0]).toContain('slug%2Fescaped');
    expect(fetch.mock.calls[3]?.[0]).toContain('id%2Fescaped');
    const calls = vi.mocked(fetch).mock.calls as unknown as [string, RequestInit][];
    const put = calls.find(([, options]) => options?.method === 'PUT');
    expect(put?.[1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf' });
    expect(JSON.parse(String(put?.[1]?.body))).toEqual({
      ...fields,
      status: 'published',
      version: 'exact-version',
    });
    expect(calls.filter(([path]) => path.endsWith('/csrf'))).toHaveLength(1);
  });
  it('rejects malformed pagination and administrative list rows', async () => {
    const valid = { items: [item], total: 1, page: 1, pageSize: 20 };
    for (const data of [
      null,
      { ...valid, items: null },
      { ...valid, total: -1 },
      { ...valid, total: 1.5 },
      { ...valid, page: 0 },
      { ...valid, page: '1' },
      { ...valid, pageSize: 0 },
      { ...valid, pageSize: 101 },
      { ...valid, pageSize: '20' },
      { ...valid, items: [{ ...item, version: '' }] },
    ]) {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(data)));
      await expect(catalog.adminList()).rejects.toMatchObject({ code: 'invalid_response' });
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(valid)));
    expect((await catalog.list()).total).toBe(1);
  });
  it('normalizes URL filters and page bounds and formats only real durations', () => {
    expect(contentQuery(new URLSearchParams('category=podcast&search=escuchar&page=3'))).toEqual({
      category: 'podcast',
      search: 'escuchar',
      page: 3,
    });
    for (const page of ['', '0', '-1', '1.5', '1000001', 'no'])
      expect(contentQuery(new URLSearchParams('page=' + page)).page).toBe(1);
    expect(
      contentQuery(new URLSearchParams('category=unknown&search=' + 'x'.repeat(101))).search,
    ).toHaveLength(100);
    expect(contentQuery(new URLSearchParams('category=unknown')).category).toBe('');
    expect(durationLabel(45)).toBe('45 s');
    expect(durationLabel(120)).toBe('2 min');
    expect(durationLabel(145)).toBe('2 min 25 s');
    expect(categoryLabel('podcast')).toBe('Podcast');
    expect(categoryLabel('unknown' as 'podcast')).toBe('unknown');
  });
  it('uses known error messages without exposing provider or server details', () => {
    expect(catalogMessage(new Error('secret'))).not.toContain('secret');
    expect(catalogMessage(new ApiError(409, 'concurrency_conflict'))).toContain(
      'Conservamos tus cambios',
    );
    expect(catalogMessage(new ApiError(409, 'slug_conflict'))).toContain('otro contenido');
    expect(catalogMessage(new ApiError(409, 'invalid_transition'))).toContain('borrador');
    expect(catalogMessage(new ApiError(403, 'forbidden'))).toContain('permiso');
  });
});
