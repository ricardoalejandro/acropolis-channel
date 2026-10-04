import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, clearCsrf, errorMessage, identity, request, userFrom } from './identity';
import { user, json } from '../test/fixtures';
beforeEach(clearCsrf);
describe('Identity API contract and browser security', () => {
  it('validates the complete user rather than trusting malformed JSON', () => {
    expect(userFrom(user)).toEqual(user);
    for (const value of [
      null,
      [],
      {},
      { ...user, id: 5 },
      { ...user, displayName: 2 },
      { ...user, email: 2 },
      { ...user, emailConfirmed: 'true' },
      { ...user, status: 'other' },
      { ...user, levels: 'Externo' },
      { ...user, levels: ['Administrator'] },
      { ...user, permissions: null },
      { ...user, permissions: [1] },
      { ...user, version: 1 },
    ])
      expect(() => userFrom(value)).toThrow(ApiError);
  });
  it('uses same-origin credentials and antiforgery on writes, and caches only the request token in memory', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({ token: 'test-csrf' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetch);
    await request('/identity/confirm-email', { userId: 'u', token: 'sample-token' });
    await request('/identity/reset-password', { newPassword: 'a very long password' });
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/v1/identity/csrf');
    expect(fetch.mock.calls[1]?.[1]).toMatchObject({
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': 'test-csrf' },
    });
    expect(fetch.mock.calls[1]?.[0]).not.toContain('sample-token');
    expect(localStorage.length).toBe(0);
  });
  it('refreshes antiforgery after login and logout, maps profile and detail contracts', async () => {
    const fetch = vi
      .fn()
      .mockImplementation((path: string) =>
        Promise.resolve(
          path.endsWith('/csrf')
            ? json({ token: 'fresh' })
            : path.endsWith('/logout')
              ? new Response(null, { status: 204 })
              : json(user),
        ),
      );
    vi.stubGlobal('fetch', fetch);
    expect(await identity.me()).toEqual(user);
    expect(await identity.login('a@b.test', 'password')).toEqual(user);
    expect(await identity.updateProfile('New Name')).toEqual(user);
    await identity.logout();
    await identity.updateUser('id/with slash', { version: 'v' });
    expect(await identity.user('id/with slash')).toEqual(user);
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/csrf'))).toHaveLength(3);
    expect(fetch.mock.calls.some(([path]) => path.endsWith('id%2Fwith%20slash'))).toBe(true);
    expect(fetch.mock.calls.some(([, options]) => options.method === 'PATCH')).toBe(true);
  });
  it('validates pagination and item shapes', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({ items: [user], total: 1, page: 1, pageSize: 20 }))
      .mockResolvedValueOnce(json({ items: [] }))
      .mockResolvedValueOnce(json({ items: [], total: 0, page: '1', pageSize: 20 }))
      .mockResolvedValueOnce(json({ items: [], total: 0, page: 1, pageSize: '20' }));
    vi.stubGlobal('fetch', fetch);
    expect((await identity.users('  query', 'active', 'Externo', 1)).items).toEqual([user]);
    expect(fetch.mock.calls[0]?.[0]).toContain('pageSize=20');
    for (let i = 0; i < 3; i++)
      await expect(identity.users('', '', '', 1)).rejects.toMatchObject({
        code: 'invalid_response',
      });
  });
  it('rejects invalid or empty antiforgery tokens before sending credentials', async () => {
    for (const token of [{}, { token: 1 }, { token: '' }, null]) {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(token)));
      await expect(identity.login('a@b.test', 'secret')).rejects.toMatchObject({
        code: 'invalid_response',
      });
      clearCsrf();
    }
  });
  it('sanitizes raw server details and accepts only shaped validation errors', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(
      json(
        {
          code: 'validation_error',
          detail: 'private server detail',
          fieldErrors: { DisplayName: ['message'], Invalid: 'not array', Mixed: [1] },
        },
        400,
      ),
    );
    vi.stubGlobal('fetch', fetch);
    const failure = await request('/test').catch((error: unknown) => error);
    expect(failure).toMatchObject({
      code: 'validation_error',
      fields: { DisplayName: ['message'] },
    });
    expect(String(failure)).not.toContain('private server detail');
    expect(errorMessage('unknown')).not.toContain('unknown');
  });
  it('clears a rejected antiforgery token without silently repeating a mutation', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({ token: 'old' }))
      .mockResolvedValueOnce(json({ code: 'csrf_invalid' }, 400))
      .mockResolvedValueOnce(json({ token: 'new' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetch);
    await expect(request('/test', {})).rejects.toMatchObject({ code: 'csrf_invalid' });
    expect(fetch).toHaveBeenCalledTimes(2);
    await request('/test', {});
    expect(fetch).toHaveBeenCalledTimes(4);
  });
  it('signals expired protected sessions, but not initial anonymous state or failed login', async () => {
    const listener = vi.fn();
    window.addEventListener('acropolis:session-expired', listener);
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({}, 401))
      .mockResolvedValueOnce(json({ token: 't' }))
      .mockResolvedValueOnce(json({ code: 'invalid_credentials' }, 401))
      .mockResolvedValueOnce(json({}, 401));
    vi.stubGlobal('fetch', fetch);
    await expect(identity.me()).rejects.toBeInstanceOf(ApiError);
    await expect(identity.login('a@b.test', 'p')).rejects.toBeInstanceOf(ApiError);
    expect(listener).not.toHaveBeenCalled();
    await expect(request('/protected')).rejects.toBeInstanceOf(ApiError);
    expect(listener).toHaveBeenCalledOnce();
    window.removeEventListener('acropolis:session-expired', listener);
  });
  it('rejects invalid JSON, handles status-only rate limits and network failures safely', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(new Response('html'))
        .mockResolvedValueOnce(new Response('html', { status: 404 }))
        .mockResolvedValueOnce(json({}, 429))
        .mockRejectedValueOnce(new Error('secret network internals')),
    );
    await expect(request('/test')).rejects.toMatchObject({ code: 'invalid_response' });
    await expect(request('/test')).rejects.toMatchObject({ code: 'unavailable' });
    await expect(request('/test')).rejects.toMatchObject({ code: 'rate_limited' });
    await expect(request('/test')).rejects.toMatchObject({ code: 'unavailable' });
  });
  it('times out requests and clears the timer on completion', async () => {
    vi.useFakeTimers();
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (_url, options: RequestInit) =>
          new Promise((_resolve, reject) =>
            options.signal?.addEventListener('abort', () => reject(new Error('aborted'))),
          ),
      ),
    );
    const pending = expect(request('/test')).rejects.toMatchObject({ code: 'timeout' });
    await vi.advanceTimersByTimeAsync(10000);
    await pending;
    expect(vi.getTimerCount()).toBe(0);
  });
  it('shares a single antiforgery fetch across concurrent form mutations', async () => {
    let release: ((response: Response) => void) | undefined;
    const fetch = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            release = resolve;
          }),
      )
      .mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetch);
    const first = request('/first', {});
    const second = request('/second', {});
    expect(fetch).toHaveBeenCalledOnce();
    release?.(json({ token: 'shared-request-token' }));
    await Promise.all([first, second]);
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(
      fetch.mock.calls
        .slice(1)
        .every(([, options]) => options.headers['X-CSRF-TOKEN'] === 'shared-request-token'),
    ).toBe(true);
  });
  it('never submits credentials with a token requested for a previous identity', async () => {
    let release: ((response: Response) => void) | undefined;
    const fetch = vi.fn(
      () =>
        new Promise<Response>((resolve) => {
          release = resolve;
        }),
    );
    vi.stubGlobal('fetch', fetch);
    const result = expect(request('/test', {})).rejects.toMatchObject({ code: 'csrf_invalid' });
    clearCsrf();
    release?.(json({ token: 'old-identity-token' }));
    await result;
    expect(fetch).toHaveBeenCalledOnce();
  });
});
