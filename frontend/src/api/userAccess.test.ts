import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './identity';
import { userAccess, userAccessFrom, userAccessMessage } from './userAccess';
import { json } from '../test/fixtures';
const instant = '2026-10-06T12:34:56.1234567+00:00';
describe('Last authenticated-session access contract', () => {
  it('keeps only an actual UTC timestamp or explicit null and strips unrelated personal data', () => {
    expect(
      userAccessFrom({ lastSignInUtc: instant, email: 'private@example.test', ip: '192.0.2.1' }),
    ).toEqual({ lastSignInUtc: instant });
    expect(userAccessFrom({ lastSignInUtc: null, userId: 'private-id' })).toEqual({
      lastSignInUtc: null,
    });
  });
  it.each(['2026-10-06T12:34:56Z', '2026-10-06T12:34:56.1Z', '2026-10-06T12:34:56+00:00'])(
    'accepts valid UTC %s without synthesizing a date',
    (value) => {
      expect(userAccessFrom({ lastSignInUtc: value })).toEqual({ lastSignInUtc: value });
    },
  );
  it.each([
    undefined,
    null,
    [],
    {},
    { lastSignInUtc: undefined },
    { lastSignInUtc: 0 },
    { lastSignInUtc: '' },
    { lastSignInUtc: 'null' },
    { lastSignInUtc: '2026-10-06' },
    { lastSignInUtc: '2026-10-06T12:00:00' },
    { lastSignInUtc: '2026-10-06T12:00:00-05:00' },
    { lastSignInUtc: '2026-02-31T12:00:00Z' },
    { lastSignInUtc: '2026-10-06T25:00:00Z' },
    { lastSignInUtc: '2026-10-06T12:00:60Z' },
    { lastSignInUtc: '0000-01-01T00:00:00Z' },
    { lastSignInUtc: '<script>bad</script>' },
  ])('rejects malformed access response %#', (value) => {
    expect(() => userAccessFrom(value)).toThrow(ApiError);
  });
  it('issues only the exact GET with no-store, no body, no CSRF and no browser storage', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetch = vi.fn().mockResolvedValue(json({ lastSignInUtc: instant }));
    vi.stubGlobal('fetch', fetch);
    await expect(userAccess('id / one')).resolves.toEqual({ lastSignInUtc: instant });
    expect(fetch).toHaveBeenCalledOnce();
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/v1/admin/users/id%20%2F%20one/access');
    expect(fetch.mock.calls[0]?.[1]).toMatchObject({
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    });
    expect(fetch.mock.calls[0]?.[1]).not.toHaveProperty('body');
    expect(fetch.mock.calls[0]?.[1]?.headers).not.toHaveProperty('X-CSRF-TOKEN');
    expect(storage).not.toHaveBeenCalled();
  });
  it('does not send an already-cancelled GET and propagates cancellation to the actual fetch signal', async () => {
    const first = new AbortController();
    first.abort();
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    await expect(userAccess('first', first.signal)).rejects.toMatchObject({ code: 'timeout' });
    expect(fetch).not.toHaveBeenCalled();
    let received: AbortSignal | undefined;
    fetch.mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          received = init.signal as AbortSignal;
          received.addEventListener('abort', () =>
            reject(new DOMException('Aborted', 'AbortError')),
          );
        }),
    );
    const controller = new AbortController();
    const pending = userAccess('second', controller.signal);
    controller.abort();
    await expect(pending).rejects.toMatchObject({ code: 'timeout' });
    expect(received?.aborted).toBe(true);
  });
  it('rejects a late response after cancellation without expiring a later session', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(pending));
    const expired = vi.fn();
    window.addEventListener('acropolis:session-expired', expired);
    try {
      const controller = new AbortController();
      const value = userAccess('first', controller.signal);
      controller.abort();
      resolve(json({ code: 'unauthorized' }, 401));
      await expect(value).rejects.toMatchObject({ code: 'timeout' });
      expect(expired).not.toHaveBeenCalled();
    } finally {
      window.removeEventListener('acropolis:session-expired', expired);
    }
  });
  it('maps a real network rejection to a safe retryable message', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('private network detail')));
    const error = await userAccess('first').catch((value: unknown) => value);
    expect(error).toMatchObject({ status: 0, code: 'unavailable' });
    expect(userAccessMessage(error)).toBe(
      'No pudimos consultar el último ingreso. Inténtalo de nuevo.',
    );
  });
  it.each([
    [403, 'forbidden'],
    [403, 'mfa_required_for_admin'],
    [401, 'unauthorized'],
    [404, 'not_found'],
    [503, 'service_unavailable'],
  ])('preserves HTTP %i/%s with a safe local message', async (status, code) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(json({ code, detail: 'private database detail' }, Number(status))),
    );
    const error = await userAccess('first').catch((value: unknown) => value);
    expect(error).toBeInstanceOf(ApiError);
    expect(userAccessMessage(error)).not.toContain('private');
  });
});
