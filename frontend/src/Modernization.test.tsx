import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderApp, renderMemoryApp } from './test/renderApp';
import { type ReactNode } from 'react';
import { SubscriptionPage } from './features/subscriptions/Subscription';
import { clearCsrf, type User } from './api/identity';
import { user as userFixture, admin, json } from './test/fixtures';
const user: User = { ...userFixture, id: '10000000-0000-0000-0000-000000000001' };
import { YouTubePlayer } from './features/catalog/YouTubePlayer';
const now = '2026-10-06T12:00:00Z';
const protectedVersion = '1234567890abcdef1234567890abcdef';
const allPermissions = ['Users.Manage', 'Content.Manage', 'Subscriptions.Manage'];
const owner: User = { ...admin, isOwner: true, permissions: allPermissions };
const reading = {
  id: 'reading-one',
  slug: 'primera-lectura',
  title: 'Primera lectura',
  summary: 'Sinopsis pública.',
  body: 'Descripción pública.',
  author: 'Autora real',
  tags: ['Filosofía'],
  category: 'lecturas',
  isFree: true,
  coverAsset: null,
  durationSeconds: null,
  publishedUtc: now,
  createdUtc: now,
  updatedUtc: now,
  version: 'v1',
  status: 'published',
  collectionKind: null,
  workText: 'Párrafo protegido.\n\n<script>window.injected=true</script>',
  youTubeId: null,
  itemIds: [],
  items: [],
};
const second = { ...reading, id: 'reading-two', slug: 'segunda-lectura', title: 'Segunda lectura' };
const course = {
  ...reading,
  id: 'course-one',
  slug: 'curso-real',
  title: 'Curso real',
  category: 'cursos',
  collectionKind: 'course',
  workText: null,
  itemIds: [reading.id, second.id],
  items: [reading, second],
};
const program = {
  ...course,
  id: 'program-one',
  slug: 'programa-real',
  title: 'Programa real',
  collectionKind: 'program',
  itemIds: [course.id],
  items: [course],
};
const subscription = {
  id: 'subscription-one',
  userId: user.id,
  plan: 'free_beta',
  status: 'active',
  createdUtc: now,
  activatedUtc: now,
  startsUtc: now,
  effectiveState: 'active',
  updatedUtc: now,
  cancelledUtc: null,
  expiresUtc: null,
  version: 'v1',
};
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(
  path: string,
  account: User | null = user,
  override?: Override,
  memory = false,
  element?: ReactNode,
) {
  window.history.replaceState({}, '', path);
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const route = String(input);
    const changed = override?.(route, init);
    if (changed) return changed;
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    if (route.endsWith('/identity/me'))
      return account ? json(account) : json({ code: 'invalid_credentials' }, 401);
    if (route.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (route.endsWith('/csrf')) return json({ token: 'csrf-test' });
    if (route.includes('/catalog/content?'))
      return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (route.includes('/catalog/content/')) {
      const slug = route.split('/').at(-1);
      return json([reading, second, course, program].find((item) => item.slug === slug) ?? reading);
    }
    if (route.includes('/consumption/content/')) {
      const slug = route.split('/').at(-1);
      return json({
        ...([reading, second, course, program].find((item) => item.slug === slug) ?? reading),
        version: protectedVersion,
      });
    }
    if (route.endsWith('/subscriptions/me'))
      return json({ subscription: null, eligibleToActivate: true });
    if (route.endsWith('/subscriptions/activate')) return json(subscription);
    if (route.endsWith('/subscriptions/cancel'))
      return json({
        ...subscription,
        status: 'cancelled',
        effectiveState: 'cancelled',
        cancelledUtc: now,
        version: 'v2',
      });
    if (route.includes('/admin/subscriptions/accounts/lookup?'))
      return json([
        {
          id: user.id,
          displayName: user.displayName,
          email: user.email,
          status: 'active',
          emailConfirmed: true,
        },
      ]);
    if (route.includes('/admin/subscriptions/accounts?'))
      return json({
        items: [
          {
            id: user.id,
            displayName: user.displayName,
            email: user.email,
            status: 'active',
            emailConfirmed: true,
          },
        ],
        total: 1,
        page: 1,
        pageSize: 20,
      });
    if (route.includes('/admin/subscriptions?'))
      return json({ items: [subscription], total: 1, page: 1, pageSize: 20 });
    if (route.endsWith('/admin/subscriptions/subscription-one'))
      return json({
        ...subscription,
        ...(init?.method === 'PATCH' ? { ...body, effectiveState: body['status'] } : {}),
      });
    if (route.includes('/audit?')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (route.includes('/admin/users/') && route.endsWith('/access'))
      return json({ lastSignInUtc: null });
    if (route.endsWith('/admin/users/test-person/permissions'))
      return json({ ...user, id: 'test-person', permissions: body['permissions'], version: 'v2' });
    if (route.endsWith('/admin/users/test-person'))
      return json(
        init?.method === 'PUT'
          ? { ...user, id: 'test-person', permissions: body['permissions'], version: 'v2' }
          : { ...user, id: 'test-person' },
      );
    if (route.endsWith('/admin/users/test-owner')) return json({ ...owner, id: 'test-owner' });
    if (route.includes('/admin/content?'))
      return json({ items: [reading, second, course], total: 3, page: 1, pageSize: 20 });
    if (route.endsWith('/summary') && route.includes('/admin/content/')) {
      const id = route.split('/').at(-2);
      return json([reading, second, course, program].find((item) => item.id === id) ?? reading);
    }
    if (route.includes('/admin/content/')) {
      const id = route.split('/').at(-1);
      const existing = [reading, second, course, program].find((item) => item.id === id) ?? reading;
      return json(init?.method === 'PUT' ? { ...existing, ...body, version: 'v2' } : existing);
    }
    if (route.endsWith('/admin/content') && init?.method === 'POST')
      return json({ ...reading, ...body, status: 'draft' }, 201);
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  const { router } = memory || element ? renderMemoryApp(path, element) : renderApp();
  return Object.assign(fetch, { router });
}
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value } });
}
function writes(fetch: ReturnType<typeof mount>, route: string) {
  return fetch.mock.calls.filter(([path, init]) => String(path).endsWith(route) && init?.body);
}
beforeEach(() => {
  clearCsrf();
  vi.stubGlobal('scrollTo', vi.fn());
  vi.spyOn(window, 'confirm').mockReturnValue(true);
});
afterEach(() => {
  delete window.YT;
  document
    .querySelectorAll('script[src*="youtube.com/iframe_api"]')
    .forEach((element) => element.remove());
});
describe('Explicit free subscription', () => {
  it('explains the free stage to a guest without requesting or activating a subscription', async () => {
    const fetch = mount(
      '/profile/subscription',
      null,
      undefined,
      true,
      <main>
        <SubscriptionPage />
      </main>,
    );
    const main = within(document.querySelector('main')!);
    expect(
      await main.findByRole(
        'heading',
        { name: 'Una cuenta, muchas formas de aprender.' },
        { timeout: 3000 },
      ),
    ).toBeVisible();
    expect(fetch.mock.calls.some(([path]) => String(path).includes('/subscriptions/'))).toBe(false);
    expect(main.getByRole('link', { name: 'Crear mi cuenta' })).toHaveAttribute(
      'href',
      '/register',
    );
  }, 10000);
  it('activates only on click and returns to the requested content', async () => {
    const fetch = mount('/profile/subscription?content=primera-lectura');
    const main = within(document.querySelector('main')!);
    const activate = await main.findByRole(
      'button',
      { name: 'Suscribirme gratis' },
      { timeout: 3000 },
    );
    expect(writes(fetch, '/subscriptions/activate')).toHaveLength(0);
    expect(main.getByText(/cualquier contratación requerirá tu aceptación/)).toBeVisible();
    await userEvent.click(activate);
    expect(await main.findByRole('status')).toHaveTextContent('Tu acceso gratuito está activo.');
    expect(main.getByRole('link', { name: 'Volver al contenido' })).toHaveAttribute(
      'href',
      '/content/primera-lectura',
    );
    expect(writes(fetch, '/subscriptions/activate')).toHaveLength(1);
  }, 10000);
  it('keeps access when cancellation is dismissed, cancels explicitly and reactivates', async () => {
    const fetch = mount('/profile/subscription', user, (path) =>
      path.endsWith('/subscriptions/me')
        ? json({ subscription, eligibleToActivate: false })
        : undefined,
    );
    const main = within(document.querySelector('main')!);
    await userEvent.click(
      await main.findByRole('button', { name: 'Cancelar suscripción' }, { timeout: 3000 }),
    );
    expect(writes(fetch, '/subscriptions/cancel')).toHaveLength(0);
    await userEvent.click(main.getByRole('button', { name: 'Conservar suscripción' }));
    expect(main.queryByRole('button', { name: 'Confirmar cancelación' })).not.toBeInTheDocument();
    await userEvent.click(main.getByRole('button', { name: 'Cancelar suscripción' }));
    await userEvent.click(main.getByRole('button', { name: 'Confirmar cancelación' }));
    await main.findByText('Cancelaste tu suscripción gratuita.');
    expect(JSON.parse(String(writes(fetch, '/subscriptions/cancel')[0]?.[1]?.body))).toEqual({
      version: 'v1',
    });
    expect(writes(fetch, '/subscriptions/activate')).toHaveLength(0);
    await userEvent.click(main.getByRole('button', { name: 'Suscribirme gratis' }));
    await main.findByText('Tu acceso gratuito está activo.');
    expect(writes(fetch, '/subscriptions/activate')).toHaveLength(1);
  }, 10000);
  it.each(['suspended', 'ineligible'])(
    'does not provide a self activation for %s access',
    async (state) => {
      let fetch!: ReturnType<typeof mount>;
      await act(async () => {
        fetch = mount('/profile/subscription', user, (path) =>
          path.endsWith('/subscriptions/me')
            ? json({
                subscription:
                  state === 'suspended'
                    ? { ...subscription, status: 'suspended', effectiveState: 'suspended' }
                    : null,
                eligibleToActivate: false,
              })
            : undefined,
        );
      });
      await screen.findByRole('heading', {
        name: state === 'suspended' ? 'Gratuito' : 'Acceso gratuito',
      });
      expect(screen.queryByRole('button', { name: 'Suscribirme gratis' })).not.toBeInTheDocument();
      expect(writes(fetch, '/subscriptions/activate')).toHaveLength(0);
    },
  );
  it('retains explicit retry after load and activation errors without exposing diagnostics', async () => {
    let unavailable = true;
    let rejected = true;
    mount('/profile/subscription', user, (path) =>
      path.endsWith('/subscriptions/me') && unavailable
        ? json({ code: 'unavailable', detail: 'private' }, 503)
        : path.endsWith('/subscriptions/activate') && rejected
          ? json({ code: 'subscription_suspended', detail: 'private' }, 403)
          : undefined,
    );
    await screen.findByRole('alert');
    unavailable = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Suscribirme gratis' }));
    await screen.findByRole('alert');
    expect(screen.queryByText('private')).not.toBeInTheDocument();
    rejected = false;
    await userEvent.click(screen.getByRole('button', { name: 'Suscribirme gratis' }));
    await screen.findByText('Tu acceso gratuito está activo.');
  });
});
describe('Protected works and ordered collections', () => {
  it('keeps full work out of the guest page and requests no protected endpoint', async () => {
    const fetch = mount('/content/primera-lectura', null);
    await screen.findByRole('heading', { name: 'Accede a la obra completa.' });
    expect(screen.queryByText('Párrafo protegido.')).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([path]) => String(path).includes('/consumption/'))).toBe(false);
  });
  it('renders protected plaintext as text and follows the real course order', async () => {
    await act(async () => {
      mount('/content/primera-lectura?collection=curso-real');
    });
    await screen.findByText('Párrafo protegido.');
    expect(screen.getByText('<script>window.injected=true</script>')).toBeVisible();
    expect(document.querySelector('main script')).toBeNull();
    const next = await screen.findByRole('link', { name: 'Siguiente: Segunda lectura' });
    expect(next).toHaveAttribute('href', '/content/segunda-lectura?collection=curso-real');
    await userEvent.click(next);
    expect(await screen.findByRole('link', { name: 'Anterior: Primera lectura' })).toHaveAttribute(
      'href',
      '/content/primera-lectura?collection=curso-real',
    );
  });
  it.each([403, 404, 503])(
    'provides the appropriate protected-work state for HTTP %s',
    async (status) => {
      let rejected = true;
      await act(async () => {
        mount('/content/primera-lectura', user, (path) =>
          path.includes('/consumption/') && rejected
            ? json(
                {
                  code:
                    status === 403
                      ? 'subscription_required'
                      : status === 404
                        ? 'not_found'
                        : 'unavailable',
                },
                status,
              )
            : undefined,
        );
      });
      if (status === 403)
        expect(
          await screen.findByRole('link', { name: 'Ver mi suscripción' }, { timeout: 3000 }),
        ).toHaveAttribute('href', '/profile/subscription?content=primera-lectura');
      else if (status === 404)
        await screen.findByText(/Esta ficha aún no tiene una obra disponible/);
      else {
        await screen.findByRole('alert');
        rejected = false;
        await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
        await screen.findByText('Párrafo protegido.');
      }
    },
  );
  it('navigates program to course to work without inventing items', async () => {
    mount('/content/programa-real');
    const main = within(document.querySelector('main')!);
    await main.findByText('Tu acceso a este programa está activo. Elige un contenido de la lista.');
    await userEvent.click(main.getByRole('link', { name: 'Curso real · Autora real' }));
    await main.findByText('Tu acceso a este curso está activo. Elige un contenido de la lista.');
    await userEvent.click(main.getByRole('link', { name: 'Primera lectura · Autora real' }));
    await main.findByText('Párrafo protegido.');
  }, 10000);
  it('ignores malformed and unrelated collection context', async () => {
    mount('/content/primera-lectura?collection=https://outside.test');
    await screen.findByText('Párrafo protegido.', {}, { timeout: 3000 });
    expect(
      screen.queryByRole('navigation', { name: 'Navegación del curso o programa' }),
    ).not.toBeInTheDocument();
  });
});
describe('Official player loaded by intent', () => {
  it.each([false, true])(
    'loads no external resources until explicit play (podcast=%s) and reports a player error',
    async (podcast) => {
      let errorCallback: (() => void) | undefined;
      const player = vi.fn(function (
        _frame: HTMLIFrameElement,
        options: { events: { onError: () => void } },
      ) {
        errorCallback = options.events.onError;
        return { destroy: vi.fn() };
      });
      window.YT = { Player: player as unknown as NonNullable<NonNullable<Window['YT']>['Player']> };
      render(<YouTubePlayer videoId="M7lc1UVf-VE" title="Obra autorizada" podcast={podcast} />);
      expect(document.querySelector('iframe')).toBeNull();
      expect(document.querySelector('script[src*="youtube"]')).toBeNull();
      await userEvent.click(
        screen.getByRole('button', { name: podcast ? 'Reproducir podcast' : 'Reproducir vídeo' }),
      );
      const frame = screen.getByTitle('Obra autorizada');
      expect(frame).toHaveAttribute('referrerpolicy', 'origin');
      expect(frame.getAttribute('src')).toContain(
        'https://www.youtube-nocookie.com/embed/M7lc1UVf-VE',
      );
      expect(frame.getAttribute('src')).toContain('autoplay=0');
      await waitFor(() => expect(player).toHaveBeenCalledOnce());
      act(() => errorCallback?.());
      expect(await screen.findByRole('alert')).toHaveTextContent('Este vídeo no está disponible');
      expect(screen.getByRole('link', { name: 'Abrir en YouTube' })).toHaveAttribute(
        'rel',
        'noopener noreferrer',
      );
      delete window.YT;
    },
  );
  it('shows malformed media as unavailable without opening a player', () => {
    render(<YouTubePlayer videoId="bad" title="Obra" />);
    expect(screen.getByRole('alert')).toHaveTextContent('El vídeo no está disponible.');
    expect(document.querySelector('iframe')).toBeNull();
  });
  it('requests the official API only after play and handles its load failure', async () => {
    delete window.YT;
    render(<YouTubePlayer videoId="M7lc1UVf-VE" title="Obra" />);
    await userEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    const script = document.querySelector(
      'script[src="https://www.youtube.com/iframe_api"]',
    ) as HTMLScriptElement;
    expect(script.referrerPolicy).toBe('origin');
    fireEvent.error(script);
    await screen.findByRole('alert');
  });
  it('shares the pending official API across navigation while YT contains only its bootstrap state', async () => {
    const first = render(<YouTubePlayer videoId="M7lc1UVf-VE" title="Primera obra" />);
    fireEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    expect(document.querySelectorAll('script[src*="youtube.com/iframe_api"]')).toHaveLength(1);
    window.YT = { loading: 1, loaded: 0 };
    first.unmount();
    render(<YouTubePlayer videoId="38Oq_C4AxgA" title="Segunda obra" />);
    fireEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(document.querySelectorAll('script[src*="youtube.com/iframe_api"]')).toHaveLength(1);
    const player = vi.fn(function (element: HTMLIFrameElement) {
      return { destroy: vi.fn(() => element.remove()) };
    });
    window.YT = { Player: player as unknown as NonNullable<NonNullable<Window['YT']>['Player']> };
    await act(async () => {
      window.onYouTubeIframeAPIReady?.();
    });
    await waitFor(() => expect(player).toHaveBeenCalledOnce());
    expect(player.mock.calls[0]?.[0]).toBe(screen.getByTitle('Segunda obra'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
  it('destroys an initialized player once on unmount even when teardown removes its iframe', async () => {
    const destroy = vi.fn();
    const player = vi.fn(function (element: HTMLIFrameElement) {
      destroy.mockImplementation(() => element.remove());
      return { destroy };
    });
    window.YT = { Player: player as unknown as NonNullable<NonNullable<Window['YT']>['Player']> };
    const view = render(<YouTubePlayer videoId="M7lc1UVf-VE" title="Obra" />);
    fireEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    await waitFor(() => expect(player).toHaveBeenCalledOnce());
    expect(() => view.unmount()).not.toThrow();
    expect(destroy).toHaveBeenCalledOnce();
    expect(document.querySelector('iframe')).toBeNull();
  });
});
describe('Administration boundaries and audit navigation', () => {
  it('uses a separate shell and only permission-owned navigation', async () => {
    mount('/admin', { ...user, permissions: ['Content.Manage'] });
    await screen.findByRole('heading', { name: 'Administración' });
    expect(screen.getByRole('navigation', { name: 'Administración' })).toBeVisible();
    expect(screen.queryByRole('contentinfo')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Usuarios' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Suscripciones' })).not.toBeInTheDocument();
  });
  it('protects an owner account and offers no owner permission mutation', async () => {
    const fetch = mount('/admin/users/test-owner', owner);
    await screen.findByText('Propietario protegido', {}, { timeout: 3000 });
    expect(screen.getByLabelText('Nombre visible')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Guardar usuario' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Guardar permisos' })).not.toBeInTheDocument();
    expect(writes(fetch, '/admin/users/test-owner')).toHaveLength(0);
  });
  it('allows only the owner to explicitly delegate separate permissions', async () => {
    let fetch!: ReturnType<typeof mount>;
    await act(async () => {
      fetch = mount('/admin/users/test-person', owner);
    });
    const editor = within(screen.getByRole('region', { name: 'Permisos administrativos' }));
    await editor.findByRole('heading', { name: 'Permisos administrativos' });
    await userEvent.click(editor.getByLabelText('Gestionar contenidos', { exact: true }));
    await userEvent.click(editor.getByRole('button', { name: 'Guardar permisos' }));
    await editor.findByText('Guardamos los permisos.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/users/test-person/permissions')[0]?.[1]?.body)),
    ).toEqual({ version: 'version-one', permissions: ['Content.Manage'] });
  });
  it('does not offer permissions management to a Users.Manage administrator', async () => {
    await act(async () => {
      mount('/admin/users/test-person', admin);
    });
    await screen.findByLabelText('Nombre visible');
    expect(screen.queryByRole('button', { name: 'Guardar permisos' })).not.toBeInTheDocument();
    expect(screen.getByText('El propietario gestiona los permisos administrativos.')).toBeVisible();
  });
  it('limits audit modules, validates UTC ranges, filters and retains applied filters for pagination', async () => {
    const fetch = mount(
      '/admin/audit?module=users',
      { ...user, permissions: ['Content.Manage'] },
      (path) =>
        path.includes('/admin/content/audit?')
          ? json({
              items: [],
              total: 21,
              page: new URL('http://test' + path).searchParams.has('page') ? 2 : 1,
              pageSize: 20,
            })
          : undefined,
    );
    await screen.findByText('21 cambios registrados', {}, { timeout: 3000 });
    expect(screen.queryByRole('button', { name: 'Usuarios' })).not.toBeInTheDocument();
    fill('Desde (UTC)', '2026-10-06T12:00');
    fill('Hasta (UTC)', '2026-10-05T12:00');
    await userEvent.click(screen.getByRole('button', { name: 'Filtrar' }));
    await screen.findByText(/Desde debe ser anterior/);
    fill('Hasta (UTC)', '2026-10-07T12:00');
    fireEvent.change(screen.getByLabelText('Acción'), { target: { value: 'content.published' } });
    fill('ID de contenido', 'reading-one');
    await userEvent.click(screen.getByRole('button', { name: 'Filtrar' }));
    await waitFor(() => expect(window.location.search).toContain('content.published'));
    fill('ID de contenido', 'unapplied');
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }));
    await waitFor(() => expect(window.location.search).toContain('page=2'));
    expect(window.location.search).toContain('reading-one');
    expect(window.location.search).not.toContain('unapplied');
    expect(fetch.mock.calls.some(([path]) => String(path).includes('/admin/users/audit'))).toBe(
      false,
    );
  }, 10000);
  it('lets keyboard users reach the named read-only subscription audit table', async () => {
    mount('/admin/audit?module=subscriptions', owner, (path) =>
      path.includes('/admin/subscriptions/audit?')
        ? json({
            items: [
              {
                id: 'audit-one',
                actorId: 'actor-qa',
                subscriptionId: 'subscription-one',
                action: 'subscription.updated',
                createdUtc: now,
                beforeStatus: 'active',
                afterStatus: 'suspended',
                reason: 'Revisión de QA',
              },
            ],
            total: 1,
            page: 1,
            pageSize: 20,
          })
        : undefined,
    );
    const region = await screen.findByRole('region', { name: 'Historial de cambios' });
    expect(region).toHaveAttribute('tabindex', '0');
    expect(within(region).getByRole('table')).toHaveTextContent('Suscripción actualizada');
    screen.getByRole('button', { name: 'Filtrar' }).focus();
    await userEvent.tab();
    expect(region).toHaveFocus();
  });
  it('denies every audit module to an ordinary account before a module API call', async () => {
    const fetch = mount('/admin/audit');
    await screen.findByText(/no tiene permisos para consultar la auditoría/);
    expect(fetch.mock.calls.some(([path]) => String(path).includes('/audit?'))).toBe(false);
  });
  it('requires Subscriptions.Manage and saves a reason with the optimistic version', async () => {
    const fetch = mount('/admin/subscriptions/subscription-one', owner);
    await screen.findByLabelText('Estado de la suscripción');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar estado' }));
    await screen.findByText(/Escribe un motivo/);
    fireEvent.change(screen.getByLabelText('Estado de la suscripción'), {
      target: { value: 'suspended' },
    });
    fill('Motivo del cambio', 'Revisión de acceso QA');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar estado' }));
    await screen.findByText('Guardamos el estado de la suscripción.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/subscriptions/subscription-one')[0]?.[1]?.body)),
    ).toEqual({ version: 'v1', status: 'suspended', reason: 'Revisión de acceso QA' });
  });
  it('filters subscription lists using URL navigation and preserves the query through back', async () => {
    const fetch = mount('/admin/subscriptions', owner, undefined, true);
    const main = within(document.querySelector('main')!);
    await main.findByText('1 suscripciones', {}, { timeout: 3000 });
    await userEvent.click(main.getByRole('button', { name: 'Buscar una cuenta' }));
    await userEvent.click(
      await main.findByRole('button', {
        name: 'Ver suscripciones de ' + user.displayName + ', ' + user.email,
      }),
    );
    fireEvent.change(screen.getByLabelText('Estado'), { target: { value: 'active' } });
    await userEvent.click(main.getByRole('button', { name: 'Buscar' }));
    await waitFor(() => expect(fetch.router.state.location.search).toContain('status=active'));
    expect(fetch.router.state.location.search).toContain('userId=' + user.id);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await waitFor(() => expect(fetch.router.state.location.search).not.toContain('status='));
    expect(fetch.router.state.location.search).toContain('userId=' + user.id);
    await waitFor(() => expect(screen.getByLabelText('Estado')).toHaveValue(''));
  }, 10000);
});
describe('Editorial payloads and ordered membership', () => {
  it('saves attribution, tags and complete reading separately from the public synopsis', async () => {
    const fetch = mount('/admin/content/reading-one', owner);
    await screen.findByLabelText('Lectura completa');
    fill('Autor o institución', '  Autora verificada  ');
    fill('Etiquetas', 'Historia, Cultura');
    fill('Lectura completa', 'Texto protegido nuevo.');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/reading-one')[0]?.[1]?.body)),
    ).toMatchObject({
      author: 'Autora verificada',
      tags: ['Historia', 'Cultura'],
      workText: 'Texto protegido nuevo.',
      body: reading.body,
      youTubeId: null,
      collectionKind: null,
      itemIds: [],
    });
  });
  it('validates tags and video IDs before submitting and keeps the user values', async () => {
    const fetch = mount('/admin/content/reading-one', owner, (path) =>
      path.endsWith('/admin/content/reading-one')
        ? json({ ...reading, category: 'videos', workText: null, youTubeId: null })
        : undefined,
    );
    await screen.findByLabelText('Enlace o identificador de YouTube');
    fill('Etiquetas', 'Cultura, cultura');
    fill('Enlace o identificador de YouTube', 'invalid');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText(/etiquetas distintas/);
    expect(screen.getByLabelText('Enlace o identificador de YouTube')).toHaveValue('invalid');
    expect(writes(fetch, '/admin/content/reading-one')).toHaveLength(0);
  });
  it('searches course works with Enter without saving pending editorial changes', async () => {
    const fetch = mount('/admin/content/course-one', owner);
    await screen.findByLabelText('Tipo de colección', {}, { timeout: 3000 });
    const collection = within(document.querySelector('.collection-editor')!);
    await collection.findByRole('button', { name: 'Bajar Primera lectura' }, { timeout: 3000 });
    fill('Título', 'Curso con cambios pendientes');
    await userEvent.click(collection.getByRole('button', { name: 'Bajar Primera lectura' }));
    await userEvent.type(
      collection.getByRole('searchbox', { name: 'Buscar elementos publicados' }),
      '  Segunda  {Enter}',
    );
    await waitFor(() =>
      expect(
        fetch.mock.calls.some(([path]) => {
          const url = new URL(String(path), 'https://qa.invalid');
          return (
            url.pathname === '/api/v1/admin/content' &&
            url.searchParams.get('search') === 'Segunda' &&
            url.searchParams.get('page') === '1' &&
            url.searchParams.get('status') === 'published'
          );
        }),
      ).toBe(true),
    );
    expect(writes(fetch, '/admin/content/course-one')).toHaveLength(0);
    expect(screen.getByLabelText('Título')).toHaveValue('Curso con cambios pendientes');
    const selected = within(
      collection.getByRole('list', { name: 'Elementos seleccionados' }),
    ).getAllByRole('listitem');
    expect(selected[0]).toHaveTextContent('Segunda lectura');
    expect(selected[1]).toHaveTextContent('Primera lectura');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
    expect(writes(fetch, '/admin/content/course-one')).toHaveLength(1);
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/course-one')[0]?.[1]?.body)),
    ).toMatchObject({
      title: 'Curso con cambios pendientes',
      itemIds: [second.id, reading.id],
      collectionKind: 'course',
    });
  }, 10000);
  it('saves the visible course order after moving works in both directions without downloading complete works', async () => {
    const fetch = mount('/admin/content/course-one', owner);
    await screen.findByLabelText('Tipo de colección', {}, { timeout: 3000 });
    const collection = within(document.querySelector('.collection-editor')!);
    await collection.findByRole('button', { name: 'Bajar Primera lectura' }, { timeout: 3000 });
    const summaries = () =>
      fetch.mock.calls.filter(
        ([path]) => String(path).includes('/admin/content/') && String(path).endsWith('/summary'),
      );
    expect(summaries()).toHaveLength(2);
    const list = within(screen.getByRole('list', { name: 'Elementos seleccionados' }));
    await userEvent.click(list.getByRole('button', { name: 'Bajar Primera lectura' }));
    expect(list.getAllByRole('listitem')[0]).toHaveTextContent('Segunda lectura');
    await userEvent.click(list.getByRole('button', { name: 'Subir Primera lectura' }));
    expect(list.getAllByRole('listitem')[0]).toHaveTextContent('Primera lectura');
    expect(summaries()).toHaveLength(2);
    expect(
      fetch.mock.calls.some(([path]) => String(path) === '/api/v1/admin/content/reading-one'),
    ).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/course-one')[0]?.[1]?.body)),
    ).toMatchObject({
      collectionKind: 'course',
      itemIds: [reading.id, second.id],
      workText: null,
      youTubeId: null,
    });
  }, 10000);
  it('removes a course work and appends it on explicit re-addition using cached metadata', async () => {
    const fetch = mount('/admin/content/course-one', owner);
    await screen.findByLabelText('Tipo de colección', {}, { timeout: 3000 });
    const collection = within(document.querySelector('.collection-editor')!);
    await collection.findByRole('button', { name: 'Quitar Primera lectura' }, { timeout: 3000 });
    await userEvent.click(screen.getByRole('button', { name: 'Quitar Primera lectura' }));
    await waitFor(() =>
      expect(
        within(collection.getByRole('list', { name: 'Elementos seleccionados' })).getAllByRole(
          'listitem',
        ),
      ).toHaveLength(1),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Añadir Primera lectura' }));
    await collection.findByRole('button', { name: 'Quitar Primera lectura' });
    const selected = within(
      collection.getByRole('list', { name: 'Elementos seleccionados' }),
    ).getAllByRole('listitem');
    expect(selected[0]).toHaveTextContent('Segunda lectura');
    expect(selected[1]).toHaveTextContent('Primera lectura');
    expect(fetch.mock.calls.filter(([path]) => String(path).endsWith('/summary'))).toHaveLength(2);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/course-one')[0]?.[1]?.body)),
    ).toMatchObject({
      itemIds: [second.id, reading.id],
      collectionKind: 'course',
    });
  }, 10000);
  it('shows only courses for a program and confirms a destructive type change', async () => {
    mount('/admin/content/program-one', owner);
    await screen.findByRole('button', { name: 'Añadir Curso real' }, { timeout: 3000 });
    expect(
      screen.queryByRole('button', { name: 'Añadir Primera lectura' }),
    ).not.toBeInTheDocument();
    vi.mocked(window.confirm).mockReturnValue(false);
    fireEvent.change(screen.getByLabelText('Tipo de colección'), { target: { value: 'course' } });
    expect(screen.getByLabelText('Tipo de colección')).toHaveValue('program');
    expect(window.confirm).toHaveBeenCalled();
  }, 10000);
  it('preserves editable metadata-only courses inherited from the existing catalogue', async () => {
    const legacy = { ...course, collectionKind: null, itemIds: [], items: [] };
    const fetch = mount('/admin/content/course-one', owner, (path, init) =>
      path.endsWith('/admin/content/course-one')
        ? json(
            init?.method === 'PUT'
              ? { ...legacy, ...JSON.parse(String(init.body)), version: 'v2' }
              : legacy,
          )
        : undefined,
    );
    const collectionType = await waitFor(
      () => {
        expect(
          fetch.mock.calls.some(
            ([path, init]) =>
              String(path).endsWith('/admin/content/course-one') && init?.method === 'GET',
          ),
        ).toBe(true);
        const form = document.querySelector<HTMLFormElement>('form.content-editor-form');
        if (!form) throw new Error('The legacy course editor is not loaded.');
        return within(form).getByLabelText('Tipo de colección');
      },
      { timeout: 3000 },
    );
    expect(collectionType).toHaveRole('combobox');
    expect(collectionType).toHaveAccessibleName('Tipo de colección');
    expect(collectionType).toBeVisible();
    expect(collectionType).toHaveValue('');
    expect(screen.getByText(/Esta ficha heredada puede conservarse publicada/)).toBeVisible();
    fill('Título', 'Ficha heredada revisada');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/course-one')[0]?.[1]?.body)),
    ).toMatchObject({
      title: 'Ficha heredada revisada',
      category: 'cursos',
      collectionKind: null,
      itemIds: [],
    });
  });
  it('warns before leaving unsaved work and permits a saved navigation', async () => {
    await act(async () => {
      mount('/admin/content/reading-one', owner);
    });
    await screen.findByLabelText('Lectura completa');
    fill('Título', 'Cambio sin guardar');
    vi.mocked(window.confirm).mockReturnValue(false);
    await userEvent.click(screen.getByRole('link', { name: 'Gestionar contenidos' }));
    expect(window.location.pathname).toBe('/admin/content/reading-one');
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    vi.mocked(window.confirm).mockReturnValue(true);
    await userEvent.click(screen.getByRole('link', { name: 'Gestionar contenidos' }));
    await waitFor(() => expect(window.location.pathname).toBe('/admin/content'));
  });
});

