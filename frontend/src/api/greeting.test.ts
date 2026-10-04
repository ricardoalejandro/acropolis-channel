import { describe, expect, it, vi } from 'vitest';
import { getGreeting, GreetingError } from './greeting';

function jsonResponse(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' },
  });
}
function abortingFetch() {
  return vi.fn(
    (_url: string, init: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        const signal = init.signal;
        if (signal?.aborted) reject(new DOMException('Private abort detail', 'AbortError'));
        signal?.addEventListener(
          'abort',
          () => reject(new DOMException('Private abort detail', 'AbortError')),
          { once: true },
        );
      }),
  );
}
describe('greeting API contract', () => {
  it('requests the same-origin versioned API and validates its response', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ message: ' Hola mundo ' }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(getGreeting()).resolves.toEqual({ message: 'Hola mundo' });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/greeting',
      expect.objectContaining({
        headers: { Accept: 'application/json' },
        signal: expect.any(AbortSignal),
      }),
    );
  });
  it('never turns an HTTP error into a successful greeting', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ message: 'Internal password detail' }, 503)),
    );
    await expect(getGreeting()).rejects.toMatchObject({
      reason: 'unavailable',
      message: 'No pudimos cargar el saludo. Inténtalo nuevamente.',
    });
  });
  it.each([null, [], {}, { message: 5 }, { message: '' }, { message: '   ' }])(
    'rejects an invalid JSON contract: %j',
    async (payload) => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(payload)));
      await expect(getGreeting()).rejects.toMatchObject({ reason: 'invalid' });
    },
  );
  it('rejects HTML instead of accepting an SPA fallback as an API response', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(
          new Response('<h1>Hola mundo</h1>', { headers: { 'content-type': 'text/html' } }),
        ),
    );
    await expect(getGreeting()).rejects.toMatchObject({ reason: 'invalid' });
  });
  it('rejects a response without a JSON content type', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null)));
    await expect(getGreeting()).rejects.toMatchObject({ reason: 'invalid' });
  });
  it('sanitizes malformed JSON', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(
          new Response('{invalid', { headers: { 'content-type': 'application/json' } }),
        ),
    );
    await expect(getGreeting()).rejects.toMatchObject({ reason: 'invalid' });
  });
  it('does not leak network errors', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('secret upstream token')));
    await expect(getGreeting()).rejects.toMatchObject({
      reason: 'unavailable',
      message: 'No pudimos cargar el saludo. Inténtalo nuevamente.',
    });
  });
  it('aborts at the five-second timeout', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', abortingFetch());
    const assertion = expect(getGreeting()).rejects.toMatchObject({
      reason: 'timeout',
      message: 'La solicitud tardó demasiado. Inténtalo nuevamente.',
    });
    await vi.advanceTimersByTimeAsync(5_000);
    await assertion;
  });
  it('honors cancellation and removes the external listener', async () => {
    vi.stubGlobal('fetch', abortingFetch());
    const controller = new AbortController();
    const remove = vi.spyOn(controller.signal, 'removeEventListener');
    const assertion = expect(getGreeting({ signal: controller.signal })).rejects.toBeInstanceOf(
      GreetingError,
    );
    controller.abort();
    await assertion;
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function));
  });
  it('honors a signal cancelled before the request', async () => {
    vi.stubGlobal('fetch', abortingFetch());
    const controller = new AbortController();
    controller.abort();
    await expect(getGreeting({ signal: controller.signal })).rejects.toMatchObject({
      reason: 'unavailable',
    });
  });
});
