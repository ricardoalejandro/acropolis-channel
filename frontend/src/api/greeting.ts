export type GreetingResponse = { message: string };
export type GreetingFailure = 'timeout' | 'unavailable' | 'invalid';

export class GreetingError extends Error {
  constructor(readonly reason: GreetingFailure) {
    super(
      reason === 'timeout'
        ? 'La solicitud tardó demasiado. Inténtalo nuevamente.'
        : 'No pudimos cargar el saludo. Inténtalo nuevamente.',
    );
    this.name = 'GreetingError';
  }
}

export async function getGreeting(
  options: { signal?: AbortSignal; timeoutMs?: number } = {},
): Promise<GreetingResponse> {
  const controller = new AbortController();
  let timedOut = false;
  const abort = () => controller.abort();
  if (options.signal?.aborted) abort();
  else options.signal?.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, options.timeoutMs ?? 5_000);

  try {
    const response = await fetch('/api/v1/greeting', {
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    });
    if (!response.ok) throw new GreetingError('unavailable');
    if (!response.headers.get('content-type')?.toLowerCase().includes('application/json')) {
      throw new GreetingError('invalid');
    }
    let payload: unknown;
    try {
      payload = await response.json();
    } catch {
      throw new GreetingError('invalid');
    }
    if (
      typeof payload !== 'object' ||
      payload === null ||
      Array.isArray(payload) ||
      !('message' in payload) ||
      typeof payload.message !== 'string' ||
      !payload.message.trim()
    ) {
      throw new GreetingError('invalid');
    }
    return { message: payload.message.trim() };
  } catch (error) {
    if (error instanceof GreetingError) throw error;
    throw new GreetingError(timedOut ? 'timeout' : 'unavailable');
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener('abort', abort);
  }
}
