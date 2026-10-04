import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { SessionProvider } from './SessionProvider';
import { useSession } from './useSession';
import { json, user } from '../test/fixtures';
function Consumer() {
  const { user: account, loading, notice, setUser } = useSession();
  return (
    <>
      <p>{account?.displayName ?? 'Anónimo'}</p>
      <p>{loading ? 'Cargando' : 'Listo'}</p>
      <p>{notice}</p>
      <button onClick={() => setUser(user)}>Establecer sesión</button>
    </>
  );
}
describe('Session lifecycle', () => {
  it('does not let a delayed anonymous bootstrap overwrite a newly authenticated user', async () => {
    let release: ((response: Response) => void) | undefined;
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            release = resolve;
          }),
      ),
    );
    render(
      <SessionProvider>
        <Consumer />
      </SessionProvider>,
    );
    await userEvent.click(screen.getByRole('button'));
    expect(screen.getByText(user.displayName)).toBeInTheDocument();
    await act(async () => {
      release?.(json({ code: 'unauthorized' }, 401));
    });
    expect(screen.getByText(user.displayName)).toBeInTheDocument();
    expect(screen.getByText('Listo')).toBeInTheDocument();
  });
  it('does not revive a revoked session when a previous bootstrap finally succeeds', async () => {
    let release: ((response: Response) => void) | undefined;
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            release = resolve;
          }),
      ),
    );
    render(
      <SessionProvider>
        <Consumer />
      </SessionProvider>,
    );
    act(() => {
      window.dispatchEvent(new Event('acropolis:session-expired'));
    });
    await act(async () => {
      release?.(json(user));
    });
    expect(screen.getByText('Anónimo')).toBeInTheDocument();
    expect(screen.getByText(/Tu sesión ha finalizado/)).toBeInTheDocument();
  });
});
