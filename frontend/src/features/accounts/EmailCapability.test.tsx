import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { EmailCapability } from './EmailCapability';
import App from '../../App';
import { json } from '../../test/fixtures';
describe('Explicit email capabilities', () => {
  it('keeps forms hidden while checking and reveals them only when email is enabled', async () => {
    let release: ((value: Response) => void) | undefined;
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
      <EmailCapability>
        <button>Formulario habilitado</button>
      </EmailCapability>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Comprobando disponibilidad');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    release?.(json({ emailEnabled: true }));
    await screen.findByRole('button', { name: 'Formulario habilitado' });
  });
  it('does not offer disabled registration or recovery forms and preserves login for confirmed accounts', async () => {
    const fetch = vi.fn((path: string) =>
      Promise.resolve(
        path.endsWith('/capabilities')
          ? json({ emailEnabled: false })
          : json({ code: 'unauthorized' }, 401),
      ),
    );
    vi.stubGlobal('fetch', fetch);
    vi.stubGlobal('scrollTo', vi.fn());
    window.history.replaceState({}, '', '/register');
    render(<App />);
    await screen.findByText(/El registro y la recuperación por correo no están disponibles/);
    expect(screen.queryByRole('button', { name: 'Crear cuenta' })).not.toBeInTheDocument();
    await userEvent.click(screen.getAllByRole('link', { name: 'Ingresar' })[0] as HTMLElement);
    await screen.findByRole('button', { name: 'Ingresar' });
    await userEvent.click(screen.getByRole('link', { name: '¿Olvidaste tu contraseña?' }));
    await screen.findByText(/El registro y la recuperación por correo no están disponibles/);
    expect(screen.queryByRole('button', { name: 'Enviar instrucciones' })).not.toBeInTheDocument();
    expect(
      fetch.mock.calls.some(
        ([path]) =>
          path.includes('/register') ||
          path.includes('/forgot-password') ||
          path.includes('/resend-confirmation'),
      ),
    ).toBe(false);
  });
  it.each([
    ['direct', null],
    ['registration', { kind: 'registration', email: 'persona@example.test' }],
  ])(
    'withholds resend actions for a disabled email capability on a %s visit',
    async (_name, state) => {
      const fetch = vi.fn((path: string) =>
        Promise.resolve(
          path.endsWith('/capabilities')
            ? json({ emailEnabled: false })
            : json({ code: 'unauthorized' }, 401),
        ),
      );
      vi.stubGlobal('fetch', fetch);
      vi.stubGlobal('scrollTo', vi.fn());
      window.history.replaceState({ usr: state, key: 'pending' }, '', '/email-pending');
      render(<App />);
      await screen.findByText(/El registro y la recuperación por correo no están disponibles/);
      expect(screen.queryByRole('button', { name: 'Reenviar enlace' })).not.toBeInTheDocument();
      expect(screen.queryByLabelText('Correo electrónico')).not.toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Volver a ingresar' })).toHaveAttribute(
        'href',
        '/login',
      );
      expect(fetch.mock.calls.some(([path]) => path.includes('/resend-confirmation'))).toBe(false);
    },
  );
  it('rejects malformed capability responses and allows an explicit retry', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({ emailEnabled: 'yes' }))
      .mockResolvedValueOnce(json({ emailEnabled: true }));
    vi.stubGlobal('fetch', fetch);
    render(
      <EmailCapability>
        <button>Formulario habilitado</button>
      </EmailCapability>,
    );
    await screen.findByRole('alert');
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByRole('button', { name: 'Formulario habilitado' });
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
