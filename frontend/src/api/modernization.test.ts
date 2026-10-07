import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, clearCsrf, identity, userFrom } from './identity';
import { consumption, workFrom } from './consumption';
import { audit, auditPageFrom } from './audit';
import { catalog, adminDetailFrom, detailFrom } from './catalog';
import {
  subscriptions,
  subscriptionFrom,
  subscriptionAccountFrom,
  subscriptionAccountsFrom,
  subscriptionMeFrom,
  subscriptionMessage,
} from './subscriptions';
import { user, json } from '../test/fixtures';
const now = '2026-10-06T12:00:00Z';
const sub = {
  id: 'sub',
  userId: 'person',
  plan: 'free_beta',
  status: 'active',
  createdUtc: now,
  activatedUtc: now,
  updatedUtc: now,
  cancelledUtc: null,
  expiresUtc: null,
  version: 'v1',
};
const summary = {
  id: 'work',
  slug: 'lectura',
  title: 'Lectura',
  category: 'lecturas',
  summary: 'Pública',
  coverAsset: null,
  durationSeconds: null,
  publishedUtc: now,
  author: 'Autora',
  tags: ['Cultura'],
  collectionKind: null,
};
const work = {
  version: '1234567890abcdef1234567890abcdef',
  id: 'work',
  slug: 'lectura',
  title: 'Lectura',
  category: 'lecturas',
  workText: 'Texto protegido',
  youTubeId: null,
  collectionKind: null,
  items: [],
};
const page = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 20 });
function mock(body: unknown) {
  const fetch = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => (
      void init,
      String(input).endsWith('/csrf') ? json({ token: 'csrf' }) : json(body)
    ),
  );
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
beforeEach(clearCsrf);
describe('Free subscription contracts', () => {
  it.each([null, '2026-10-06T12:01:00Z'])(
    'accepts non-expiring views including cancellation dates %s',
    (cancelledUtc) => {
      expect(subscriptionFrom({ ...sub, cancelledUtc }).expiresUtc).toBeNull();
    },
  );
  it.each([
    null,
    [],
    { ...sub, id: '' },
    { ...sub, plan: 'paid' },
    { ...sub, status: 'unknown' },
    { ...sub, createdUtc: 'bad' },
    { ...sub, activatedUtc: null },
    { ...sub, cancelledUtc: 'bad' },
    { ...sub, expiresUtc: now },
  ])('rejects a malformed or paid subscription %#', (value) => {
    expect(() => subscriptionFrom(value)).toThrow(ApiError);
  });
  it('accepts absent and existing subscription and rejects malformed eligibility', () => {
    expect(subscriptionMeFrom({ subscription: null, eligibleToActivate: true })).toEqual({
      subscription: null,
      eligibleToActivate: true,
    });
    expect(
      subscriptionMeFrom({ subscription: sub, eligibleToActivate: false }).subscription,
    ).toEqual(sub);
    expect(() => subscriptionMeFrom({ subscription: null, eligibleToActivate: 'yes' })).toThrow(
      ApiError,
    );
    expect(() => subscriptionMeFrom(null)).toThrow(ApiError);
  });
  it('uses explicit mutation payloads with CSRF and versions', async () => {
    const fetch = mock(sub);
    await subscriptions.activate();
    await subscriptions.cancel('v1');
    await subscriptions.update('sub/one', 'v2', 'suspended', 'Revisión');
    const writes = fetch.mock.calls.filter(([, init]) => init?.body);
    expect(writes).toHaveLength(3);
    expect(writes.map(([, init]) => JSON.parse(String(init?.body)))).toEqual([
      {},
      { version: 'v1' },
      { version: 'v2', status: 'suspended', reason: 'Revisión' },
    ]);
    expect(writes[2]?.[0]).toBe('/api/v1/admin/subscriptions/sub%2Fone');
    expect(writes[2]?.[1]?.method).toBe('PATCH');
    expect(writes[0]?.[1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf' });
  });
  it('reads me, detail and filtered pagination and omits empty filters', async () => {
    const fetch = mock({ subscription: sub, eligibleToActivate: true });
    await subscriptions.me();
    mock(sub);
    await subscriptions.detail('sub/one');
    const filtered = mock(page([sub]));
    await subscriptions.list('active', 'person', 2);
    await subscriptions.list('', '');
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/v1/subscriptions/me');
    expect(filtered.mock.calls[0]?.[0]).toContain('status=active&userId=person&page=2&pageSize=20');
    expect(filtered.mock.calls[1]?.[0]).toContain('?page=1&pageSize=20');
  });
  it.each([
    { items: [], total: -1, page: 1, pageSize: 20 },
    { items: [], total: 0, page: 0, pageSize: 20 },
    { items: [], total: 0, page: 1, pageSize: 101 },
    { items: null, total: 0, page: 1, pageSize: 20 },
    null,
  ])('rejects malformed subscription pagination %#', async (value) => {
    mock(value);
    await expect(subscriptions.list('', '')).rejects.toBeInstanceOf(ApiError);
  });
  it('parses safe subscription audit rows and rejects malformed fields', async () => {
    const entry = {
      id: 'event',
      subscriptionId: 'sub',
      userId: 'person',
      actorId: 'actor',
      action: 'subscription.activated',
      beforeStatus: null,
      afterStatus: 'active',
      reason: '',
      createdUtc: now,
    };
    const fetch = mock(page([entry]));
    expect((await subscriptions.audit('sub', 2)).items[0]).toEqual(entry);
    expect(fetch.mock.calls[0]?.[0]).toContain('subscriptionId=sub&page=2');
    for (const broken of [
      { ...entry, beforeStatus: 1 },
      { ...entry, reason: null },
      { ...entry, createdUtc: 'bad' },
      null,
    ]) {
      mock(page([broken]));
      await expect(subscriptions.audit()).rejects.toBeInstanceOf(ApiError);
    }
  });
  it.each([
    'subscription_suspended',
    'subscription_required',
    'email_confirmation_required',
    'concurrency_conflict',
    'unknown',
  ])('uses safe messages for %s', (code) => {
    expect(subscriptionMessage(new ApiError(403, code))).not.toBe('');
    expect(subscriptionMessage(new Error('secret'))).not.toContain('secret');
  });
});
describe('Protected work and editorial contracts', () => {
  it('parses full readings, videos and ordered course summaries without interpreting HTML', async () => {
    expect(workFrom({ ...work, workText: '<script>literal</script>' }).workText).toBe(
      '<script>literal</script>',
    );
    expect(
      workFrom({ ...work, category: 'videos', workText: null, youTubeId: 'M7lc1UVf-VE' }).youTubeId,
    ).toBe('M7lc1UVf-VE');
    expect(
      workFrom({
        ...work,
        category: 'cursos',
        workText: null,
        collectionKind: 'course',
        items: [summary],
      }).items,
    ).toEqual([summary]);
    const fetch = mock(work);
    expect(await consumption.content('lectura/one')).toEqual(work);
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/v1/consumption/content/lectura%2Fone');
  });
  it.each([
    null,
    [],
    { ...work, id: 1 },
    { ...work, workText: 1 },
    { ...work, youTubeId: 'bad' },
    { ...work, collectionKind: 'series' },
    { ...work, items: null },
    { ...work, items: [{}] },
  ])('rejects malformed protected-work response %#', (value) => {
    expect(() => workFrom(value)).toThrow(ApiError);
  });
  it('preserves editorial author, tags, work fields and ordered item IDs, while public detail has no work payload', () => {
    const detail = { ...summary, body: 'Sinopsis pública', updatedUtc: now, items: [summary] };
    expect(detailFrom(detail).items).toEqual([summary]);
    const admin = {
      ...detail,
      status: 'draft',
      createdUtc: now,
      version: 'v1',
      workText: 'Protegido',
      youTubeId: null,
      itemIds: ['work'],
    };
    expect(adminDetailFrom(admin)).toMatchObject({
      workText: 'Protegido',
      itemIds: ['work'],
      tags: ['Cultura'],
    });
    for (const broken of [
      { ...admin, workText: 7 },
      { ...admin, youTubeId: 'bad' },
      { ...admin, itemIds: [1] },
    ])
      expect(() => adminDetailFrom(broken)).toThrow(ApiError);
    expect(() => detailFrom({ ...detail, items: null })).toThrow(ApiError);
  });
  it('sends agreed editorial fields and parses object audit history', async () => {
    const view = {
      ...summary,
      body: 'Pública',
      createdUtc: now,
      updatedUtc: now,
      version: 'v1',
      status: 'draft',
      workText: 'Privada',
      youTubeId: null,
      itemIds: [],
    };
    const fetch = mock(view);
    const fields = {
      title: summary.title,
      slug: summary.slug,
      summary: summary.summary,
      body: 'Pública',
      category: 'lecturas' as const,
      coverAsset: null,
      durationSeconds: null,
      author: 'Autora',
      tags: ['Cultura'],
      workText: 'Privada',
      youTubeId: null,
      collectionKind: null,
      itemIds: [],
    };
    await catalog.create(fields);
    expect(JSON.parse(String(fetch.mock.calls.find(([, init]) => init?.body)?.[1]?.body))).toEqual(
      fields,
    );
    const entry = {
      id: 'event',
      actorId: 'actor',
      contentId: 'work',
      action: 'content.created',
      changes: '{"fields":["workText"]}',
      createdUtc: now,
    };
    const auditFetch = mock(page([entry]));
    expect((await catalog.audit('work', 2)).items).toEqual([entry]);
    expect(auditFetch.mock.calls[0]?.[0]).toContain('/admin/content/work/audit?page=2');
    mock(page([{ ...entry, changes: null }]));
    await expect(catalog.audit('work')).rejects.toBeInstanceOf(ApiError);
  });
});
describe('Scoped audit and owner contracts', () => {
  const base = { id: 'event', actorId: 'actor', action: 'event', createdUtc: now };
  it('normalizes the three safe audit descriptions', () => {
    expect(
      auditPageFrom(
        page([{ ...base, userId: 'person', changes: { fields: ['permissions'] } }]),
        'users',
      ).items[0]?.description,
    ).toBe('permisos');
    expect(
      auditPageFrom(
        page([
          {
            ...base,
            contentId: 'work',
            changes: '{"status":"published","before":{"titleChanged":true}}',
          },
        ]),
        'content',
      ).items[0]?.description,
    ).toContain('título');
    expect(
      auditPageFrom(
        page([
          { ...base, subscriptionId: 'sub', beforeStatus: null, afterStatus: 'active', reason: '' },
        ]),
        'subscriptions',
      ).items[0]?.description,
    ).toBe('active');
    expect(
      auditPageFrom(
        page([
          {
            ...base,
            subscriptionId: 'sub',
            beforeStatus: 'active',
            afterStatus: 'suspended',
            reason: 'Revisión',
          },
        ]),
        'subscriptions',
      ).items[0]?.description,
    ).toBe('active → suspended: Revisión');
  });
  it.each([
    null,
    { items: [], total: -1, page: 1, pageSize: 20 },
    { items: [], total: 0, page: 0, pageSize: 20 },
    { items: [], total: 0, page: 1, pageSize: 0 },
    { items: [], total: 0, page: 1, pageSize: 101 },
  ])('rejects invalid audit pagination %#', (value) => {
    expect(() => auditPageFrom(value, 'users')).toThrow(ApiError);
  });
  it('rejects malformed per-module fields and dates', () => {
    for (const broken of [
      { ...base, userId: 'person', changes: { fields: [1] } },
      { ...base, userId: 'person', changes: null },
      { ...base, userId: 'person', changes: { fields: [] }, createdUtc: 'bad' },
      null,
    ])
      expect(() => auditPageFrom(page([broken]), 'users')).toThrow(ApiError);
    expect(() =>
      auditPageFrom(page([{ ...base, contentId: 'work', changes: 1 }]), 'content'),
    ).toThrow(ApiError);
    for (const broken of [
      { ...base, subscriptionId: 'sub', beforeStatus: 1, afterStatus: 'active', reason: '' },
      { ...base, subscriptionId: 'sub', beforeStatus: null, afterStatus: null, reason: '' },
    ])
      expect(() => auditPageFrom(page([broken]), 'subscriptions')).toThrow(ApiError);
  });
  it('whitelists audit query keys and preserves UTC dates, action, object ID and page', async () => {
    const fetch = mock(page([]));
    await audit.list(
      'content',
      new URLSearchParams({
        fromUtc: now,
        toUtc: now,
        action: 'content.updated',
        contentId: 'work',
        page: '2',
        token: 'private',
        userId: 'irrelevant',
      }),
    );
    const route = new URL('http://test' + String(fetch.mock.calls[0]?.[0]));
    expect(route.pathname).toBe('/api/v1/admin/content/audit');
    expect(route.searchParams.get('pageSize')).toBe('20');
    expect(route.searchParams.get('fromUtc')).toBe(now);
    expect(route.searchParams.has('token')).toBe(false);
    expect(route.searchParams.has('userId')).toBe(false);
  });
  it('parses protected owner and uses the distinct permissions endpoint', async () => {
    expect(userFrom({ ...user, isOwner: true }).isOwner).toBe(true);
    expect(() => userFrom({ ...user, isOwner: 'yes' })).toThrow(ApiError);
    const fetch = mock({ ...user, isOwner: false, permissions: ['Content.Manage'] });
    await identity.permissions('person/one', 'v1', ['Content.Manage']);
    const call = fetch.mock.calls.find(([, init]) => init?.body);
    expect(call?.[0]).toBe('/api/v1/admin/users/person%2Fone/permissions');
    expect(call?.[1]?.method).toBe('PUT');
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      version: 'v1',
      permissions: ['Content.Manage'],
    });
  });
});

