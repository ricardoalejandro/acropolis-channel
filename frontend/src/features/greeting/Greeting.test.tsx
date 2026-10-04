import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from '../../App';

function json(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
describe('greeting experience', () => {
  it('announces loading before rendering the API message', async () => {
    let resolve: ((response: Response) => void) | undefined;
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        () =>
          new Promise<Response>((done) => {
            resolve = done;
          }),
      ),
    );
    render(<App />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Acrópolis');
    expect(screen.getByRole('status')).toHaveTextContent('Cargando');
    await act(async () => {
      resolve?.(json({ message: 'Hola mundo' }));
    });
    expect(screen.getByRole('status')).toHaveTextContent('Hola mundo');
    expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument();
  });
  it('shows a sanitized error and retries the actual HTTP request', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json({ message: 'secret' }, 500))
      .mockResolvedValueOnce(json({ message: 'Hola mundo' }));
    vi.stubGlobal('fetch', fetchMock);
    render(<App />);
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos cargar el saludo');
    expect(screen.queryByText('secret')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    expect(await screen.findByText('Hola mundo')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
  it('shows an error for an invalid response without a fabricated greeting', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ message: null })));
    render(<App />);
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos cargar el saludo');
    expect(screen.queryByText('Hola mundo')).not.toBeInTheDocument();
  });
  it('presents a timeout and a keyboard-accessible retry', async () => {
    vi.useFakeTimers();
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (_url: string, init: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init.signal?.addEventListener(
              'abort',
              () => reject(new DOMException('timeout', 'AbortError')),
              { once: true },
            );
          }),
      ),
    );
    render(<App />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(screen.getByRole('alert')).toHaveTextContent('La solicitud tardó demasiado');
    const button = screen.getByRole('button', { name: 'Reintentar' });
    button.focus();
    expect(button).toHaveFocus();
  });
  it('cancels a pending request when unmounted', async () => {
    let signal: AbortSignal | null | undefined;
    vi.stubGlobal(
      'fetch',
      vi.fn((_url: string, init: RequestInit) => {
        signal = init.signal;
        return new Promise<Response>((_resolve, reject) => {
          signal?.addEventListener(
            'abort',
            () => reject(new DOMException('unmounted', 'AbortError')),
            { once: true },
          );
        });
      }),
    );
    const view = render(<App />);
    view.unmount();
    await act(async () => {
      await Promise.resolve();
    });
    expect(signal?.aborted).toBe(true);
  });
});
