import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, clearCsrf } from './identity';
import {
  topics,
  publicTopicFrom,
  adminTopicFrom,
  publicTopicPageFrom,
  directoryFrom,
  contentTopicsFrom,
  validTopicName,
  validTopicSlug,
  topicMessage,
} from './topics';
import {
  topic,
  topicId,
  contentId,
  topicVersion,
  topicDirectory,
  contentTopics,
} from '../test/topicFixtures';
import { json } from '../test/fixtures';
beforeEach(() => clearCsrf());
describe('Topic metadata and assignment contracts', () => {
  it('strips unrelated and private data at every DTO boundary without adding a format', () => {
    const extra = {
      email: 'private@example.test',
      workText: 'Private text',
      youTubeId: 'AbCdEfGh123',
      actorId: contentId,
    };
    expect(publicTopicFrom({ ...topic, ...extra })).toEqual({
      id: topic.id,
      slug: topic.slug,
      name: topic.name,
      position: topic.position,
    });
    expect(adminTopicFrom({ ...topic, ...extra })).toEqual(topic);
    expect(directoryFrom({ ...topicDirectory, ...extra, items: [{ ...topic, ...extra }] })).toEqual(
      { ...topicDirectory, items: [topic] },
    );
    expect(
      contentTopicsFrom({ ...contentTopics, ...extra, items: [{ ...topic, ...extra }] }),
    ).toEqual(contentTopics);
  });
  it('accepts empty directories and optional associations without creating a fake topic', () => {
    expect(publicTopicPageFrom({ items: [], total: 0, page: 1, pageSize: 100 }).items).toEqual([]);
    expect(directoryFrom({ ...topicDirectory, items: [], total: 0 }).items).toEqual([]);
    expect(contentTopicsFrom({ ...contentTopics, items: [] }).items).toEqual([]);
    expect(validTopicName('  Ideas  ')).toBe(true);
    expect(validTopicSlug('ideas-2')).toBe(true);
    expect(validTopicName('Ideas\n')).toBe(false);
    expect(validTopicName('A')).toBe(false);
    expect(validTopicSlug('Invalid')).toBe(false);
  });
  it.each([
    null,
    [],
    { ...topic, id: '' },
    { ...topic, id: topicId + '\n' },
    { ...topic, id: '00000000-0000-0000-0000-000000000000' },
    { ...topic, slug: 'Invalid' },
    { ...topic, slug: 'a' },
    { ...topic, slug: 'valid-qa\n' },
    { ...topic, name: '' },
    { ...topic, name: 'x\0' },
    { ...topic, name: 'x'.repeat(181) },
    { ...topic, position: -1 },
    { ...topic, position: 2147483648 },
    { ...topic, position: 0.5 },
    { ...topic, position: Number.MAX_SAFE_INTEGER + 1 },
  ])('rejects malformed public topic %#', (value) =>
    expect(() => publicTopicFrom(value)).toThrow(ApiError),
  );
  it.each([
    { ...topic, status: 'draft' },
    { ...topic, version: '' },
    { ...topic, version: topicVersion + '\n' },
  ])('rejects invalid administrative version or state %#', (value) =>
    expect(() => adminTopicFrom(value)).toThrow(ApiError),
  );
  it.each([
    null,
    { ...topicDirectory, directoryVersion: '' },
    { ...topicDirectory, items: null },
    { ...topicDirectory, items: [topic, topic] },
    { ...topicDirectory, total: -1 },
    { ...topicDirectory, total: 0.5 },
    { ...topicDirectory, page: 0 },
    { ...topicDirectory, pageSize: 101 },
    { ...topicDirectory, pageSize: 1 },
  ])('rejects ambiguous or unbounded directories %#', (value) =>
    expect(() => directoryFrom(value)).toThrow(ApiError),
  );
  it.each([
    null,
    { ...contentTopics, contentId: '' },
    { ...contentTopics, contentVersion: '' },
    { ...contentTopics, items: [topic, topic] },
    {
      ...contentTopics,
      items: Array.from({ length: 13 }, (_, index) => ({
        ...topic,
        id: '40000000-0000-0000-0000-' + String(index + 10).padStart(12, '0'),
      })),
    },
  ])('rejects invalid content associations %#', (value) =>
    expect(() => contentTopicsFrom(value)).toThrow(ApiError),
  );
  it('uses narrow real paths and explicit writes with no private data, storage or automatic resend', async () => {
    const store = vi.spyOn(Storage.prototype, 'setItem');
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith('/csrf')) return json({ token: 'synthetic-csrf' });
      if (url.includes('/catalog/topics?') && url.startsWith('/api/v1/catalog'))
        return json({ items: [topic], total: 1, page: 1, pageSize: 100 });
      if (url.endsWith('/topics') && url.includes('/admin/content/')) return json(contentTopics);
      if (init?.method === 'GET' && url.includes('/admin/catalog/topics?'))
        return json(topicDirectory);
      return json(topic);
    });
    vi.stubGlobal('fetch', fetch);
    await topics.publicList();
    await topics.list({ search: '50%_\\', status: 'active' });
    await topics.detail(topicId);
    await topics.assigned(contentId);
    expect(fetch.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
    await topics.create('tema-qa', 'Tema QA');
    await topics.update(topicId, topicVersion, 'Nuevo nombre');
    await topics.state(topicId, topicVersion, 'archived');
    await topics.move(topicVersion, topicId, null);
    await topics.assign(contentId, topicVersion, [topicId]);
    const writes = fetch.mock.calls.filter(([, init]) => init?.method !== 'GET');
    expect(writes).toHaveLength(5);
    expect(writes.map(([url]) => String(url))).toEqual([
      '/api/v1/admin/catalog/topics',
      '/api/v1/admin/catalog/topics/' + topicId,
      '/api/v1/admin/catalog/topics/' + topicId + '/state',
      '/api/v1/admin/catalog/topics/order',
      '/api/v1/admin/content/' + contentId + '/topics',
    ]);
    expect(JSON.parse(String(writes[1]?.[1]?.body))).toEqual({
      version: topicVersion,
      name: 'Nuevo nombre',
    });
    expect(JSON.parse(String(writes[3]?.[1]?.body))).toEqual({
      directoryVersion: topicVersion,
      id: topicId,
      beforeId: null,
    });
    expect(JSON.parse(String(writes[4]?.[1]?.body))).toEqual({
      contentVersion: topicVersion,
      topicIds: [topicId],
    });
    expect(store).not.toHaveBeenCalled();
    for (const [, init] of fetch.mock.calls.filter(
      ([url, init]) => init?.method === 'GET' && !String(url).endsWith('/csrf'),
    ))
      expect(init?.cache).toBe('no-store');
  });
  it('aborts late unauthorized responses without reporting a stale session expiry', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    vi.stubGlobal(
      'fetch',
      vi.fn(() => pending),
    );
    const expired = vi.fn();
    window.addEventListener('acropolis:session-expired', expired);
    const controller = new AbortController();
    const read = topics.detail(topicId, controller.signal);
    controller.abort();
    resolve(json({ code: 'forbidden' }, 401));
    await expect(read).rejects.toMatchObject({ code: 'timeout' });
    expect(expired).not.toHaveBeenCalled();
    window.removeEventListener('acropolis:session-expired', expired);
  });
  it.each([401, 403, 429, 503])(
    'propagates actual HTTP failures without private details %i',
    async (status) => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          json(
            {
              code: status === 403 ? 'forbidden' : 'service_unavailable',
              detail: 'private connection',
            },
            status,
          ),
        ),
      );
      await expect(topics.list()).rejects.toMatchObject({ status });
      expect(topicMessage(new ApiError(status, 'service_unavailable'))).not.toContain('private');
    },
  );
  it('explains conflicts and unavailable topics while preserving retry intent', () => {
    expect(topicMessage(new ApiError(409, 'concurrency_conflict'))).toContain('Conservamos');
    expect(topicMessage(new ApiError(409, 'topic_unavailable'))).toContain('Conservamos');
    expect(topicMessage(new ApiError(409, 'topic_slug_conflict'))).toContain('archivado');
    expect(topicMessage(new Error('private detail'))).not.toContain('private');
  });
});