describe('Minimal subscriptions directory', () => {
  it('parses only the safe account fields and strips unrelated permissions', async () => {
    const account = {
      id: user.id,
      displayName: user.displayName,
      email: user.email,
      status: 'active',
      emailConfirmed: true,
    };
    expect(
      subscriptionAccountFrom({ ...account, permissions: ['Users.Manage'], isOwner: true }),
    ).toEqual(account);
    const fetch = mock(page([account]));
    expect((await subscriptions.accounts('Nombre o correo', 2)).items).toEqual([account]);
    expect(fetch.mock.calls[0]?.[0]).toContain(
      '/admin/subscriptions/accounts?search=Nombre+o+correo&page=2&pageSize=20',
    );
    await subscriptions.accounts();
    expect(fetch.mock.calls[1]?.[0]).toContain('?page=1&pageSize=20');
  });
  it.each([
    null,
    [],
    { id: '', displayName: 'Nombre', email: 'a@test', status: 'active', emailConfirmed: true },
    { id: 'id', displayName: 'Nombre', email: 'a@test', status: 'suspended', emailConfirmed: true },
    { id: 'id', displayName: 'Nombre', email: 'a@test', status: 'active', emailConfirmed: 'yes' },
  ])('rejects invalid directory accounts %#', (value) => {
    expect(() => subscriptionAccountFrom(value)).toThrow(ApiError);
  });
  it('renders only supported editorial flags and uses a safe fallback for unknown changes', () => {
    const event = {
      id: 'e',
      actorId: 'a',
      contentId: 'c',
      action: 'content.created',
      createdUtc: now,
    };
    for (const changes of [
      'bad-json',
      '[]',
      '{}',
      JSON.stringify({ unexpectedSecret: 'private text' }),
    ]) {
      const description = auditPageFrom(page([{ ...event, changes }]), 'content').items[0]
        ?.description;
      expect(description).toBe('Cambio editorial registrado');
      expect(description).not.toContain('private text');
    }
    expect(
      auditPageFrom(
        page([
          { ...event, changes: JSON.stringify({ status: 'draft', hasWork: true, itemCount: 2 }) },
        ]),
        'content',
      ).items[0]?.description,
    ).toBe('Borrador · con obra completa · 2 elementos');
    expect(
      auditPageFrom(
        page([
          {
            ...event,
            changes: JSON.stringify({
              status: 'published',
              before: {
                workChanged: true,
                metadataChanged: true,
                collectionChanged: true,
                synopsisChanged: true,
              },
              itemCount: 0,
            }),
          },
        ]),
        'content',
      ).items[0]?.description,
    ).toContain('obra completa');
  });
});