describe('Usable subscription account lookup', () => {
  it('loads no directory until asked, searches by email, selects and clears a named account without technical IDs', async () => {
    const fetch = mount('/admin/subscriptions', owner);
    await screen.findByText('1 suscripciones');
    expect(
      fetch.mock.calls.some(([path]) => String(path).includes('/subscriptions/accounts?')),
    ).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Buscar una cuenta' }));
    await screen.findByText('1 cuenta encontrada');
    fill('Nombre o correo de la cuenta', user.email);
    await userEvent.click(screen.getByRole('button', { name: 'Buscar cuentas' }));
    await waitFor(() =>
      expect(
        fetch.mock.calls.some(([path]) => String(path).includes('search=persona%40example.test')),
      ).toBe(true),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Ver suscripciones de ' + user.displayName + ', ' + user.email,
      }),
    );
    await waitFor(() => expect(window.location.search).toContain('userId=' + user.id));
    expect(screen.getAllByText(user.displayName).length).toBeGreaterThan(0);
    expect(screen.queryByLabelText('ID de usuario')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Quitar filtro de cuenta' }));
    await waitFor(() => expect(window.location.search).not.toContain('userId='));
  }, 10000);
  it('shows load errors, explicit retry, empty results and paginated search without unauthorized data', async () => {
    let rejected = true;
    let empty = false;
    mount('/admin/subscriptions', { ...user, permissions: ['Subscriptions.Manage'] }, (path) =>
      path.includes('/subscriptions/accounts?')
        ? rejected
          ? json({ code: 'unavailable', detail: 'private account payload' }, 503)
          : json({
              items: empty
                ? []
                : [
                    {
                      id: user.id,
                      displayName: user.displayName,
                      email: user.email,
                      status: 'active',
                      emailConfirmed: true,
                    },
                  ],
              total: empty ? 0 : 21,
              page: path.includes('page=2') ? 2 : 1,
              pageSize: 20,
            })
        : undefined,
    );
    await screen.findByRole('button', { name: 'Buscar una cuenta' });
    await userEvent.click(screen.getByRole('button', { name: 'Buscar una cuenta' }));
    await screen.findByRole('alert');
    expect(screen.queryByText('private account payload')).not.toBeInTheDocument();
    rejected = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByText('21 cuentas encontradas');
    const directory = document.getElementById('subscription-account-directory')!;
    await userEvent.click(within(directory).getByRole('button', { name: 'Siguiente' }));
    await waitFor(() => expect(within(directory).getByText('Página 2')).toBeVisible());
    empty = true;
    fill('Nombre o correo de la cuenta', 'no existe');
    await userEvent.click(screen.getByRole('button', { name: 'Buscar cuentas' }));
    await screen.findByText('No encontramos cuentas con esa búsqueda.');
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar búsqueda de cuentas' }));
    expect(document.getElementById('subscription-account-directory')).toBeNull();
  }, 10000);
  it('requires a new Play when navigating from one video work to another', async () => {
    const destroy = vi.fn();
    const apiPlayer = vi.fn(function () {
      return { destroy };
    });
    window.YT = {
      Player: apiPlayer as unknown as NonNullable<NonNullable<Window['YT']>['Player']>,
    };
    const fetch = mount('/content/video-one', user, (path) => {
      if (
        !path.includes('/catalog/content/video-') &&
        !path.includes('/consumption/content/video-')
      )
        return undefined;
      const isSecond = path.endsWith('video-two');
      return json({
        ...reading,
        version: path.includes('/consumption/content/') ? protectedVersion : reading.version,
        id: isSecond ? 'video-two' : 'video-one',
        slug: isSecond ? 'video-two' : 'video-one',
        title: isSecond ? 'Segundo vídeo' : 'Primer vídeo',
        category: 'videos',
        workText: null,
        youTubeId: isSecond ? '38Oq_C4AxgA' : 'M7lc1UVf-VE',
      });
    });
    await userEvent.click(
      await screen.findByRole('button', { name: 'Reproducir vídeo' }, { timeout: 3000 }),
    );
    expect(screen.getByTitle('Primer vídeo')).toHaveAttribute(
      'src',
      expect.stringContaining('M7lc1UVf-VE'),
    );
    await act(async () => {
      await fetch.router.navigate('/content/video-two');
    });
    await screen.findByRole('heading', { name: 'Segundo vídeo' });
    expect(document.querySelector('iframe')).toBeNull();
    const nextPlay = await screen.findByRole(
      'button',
      { name: 'Reproducir vídeo' },
      { timeout: 3000 },
    );
    expect(nextPlay).toBeVisible();
    expect(document.querySelector('iframe')).toBeNull();
    expect(apiPlayer).toHaveBeenCalledOnce();
    await waitFor(() => expect(destroy).toHaveBeenCalledOnce());
    await userEvent.click(nextPlay);
    expect(screen.getByTitle('Segundo vídeo')).toHaveAttribute(
      'src',
      expect.stringContaining('38Oq_C4AxgA'),
    );
    delete window.YT;
  }, 10000);
});

