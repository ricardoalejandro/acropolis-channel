import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppRoutes } from '../../App';
import { identity, type User } from '../../api/identity';
import * as userAccessApi from '../../api/userAccess';
import { SessionProvider } from '../../auth/SessionProvider';
import { admin } from '../../test/fixtures';

const ownAccount: User = { ...admin, isOwner: false };
const routers: ReturnType<typeof createMemoryRouter>[] = [];

function mountOwnAccount() {
  vi.spyOn(identity, 'me').mockResolvedValue(ownAccount);
  vi.spyOn(identity, 'user').mockResolvedValue(ownAccount);
  vi.spyOn(userAccessApi, 'userAccess').mockResolvedValue({ lastSignInUtc: null });
  vi.spyOn(identity, 'capabilities').mockResolvedValue({ emailEnabled: true });
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: (
          <SessionProvider>
            <AppRoutes />
          </SessionProvider>
        ),
      },
    ],
    { initialEntries: ['/admin/users/' + ownAccount.id] },
  );
  routers.push(router);
  render(<RouterProvider router={router} />);
  return router;
}

async function ownEditor() {
  const editor = within(screen.getByRole('main'));
  const name = await editor.findByLabelText('Nombre visible', {}, { timeout: 5000 });
  expect(name).toHaveValue(ownAccount.displayName);
  expect(editor.getByRole('heading', { name: ownAccount.displayName, level: 1 })).toBeVisible();
  return editor;
}

beforeEach(() => {
  vi.stubGlobal('scrollTo', vi.fn());
  vi.spyOn(window, 'confirm').mockReturnValue(false);
});

afterEach(() => {
  for (const router of routers.splice(0)) router.dispose();
});

describe('Administrative own-account session changes', () => {
  it('keeps the authenticated account and current route after saving unchanged values', async () => {
    const update = vi.spyOn(identity, 'updateUser').mockResolvedValue({ ...ownAccount });
    const router = mountOwnAccount();
    const editor = await ownEditor();

    await userEvent.click(editor.getByRole('button', { name: 'Guardar usuario' }));

    await editor.findByText('Guardamos los cambios del usuario.', {}, { timeout: 3000 });
    expect(update).toHaveBeenCalledExactlyOnceWith(ownAccount.id, {
      version: ownAccount.version,
      displayName: ownAccount.displayName,
      levels: ownAccount.levels,
      status: ownAccount.status,
    });
    expect(router.state.location.pathname).toBe('/admin/users/' + ownAccount.id);
    expect(screen.getByRole('button', { name: 'Cerrar sesi\u00f3n' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Ingresar', level: 1 })).not.toBeInTheDocument();
    expect(window.confirm).not.toHaveBeenCalled();
  }, 10000);

  it('clears the local session and opens login after a real own-account access change', async () => {
    const changed: User = {
      ...ownAccount,
      levels: ['Externo', 'Miembro'],
      version: 'version-two',
    };
    const update = vi.spyOn(identity, 'updateUser').mockResolvedValue(changed);
    const router = mountOwnAccount();
    const editor = await ownEditor();

    await userEvent.click(editor.getByRole('checkbox', { name: 'Miembro' }));
    await userEvent.click(editor.getByRole('button', { name: 'Guardar usuario' }));

    await screen.findByLabelText('Correo electr\u00f3nico', {}, { timeout: 5000 });
    expect(screen.getByRole('heading', { name: 'Ingresar', level: 1 })).toBeVisible();
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(update).toHaveBeenCalledExactlyOnceWith(ownAccount.id, {
      version: ownAccount.version,
      displayName: ownAccount.displayName,
      levels: ['Externo', 'Miembro'],
      status: ownAccount.status,
    });
    expect(
      screen.getByText('Actualizamos tu cuenta. Ingresa de nuevo para continuar.'),
    ).toHaveAttribute('role', 'status');
    expect(screen.queryByRole('button', { name: 'Cerrar sesi\u00f3n' })).not.toBeInTheDocument();
  }, 10000);
});