describe('Bounded subscription account lookup', () => {
  const account = {
    id: '10000000-0000-0000-0000-000000000001',
    displayName: 'Persona',
    email: 'person@example.test',
    status: 'active',
    emailConfirmed: true,
  };
  it('accepts missing rows and keeps only the five authorized account fields', () => {
    expect(subscriptionAccountsFrom([])).toEqual([]);
    expect(
      subscriptionAccountsFrom([{ ...account, permissions: ['Users.Manage'], isOwner: true }]),
    ).toEqual([account]);
  });
  it.each([
    {},
    [null],
    [{ ...account, emailConfirmed: 'true' }],
    [account, account],
    Array.from({ length: 21 }, (_, index) => ({ ...account, id: String(index) })),
  ])('rejects malformed or oversized metadata %j', (value) => {
    expect(() => subscriptionAccountsFrom(value)).toThrow(ApiError);
  });
  it('requests one bounded lookup using IDs and accepts omitted accounts without exposing names in the URL', async () => {
    const fetch = mock([account]);
    expect(
      await subscriptions.lookupAccounts([account.id, '10000000-0000-0000-0000-000000000002']),
    ).toEqual([account]);
    expect(fetch).toHaveBeenCalledOnce();
    const url = String(fetch.mock.calls[0]?.[0]);
    expect(url).toContain(
      '/accounts/lookup?userIds=' + account.id + '%2C10000000-0000-0000-0000-000000000002',
    );
    expect(url).not.toMatch(/Persona|person@example.test/);
    expect(await subscriptions.lookupAccounts([])).toEqual([]);
    expect(fetch).toHaveBeenCalledOnce();
  });
  it('rejects duplicate, empty and oversized requests before HTTP, and unexpected response IDs', async () => {
    const fetch = mock([{ ...account, id: 'unexpected' }]);
    for (const ids of [
      [account.id, account.id],
      [''],
      ['not-a-guid'],
      ['00000000-0000-0000-0000-000000000000'],
      [account.id, account.id.toUpperCase()],
      Array.from(
        { length: 21 },
        (_, index) => '10000000-0000-0000-0000-' + String(index + 1).padStart(12, '0'),
      ),
    ])
      await expect(subscriptions.lookupAccounts(ids)).rejects.toThrow(ApiError);
    expect(fetch).not.toHaveBeenCalled();
    await expect(subscriptions.lookupAccounts([account.id])).rejects.toThrow(ApiError);
  });
});

describe('authorized work version binding', () => {
  it.each([undefined, 'v1', 'A'.repeat(32), '1234567890abcdef1234567890abcdef\n'])('rejects missing or noncanonical protected-work version %#', (version) => {
    expect(() => workFrom({ ...work, version })).toThrow(ApiError);
  });
});