describe('Navigation preserves unsaved work', () => {
  it('keeps the content heading node and its focus when the public detail finishes loading', async () => {
    let resolveDetail: ((response: Response) => void) | undefined;
    const pending = new Promise<Response>((resolve) => {
      resolveDetail = resolve;
    });
    mount('/content/primera-lectura', user, (path) =>
      path.endsWith('/catalog/content/primera-lectura') ? pending : undefined,
    );
    const heading = screen.getByRole('heading', { name: 'Contenido', level: 1 });
    expect(heading).toHaveFocus();
    await act(async () => {
      resolveDetail?.(json(reading));
    });
    await waitFor(() => expect(heading).toHaveTextContent(reading.title));
    expect(screen.getByRole('heading', { name: reading.title, level: 1 })).toBe(heading);
    expect(heading).toHaveFocus();
    const topline = document.querySelector('.catalog-topline')!;
    expect(
      topline.compareDocumentPosition(heading) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('focuses main while a protected initial route awaits its session and leaves subsequent input focus intact', async () => {
    let resolveSession: ((response: Response) => void) | undefined;
    const session = new Promise<Response>((resolve) => {
      resolveSession = resolve;
    });
    mount('/admin/content', owner, (path) => (path.endsWith('/identity/me') ? session : undefined));
    expect(document.querySelector('main')).toHaveFocus();
    expect(screen.getByText('Comprobando tu sesión…')).toBeVisible();
    const skip = screen.getByRole('link', { name: 'Saltar al contenido' });
    skip.focus();
    await act(async () => {
      resolveSession?.(json(owner));
    });
    await screen.findByRole('heading', { name: 'Contenidos' }, { timeout: 3000 });
    expect(skip).toHaveFocus();
  });
  it('cancels Back without losing reading edits and leaves only after accepting', async () => {
    const fetch = mount('/admin/content', owner, undefined, true);
    fireEvent.click(
      await screen.findByRole(
        'link',
        { name: 'Editar contenido Primera lectura' },
        { timeout: 3000 },
      ),
    );
    await screen.findByLabelText('Lectura completa', {}, { timeout: 3000 });
    fill('Lectura completa', 'Cambios que deben conservarse.');
    vi.mocked(window.confirm).mockReturnValue(false);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await waitFor(() => expect(window.confirm).toHaveBeenCalledOnce());
    await waitFor(() =>
      expect(fetch.router.state.location.pathname).toBe('/admin/content/reading-one'),
    );
    await waitFor(() =>
      expect(
        [...fetch.router.state.blockers.values()].every((blocker) => blocker.state === 'unblocked'),
      ).toBe(true),
    );
    expect(screen.getByLabelText('Lectura completa')).toHaveValue('Cambios que deben conservarse.');
    expect(writes(fetch, '/admin/content/reading-one')).toHaveLength(0);
    vi.mocked(window.confirm).mockReturnValue(true);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await screen.findByRole('heading', { name: 'Contenidos' }, { timeout: 3000 });
    expect(fetch.router.state.location.pathname).toBe('/admin/content');
    expect(window.confirm).toHaveBeenCalledTimes(2);
    expect(writes(fetch, '/admin/content/reading-one')).toHaveLength(0);
  }, 10000);
  it('cancels Back without losing subscription status or reason and accepts leaving without a PATCH', async () => {
    const fetch = mount('/admin/subscriptions', owner, undefined, true);
    fireEvent.click(
      await screen.findByRole('link', { name: 'Gestionar suscripción' }, { timeout: 3000 }),
    );
    await screen.findByLabelText('Motivo del cambio', {}, { timeout: 3000 });
    fill('Estado de la suscripción', 'suspended');
    fill('Motivo del cambio', 'Revisión pendiente');
    vi.mocked(window.confirm).mockReturnValue(false);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await waitFor(() => expect(window.confirm).toHaveBeenCalledOnce());
    await waitFor(() =>
      expect(fetch.router.state.location.pathname).toBe('/admin/subscriptions/subscription-one'),
    );
    await waitFor(() =>
      expect(
        [...fetch.router.state.blockers.values()].every((blocker) => blocker.state === 'unblocked'),
      ).toBe(true),
    );
    expect(screen.getByLabelText('Estado de la suscripción')).toHaveValue('suspended');
    expect(screen.getByLabelText('Motivo del cambio')).toHaveValue('Revisión pendiente');
    vi.mocked(window.confirm).mockReturnValue(true);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await screen.findByRole('heading', { name: 'Suscripciones' }, { timeout: 3000 });
    expect(fetch.router.state.location.pathname).toBe('/admin/subscriptions');
    expect(writes(fetch, '/admin/subscriptions/subscription-one')).toHaveLength(0);
  }, 10000);
  it('does not steal navigation when a pending draft creation finishes after the editor is left', async () => {
    let resolveCreate: ((response: Response) => void) | undefined;
    const pending = new Promise<Response>((resolve) => {
      resolveCreate = resolve;
    });
    const fetch = mount('/admin/content', owner, (path, init) =>
      path.endsWith('/admin/content') && init?.method === 'POST' ? pending : undefined,
    );
    fireEvent.click(
      await screen.findByRole('link', { name: 'Crear contenido' }, { timeout: 3000 }),
    );
    fill('Título', 'Borrador pendiente');
    fill('Dirección del contenido', 'borrador-pendiente');
    fireEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    await waitFor(() => expect(writes(fetch, '/admin/content')).toHaveLength(1));
    fireEvent.click(screen.getByRole('link', { name: 'Gestionar contenidos' }));
    await screen.findByRole('heading', { name: 'Contenidos' }, { timeout: 3000 });
    await act(async () => {
      resolveCreate?.(json({ ...reading, status: 'draft' }, 201));
    });
    expect(fetch.router.state.location.pathname).toBe('/admin/content');
    expect(screen.queryByLabelText('Lectura completa')).not.toBeInTheDocument();
    expect(window.confirm).toHaveBeenCalledOnce();
  }, 10000);
});

describe('Subscription account names use a bounded lookup', () => {
  const other = {
    id: '10000000-0000-0000-0000-000000000002',
    displayName: 'Otra persona',
    email: 'other@example.test',
    status: 'active',
    emailConfirmed: true,
  };
  const first = {
    id: user.id,
    displayName: user.displayName,
    email: user.email,
    status: 'active',
    emailConfirmed: true,
  };
  it('shows account names on every row with one lookup per page and deduplicated IDs', async () => {
    const fetch = mount('/admin/subscriptions', owner, (path) => {
      if (path.includes('/admin/subscriptions?')) {
        const next = new URL('http://test' + path).searchParams.get('page') === '2';
        return json({
          items: next
            ? [{ ...subscription, id: 'next-sub', userId: other.id }]
            : [
                subscription,
                { ...subscription, id: 'duplicate-sub' },
                { ...subscription, id: 'other-sub', userId: other.id },
              ],
          total: 21,
          page: next ? 2 : 1,
          pageSize: 20,
        });
      }
      if (path.includes('/accounts/lookup?')) {
        const ids = new URL('http://test' + path).searchParams.get('userIds')?.split(',') ?? [];
        return json([first, other].filter((account) => ids.includes(account.id)));
      }
      return undefined;
    });
    const main = within(document.querySelector('main')!);
    await main.findByText(other.displayName, {}, { timeout: 3000 });
    expect(main.getAllByText(user.displayName)).toHaveLength(2);
    const lookups = () =>
      fetch.mock.calls.filter(([path]) => String(path).includes('/accounts/lookup?'));
    expect(lookups()).toHaveLength(1);
    expect(
      new URL('http://test' + String(lookups()[0]?.[0])).searchParams
        .get('userIds')
        ?.split(',')
        .sort(),
    ).toEqual([user.id, other.id].sort());
    expect(main.queryByText(user.id, { exact: true })).not.toBeInTheDocument();
    fireEvent.click(main.getByRole('button', { name: 'Siguiente' }));
    await main.findByText('Página 2', {}, { timeout: 3000 });
    await main.findByText(other.email, {}, { timeout: 3000 });
    expect(lookups()).toHaveLength(2);
    expect(window.location.search).toBe('?page=2');
    expect(window.location.search).not.toMatch(/example|Persona/);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  }, 10000);
  it('keeps subscription rows usable while names load, reports a failed lookup safely and retries only metadata', async () => {
    let release: ((response: Response) => void) | undefined;
    const pending = new Promise<Response>((resolve) => {
      release = resolve;
    });
    let retry = false;
    const fetch = mount('/admin/subscriptions', owner, (path) =>
      path.includes('/accounts/lookup?') ? (retry ? json([first]) : pending) : undefined,
    );
    const main = within(document.querySelector('main')!);
    await main.findByText('Cargando datos de las cuentas…', {}, { timeout: 3000 });
    expect(main.getByRole('link', { name: 'Gestionar suscripción' })).toBeVisible();
    await act(async () => {
      release?.(json({ code: 'unavailable', detail: 'private lookup diagnostic' }, 503));
    });
    await main.findByText('No pudimos obtener los datos de algunas cuentas.');
    expect(main.getByText('Datos de la cuenta no disponibles.')).toBeVisible();
    expect(main.queryByText('private lookup diagnostic')).not.toBeInTheDocument();
    retry = true;
    fireEvent.click(main.getByRole('button', { name: 'Reintentar datos de cuentas' }));
    await main.findByText(user.email, {}, { timeout: 3000 });
    expect(
      fetch.mock.calls.filter(([path]) => String(path).includes('/accounts/lookup?')),
    ).toHaveLength(2);
    expect(
      fetch.mock.calls.filter(([path]) => String(path).includes('/admin/subscriptions?')),
    ).toHaveLength(1);
    expect(writes(fetch, '/admin/subscriptions')).toHaveLength(0);
  }, 10000);
  it('retries omitted accounts and shows the same authorized details without another lookup on status save', async () => {
    let missing = true;
    const fetch = mount('/admin/subscriptions/subscription-one', owner, (path) =>
      path.includes('/accounts/lookup?') ? json(missing ? [] : [first]) : undefined,
    );
    const main = within(document.querySelector('main')!);
    await main.findByText('Datos de la cuenta no disponibles.', {}, { timeout: 3000 });
    expect(main.queryByText('Cuenta: ' + user.id)).not.toBeInTheDocument();
    missing = false;
    fireEvent.click(main.getByRole('button', { name: 'Reintentar datos de cuentas' }));
    await main.findByText(user.email, {}, { timeout: 3000 });
    expect(main.getByText('Estado de la cuenta: Activa. Correo confirmado.')).toBeVisible();
    fill('Estado de la suscripción', 'suspended');
    fill('Motivo del cambio', 'Revisión administrativa');
    fireEvent.click(main.getByRole('button', { name: 'Guardar estado' }));
    await main.findByText('Guardamos el estado de la suscripción.');
    expect(
      fetch.mock.calls.filter(([path]) => String(path).includes('/accounts/lookup?')),
    ).toHaveLength(2);
    expect(writes(fetch, '/admin/subscriptions/subscription-one')).toHaveLength(1);
  }, 10000);
});

describe('YouTube URL entry preserves the editorial API contract', () => {
  it.each([
    ['videos', 'https://www.youtube.com/watch?v=M7lc1UVf-VE&si=share&t=90'],
    ['podcast', 'https://youtu.be/M7lc1UVf-VE?si=share'],
  ])('saves a pasted URL in %s as only an eleven-character identifier', async (category, input) => {
    const fetch = mount('/admin/content/reading-one', owner, (path, init) =>
      path.endsWith('/admin/content/reading-one') && init?.method !== 'PUT'
        ? json({ ...reading, category, workText: null, youTubeId: null })
        : undefined,
    );
    const field = await screen.findByLabelText(
      'Enlace o identificador de YouTube',
      {},
      { timeout: 3000 },
    );
    expect(field).toHaveAttribute('maxlength', '2048');
    fill('Enlace o identificador de YouTube', input);
    expect(field).toHaveValue(input);
    expect(writes(fetch, '/admin/content/reading-one')).toHaveLength(0);
    expect(document.querySelector('iframe, script[src*="youtube"]')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.', {}, { timeout: 3000 });
    const calls = writes(fetch, '/admin/content/reading-one');
    expect(calls).toHaveLength(1);
    const payload = JSON.parse(String(calls[0]?.[1]?.body));
    expect(payload).toMatchObject({
      category,
      youTubeId: 'M7lc1UVf-VE',
      workText: null,
      version: 'v1',
    });
    expect(JSON.stringify(payload)).not.toContain('https://');
    expect(document.querySelector('iframe, script[src*="youtube"]')).toBeNull();
  });

  it('creates a video draft from a pasted live URL without storing that URL', async () => {
    const fetch = mount('/admin/content/new', owner);
    await screen.findByLabelText('Título', {}, { timeout: 3000 });
    fill('Título', 'Vídeo autorizado');
    fill('Dirección del contenido', 'video-autorizado');
    fireEvent.change(screen.getByLabelText('Categoría del contenido'), {
      target: { value: 'videos' },
    });
    fill('Enlace o identificador de YouTube', 'https://www.youtube.com/live/M7lc1UVf-VE?si=share');
    expect(writes(fetch, '/admin/content')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    await waitFor(() => expect(writes(fetch, '/admin/content')).toHaveLength(1), { timeout: 3000 });
    const payload = JSON.parse(String(writes(fetch, '/admin/content')[0]?.[1]?.body));
    expect(payload).toMatchObject({
      title: 'Vídeo autorizado',
      slug: 'video-autorizado',
      category: 'videos',
      youTubeId: 'M7lc1UVf-VE',
      workText: null,
    });
    expect(JSON.stringify(payload)).not.toContain('https://');
    await waitFor(() => expect(window.location.pathname).toBe('/admin/content/reading-one'), {
      timeout: 3000,
    });
    expect(document.querySelector('iframe, script[src*="youtube"]')).toBeNull();
  });

  it('continues to accept an existing eleven-character identifier', async () => {
    const fetch = mount('/admin/content/reading-one', owner, (path, init) =>
      path.endsWith('/admin/content/reading-one') && init?.method !== 'PUT'
        ? json({ ...reading, category: 'videos', workText: null, youTubeId: 'M7lc1UVf-VE' })
        : undefined,
    );
    await screen.findByLabelText('Enlace o identificador de YouTube', {}, { timeout: 3000 });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.', {}, { timeout: 3000 });
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/reading-one')[0]?.[1]?.body)),
    ).toMatchObject({ youTubeId: 'M7lc1UVf-VE', version: 'v1' });
  });

  it.each([
    'https://youtube.com.example.test/watch?v=M7lc1UVf-VE',
    'https://user@www.youtube.com/watch?v=M7lc1UVf-VE',
    'https://youtu.be/M7lc1UVf-V',
  ])('keeps an invalid URL editable with accessible feedback and no write: %s', async (input) => {
    const fetch = mount('/admin/content/reading-one', owner, (path) =>
      path.endsWith('/admin/content/reading-one')
        ? json({ ...reading, category: 'videos', workText: null, youTubeId: null })
        : undefined,
    );
    const field = await screen.findByLabelText(
      'Enlace o identificador de YouTube',
      {},
      { timeout: 3000 },
    );
    fill('Enlace o identificador de YouTube', input);
    fireEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText(
      'Pega un enlace HTTPS válido de YouTube o su identificador de 11 caracteres.',
    );
    expect(field).toHaveValue(input);
    expect(field).toHaveAttribute('aria-invalid', 'true');
    expect(field).toHaveAttribute('aria-describedby', 'editor-youTubeId-help');
    expect(field).toHaveFocus();
    expect(writes(fetch, '/admin/content/reading-one')).toHaveLength(0);
    expect(document.querySelector('iframe, script[src*="youtube"]')).toBeNull();
  });

  it('clears a previous video reference explicitly using the existing null payload', async () => {
    const fetch = mount('/admin/content/reading-one', owner, (path, init) =>
      path.endsWith('/admin/content/reading-one') && init?.method !== 'PUT'
        ? json({ ...reading, category: 'videos', workText: null, youTubeId: 'M7lc1UVf-VE' })
        : undefined,
    );
    await screen.findByLabelText('Enlace o identificador de YouTube', {}, { timeout: 3000 });
    fill('Enlace o identificador de YouTube', '');
    fireEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.', {}, { timeout: 3000 });
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/reading-one')[0]?.[1]?.body)),
    ).toMatchObject({ youTubeId: null, version: 'v1' });
  });
});

describe('Explicit free works and effective plan access', () => {
  it('starts a new editorial work with free access unchecked and saves that explicit value', async () => {
    const fetch = mount('/admin/content/new', owner);
    const free = await screen.findByRole('checkbox', { name: 'Disponible con el plan Gratuito' });
    expect(free).not.toBeChecked();
    fill('Título', 'Obra de acceso completo');
    fill('Dirección del contenido', 'obra-de-acceso-completo');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    await waitFor(() => expect(writes(fetch, '/admin/content')).toHaveLength(1));
    expect(JSON.parse(String(writes(fetch, '/admin/content')[0]?.[1]?.body)).isFree).toBe(false);
  });
  it('preserves an edited free flag and full text on a version conflict', async () => {
    const fetch = mount('/admin/content/reading-one', owner, (path, init) =>
      path.endsWith('/admin/content/reading-one') && init?.method === 'PUT'
        ? json({ code: 'concurrency_conflict' }, 409)
        : undefined,
    );
    const free = await screen.findByRole('checkbox', { name: 'Disponible con el plan Gratuito' });
    expect(free).toBeChecked();
    await userEvent.click(free);
    fill('Lectura completa', 'Texto que debe conservarse.');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Conservamos tus cambios');
    expect(free).not.toBeChecked();
    expect(screen.getByLabelText('Lectura completa')).toHaveValue('Texto que debe conservarse.');
    expect(
      JSON.parse(String(writes(fetch, '/admin/content/reading-one')[0]?.[1]?.body)),
    ).toMatchObject({ isFree: false, version: 'v1', workText: 'Texto que debe conservarse.' });
  });
  it('does not inherit a free collection flag to a protected child and offers the real plan page', async () => {
    const paid = { ...reading, isFree: false };
    mount('/content/curso-real', user, (path) => {
      if (path.endsWith('/catalog/content/curso-real'))
        return json({ ...course, items: [paid, second] });
      if (path.endsWith('/catalog/content/primera-lectura')) return json(paid);
      if (path.endsWith('/consumption/content/primera-lectura'))
        return json({ code: 'content_requires_plan' }, 403);
      return undefined;
    });
    await screen.findByRole('heading', { name: 'Curso real', level: 1 });
    const collection = document.querySelector('.collection-items')!;
    expect(
      within(collection as HTMLElement).getByRole('link', {
        name: 'Primera lectura · Autora real',
      }),
    ).not.toHaveTextContent('Gratuito');
    expect(
      within(collection as HTMLElement).getByRole('link', {
        name: 'Segunda lectura · Autora real',
      }),
    ).toHaveTextContent('Gratuito');
    expect(
      within(collection as HTMLElement).getByRole('link', {
        name: 'Segunda lectura · Autora real',
      }),
    ).toHaveAccessibleDescription('Gratuito');
    await userEvent.click(
      within(collection as HTMLElement).getByRole('link', {
        name: 'Primera lectura · Autora real',
      }),
    );
    expect(
      await screen.findByRole('heading', { name: 'Esta obra requiere otro plan.' }),
    ).toBeVisible();
    expect(screen.getByRole('link', { name: 'Ver mi suscripción' })).toHaveAttribute(
      'href',
      '/profile/subscription?content=primera-lectura',
    );
    expect(screen.queryByRole('article', { name: 'Lectura completa' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Suscribirme gratis' })).not.toBeInTheDocument();
  });
  it.each(['scheduled', 'expired', 'cancelled'])(
    'shows full-plan %s dates without a self activation or invented extension',
    async (effectiveState) => {
      const full = {
        ...subscription,
        plan: 'annual',
        expiresUtc: '2027-10-06T12:00:00Z',
        effectiveState,
        status: effectiveState === 'cancelled' ? 'cancelled' : 'active',
      };
      const fetch = mount('/profile/subscription', user, (path) =>
        path.endsWith('/subscriptions/me')
          ? json({ subscription: full, eligibleToActivate: false })
          : undefined,
      );
      const planHeading = await screen.findByRole('heading', { name: 'Anual', level: 2 });
      const planSection = planHeading.closest('section');
      expect(planSection).not.toBeNull();
      const planSummary = within(planSection!);
      expect(document.querySelector('time[datetime="2027-10-06T12:00:00Z"]')).toBeVisible();
      expect(
        screen.getByText(
          'Fechas en hora de Lima (UTC−05:00). El acceso termina al llegar al vencimiento.',
        ),
      ).toBeVisible();
      expect(screen.queryByRole('button', { name: 'Suscribirme gratis' })).not.toBeInTheDocument();
      expect(planSummary.queryByRole('link', { name: 'Explorar la mediateca' })).not.toBeInTheDocument();
      expect(
        within(screen.getByRole('contentinfo')).getByRole('link', { name: 'Explorar la mediateca' }),
      ).toHaveAttribute('href', '/explore');
      expect(writes(fetch, '/subscriptions/activate')).toHaveLength(0);
    },
  );
});

describe('Manual plan assignment is reviewed, versioned and recoverable', () => {
  const route = '/admin/subscriptions/accounts/' + user.id;
  const assignRoute = '/admin/subscriptions/accounts/' + user.id + '/assign';
  const full = {
    ...subscription,
    plan: 'probationismo',
    startsUtc: '2026-10-07T14:30:00.123456Z',
    expiresUtc: '2027-01-07T14:30:00.123456Z',
    version: 'full-v1',
  };
  const renewed = { ...full, expiresUtc: '2027-04-07T14:30:00.123456Z', version: 'full-v2' };
  function assigned(override?: Override) {
    return mount(route, owner, override, true);
  }
  async function form() {
    return within(
      await screen.findByRole('form', { name: 'Asignación manual de plan' }, { timeout: 3000 }),
    );
  }
  it('assigns a first full plan only after review, converting Lima input and keeping version null', async () => {
    const fetch = assigned((path, init) => {
      if (path.includes('/admin/subscriptions?'))
        return json({ items: [], total: 0, page: 1, pageSize: 20 });
      if (path.endsWith(assignRoute) && init?.method === 'POST')
        return json({
          ...subscription,
          plan: 'annual',
          startsUtc: '2026-10-07T14:30:00.000Z',
          expiresUtc: '2027-10-07T14:30:00Z',
          version: 'new-v1',
        });
      return undefined;
    });
    const fields = await form();
    expect(writes(fetch, assignRoute)).toHaveLength(0);
    await userEvent.selectOptions(fields.getByLabelText('Plan'), 'annual');
    fireEvent.change(fields.getByLabelText('Inicio del período (hora de Lima)'), {
      target: { value: '2026-10-07T09:30:00' },
    });
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Período autorizado');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    expect(writes(fetch, assignRoute)).toHaveLength(0);
    await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Guardamos el plan Anual.');
    expect(JSON.parse(String(writes(fetch, assignRoute)[0]?.[1]?.body))).toEqual({
      plan: 'annual',
      startsUtc: '2026-10-07T14:30:00.000Z',
      version: null,
      reason: 'Período autorizado',
    });
    expect(screen.getByRole('link', { name: 'Revisar el cambio en la auditoría' })).toHaveAttribute(
      'href',
      '/admin/audit?module=subscriptions&subscriptionId=subscription-one',
    );
  }, 10000);
  it('renews from the exact existing expiry and presents the server period while preserving the original start', async () => {
    const fetch = assigned((path, init) => {
      if (path.includes('/admin/subscriptions?'))
        return json({ items: [full], total: 1, page: 1, pageSize: 20 });
      if (path.endsWith(assignRoute) && init?.method === 'POST') return json(renewed);
      return undefined;
    });
    const fields = await form();
    expect(fields.getByLabelText('Plan')).toBeDisabled();
    expect(fields.getByLabelText('Inicio del período (hora de Lima)')).toHaveValue(
      '2027-01-07T09:30',
    );
    expect(fields.getByLabelText('Inicio del período (hora de Lima)')).toHaveAttribute('readonly');
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Renovación anticipada');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar renovación' }));
    expect(
      screen.getByText(
        'Confirma la cuenta, el plan y la fecha antes de registrar el cambio. El inicio del acceso actual se conservará.',
      ),
    ).toBeVisible();
    expect(writes(fetch, assignRoute)).toHaveLength(0);
    await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Guardamos el plan Probacionismo.');
    expect(JSON.parse(String(writes(fetch, assignRoute)[0]?.[1]?.body))).toEqual({
      plan: 'probationismo',
      startsUtc: full.expiresUtc,
      version: 'full-v1',
      reason: 'Renovación anticipada',
    });
    expect(screen.getByRole('status')).toHaveTextContent('Acceso desde');
    expect(screen.getByRole('status')).not.toHaveTextContent('Acceso desde 7 ene');
    expect(fields.getByLabelText('Motivo de la asignación')).toHaveValue('');
  }, 10000);
  it('makes a change to Free explicit with no invented start and with the current full version', async () => {
    const fetch = assigned((path, init) => {
      if (path.includes('/admin/subscriptions?'))
        return json({ items: [full], total: 1, page: 1, pageSize: 20 });
      if (path.endsWith(assignRoute) && init?.method === 'POST')
        return json({ ...subscription, version: 'free-v2' });
      return undefined;
    });
    const fields = await form();
    await userEvent.click(screen.getByRole('button', { name: 'Asignar otro período o plan' }));
    await userEvent.selectOptions(fields.getByLabelText('Plan'), 'free_beta');
    expect(fields.queryByLabelText('Inicio del período (hora de Lima)')).not.toBeInTheDocument();
    expect(fields.getByText(/Sustituirá el alcance del plan actual/)).toBeVisible();
    await userEvent.type(
      fields.getByLabelText('Motivo de la asignación'),
      'Cambio autorizado a Gratuito',
    );
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    expect(writes(fetch, assignRoute)).toHaveLength(0);
    await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
    await screen.findByRole('status');
    expect(JSON.parse(String(writes(fetch, assignRoute)[0]?.[1]?.body))).toEqual({
      plan: 'free_beta',
      startsUtc: null,
      version: 'full-v1',
      reason: 'Cambio autorizado a Gratuito',
    });
  }, 10000);
  it('focuses the invalid field and sends no request until a reason and valid date are supplied', async () => {
    const fetch = assigned();
    const fields = await form();
    await userEvent.selectOptions(fields.getByLabelText('Plan'), 'annual');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    expect(fields.getByLabelText('Inicio del período (hora de Lima)')).toHaveFocus();
    fireEvent.change(fields.getByLabelText('Inicio del período (hora de Lima)'), {
      target: { value: '2026-10-07T09:30:00' },
    });
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    expect(fields.getByLabelText('Motivo de la asignación')).toHaveFocus();
    expect(writes(fetch, assignRoute)).toHaveLength(0);
  });
  it.each([429, 503])(
    'preserves reviewed values after HTTP %i and retries only on a deliberate click',
    async (status) => {
      let fail = true;
      const fetch = assigned((path, init) =>
        path.endsWith(assignRoute) && init?.method === 'POST'
          ? fail
            ? json(
                { code: status === 429 ? 'rate_limited' : 'unavailable', detail: 'private' },
                status,
              )
            : json({ ...subscription, version: 'new-v2' })
          : undefined,
      );
      const fields = await form();
      await userEvent.type(
        fields.getByLabelText('Motivo de la asignación'),
        'Acceso gratuito autorizado',
      );
      await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
      await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
      await screen.findByRole('alert');
      expect(writes(fetch, assignRoute)).toHaveLength(1);
      expect(fields.getByLabelText('Motivo de la asignación')).toHaveValue(
        'Acceso gratuito autorizado',
      );
      expect(screen.queryByText('private')).not.toBeInTheDocument();
      fail = false;
      await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
      await screen.findByRole('status');
      expect(writes(fetch, assignRoute)).toHaveLength(2);
    },
    10000,
  );
  it('blocks a stale-version retry and preserves values until an explicit accepted reload', async () => {
    const fetch = assigned((path, init) =>
      path.endsWith(assignRoute) && init?.method === 'POST'
        ? json({ code: 'concurrency_conflict' }, 409)
        : undefined,
    );
    const fields = await form();
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Motivo conservado');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
    await screen.findByRole('alert');
    expect(fields.getByRole('button', { name: 'Confirmar asignación' })).toBeDisabled();
    expect(fields.getByLabelText('Motivo de la asignación')).toHaveValue('Motivo conservado');
    vi.mocked(window.confirm).mockReturnValue(false);
    await userEvent.click(screen.getByRole('button', { name: 'Recargar suscripción' }));
    expect(fields.getByLabelText('Motivo de la asignación')).toHaveValue('Motivo conservado');
    expect(writes(fetch, assignRoute)).toHaveLength(1);
    vi.mocked(window.confirm).mockReturnValue(true);
    await userEvent.click(screen.getByRole('button', { name: 'Recargar suscripción' }));
    expect((await form()).getByLabelText('Motivo de la asignación')).toHaveValue('');
    expect(writes(fetch, assignRoute)).toHaveLength(1);
  }, 10000);
  it('does not silently replace a live period when the server rejects a future assignment', async () => {
    const fetch = assigned((path, init) => {
      if (path.includes('/admin/subscriptions?'))
        return json({ items: [full], total: 1, page: 1, pageSize: 20 });
      return path.endsWith(assignRoute) && init?.method === 'POST'
        ? json({ code: 'active_period_would_be_replaced' }, 409)
        : undefined;
    });
    const fields = await form();
    await userEvent.click(screen.getByRole('button', { name: 'Asignar otro período o plan' }));
    await userEvent.selectOptions(fields.getByLabelText('Plan'), 'annual');
    fireEvent.change(fields.getByLabelText('Inicio del período (hora de Lima)'), {
      target: { value: '2027-01-07T09:30:00' },
    });
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Período futuro');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    await userEvent.click(fields.getByRole('button', { name: 'Confirmar asignación' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('sustituiría el acceso actual');
    expect(fields.getByLabelText('Inicio del período (hora de Lima)')).toHaveValue(
      '2027-01-07T09:30',
    );
    expect(writes(fetch, assignRoute)).toHaveLength(1);
    expect(screen.queryByText(/Guardamos el plan/)).not.toBeInTheDocument();
  });
  it('prevents duplicate assignment and ignores a delayed response after leaving the route', async () => {
    let resolveAssignment: ((response: Response) => void) | undefined;
    const deferred = new Promise<Response>((resolve) => {
      resolveAssignment = resolve;
    });
    const fetch = assigned((path, init) =>
      path.endsWith(assignRoute) && init?.method === 'POST' ? deferred : undefined,
    );
    const fields = await form();
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Respuesta pendiente');
    await userEvent.click(fields.getByRole('button', { name: 'Revisar asignación' }));
    const confirm = fields.getByRole('button', { name: 'Confirmar asignación' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    await waitFor(() => expect(writes(fetch, assignRoute)).toHaveLength(1));
    expect(fields.getByLabelText('Motivo de la asignación')).toBeDisabled();
    await act(async () => {
      await fetch.router.navigate('/explore');
    });
    await act(async () => {
      resolveAssignment?.(json({ ...subscription, version: 'late-v2' }));
    });
    expect(fetch.router.state.location.pathname).toBe('/explore');
    expect(screen.queryByText(/Guardamos el plan/)).not.toBeInTheDocument();
  });
  it('keeps a draft when Back is dismissed and never posts during navigation', async () => {
    const fetch = mount('/admin/subscriptions', owner, undefined, true);
    fireEvent.click(
      await screen.findByRole('link', { name: 'Gestionar suscripción' }, { timeout: 3000 }),
    );
    const fields = await form();
    await userEvent.type(fields.getByLabelText('Motivo de la asignación'), 'Cambio por revisar');
    vi.mocked(window.confirm).mockReturnValue(false);
    await act(async () => {
      await fetch.router.navigate(-1);
    });
    await waitFor(() => expect(window.confirm).toHaveBeenCalledOnce());
    expect(fields.getByLabelText('Motivo de la asignación')).toHaveValue('Cambio por revisar');
    expect(fetch.router.state.location.pathname).toBe('/admin/subscriptions/subscription-one');
    expect(writes(fetch, assignRoute)).toHaveLength(0);
  });
  it('requires the subscription permission on the assignment route before making a lookup or write', async () => {
    const fetch = mount(route, { ...user, permissions: ['Content.Manage'] }, undefined, true);
    expect(
      await screen.findByRole('heading', { name: 'Este espacio requiere autorización.', level: 1 }),
    ).toBeVisible();
    expect(screen.getByRole('link', { name: 'Ir a mi perfil' })).toHaveAttribute('href', '/profile');
    expect(fetch.mock.calls.some(([path]) => String(path).includes('/admin/subscriptions'))).toBe(
      false,
    );
  });
});

describe('Plan and status edits keep separate unsaved intent', () => {
  it('blocks the other form while a status or plan draft is present and unlocks when cleared', async () => {
    mount('/admin/subscriptions/subscription-one', owner);
    const statusReason = await screen.findByLabelText('Motivo del cambio', {}, { timeout: 3000 });
    const planReason = screen.getByLabelText('Motivo de la asignación');
    await userEvent.type(statusReason, 'Cambio de estado pendiente');
    expect(planReason).toBeDisabled();
    expect(screen.getByLabelText('Plan')).toBeDisabled();
    await userEvent.clear(statusReason);
    expect(planReason).toBeEnabled();
    await userEvent.type(planReason, 'Cambio de plan pendiente');
    expect(statusReason).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Guardar estado' })).toBeDisabled();
    await userEvent.clear(planReason);
    expect(statusReason).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Guardar estado' })).toBeEnabled();
  });
});
