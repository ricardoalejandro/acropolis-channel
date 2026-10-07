import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderMemoryApp } from '../../test/renderApp';
import { user, json } from '../../test/fixtures';
import { clearCsrf } from '../../api/identity';
import {
  topic,
  otherTopic,
  topicId,
  contentId,
  topicVersion,
  nextTopicVersion,
  topicDirectory,
  contentTopics,
} from '../../test/topicFixtures';
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(
  path = '/admin/topics',
  override?: Override,
  permission: string | null = 'Content.Manage',
) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    if (url.endsWith('/identity/me'))
      return permission === null
        ? json({ code: 'unauthorized' }, 401)
        : json({ ...user, permissions: [permission] });
    if (url.endsWith('/identity/capabilities')) return json({ emailEnabled: true });
    if (url.endsWith('/identity/csrf')) return json({ token: 'synthetic-topic-csrf' });
    if (url.includes('/admin/catalog/topics?')) return json(topicDirectory);
    if (url.endsWith('/admin/catalog/topics/' + topicId))
      return json(
        init?.method === 'PUT'
          ? { ...topic, name: JSON.parse(String(init.body)).name, version: nextTopicVersion }
          : topic,
      );
    if (url.endsWith('/admin/catalog/topics/' + topicId + '/state'))
      return json({
        ...topic,
        status: JSON.parse(String(init?.body)).status,
        version: nextTopicVersion,
      });
    if (url.endsWith('/admin/catalog/topics/order'))
      return json({ ...topic, position: 1, version: nextTopicVersion });
    if (url.endsWith('/admin/catalog/topics') && init?.method === 'POST')
      return json({ ...topic, ...JSON.parse(String(init.body)) }, 201);
    if (url.endsWith('/admin/content/' + contentId + '/topics')) {
      if (init?.method === 'PUT') {
        const ids = JSON.parse(String(init.body)).topicIds as string[];
        return json({
          ...contentTopics,
          contentVersion: nextTopicVersion,
          items: [topic, otherTopic].filter((item) => ids.includes(item.id)),
        });
      }
      return json(contentTopics);
    }
    if (url.includes('/catalog/topics?')) return json({ ...topicDirectory, pageSize: 100 });
    if (url.includes('/catalog/content?'))
      return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  return { ...renderMemoryApp(path), fetch };
}
const writes = (fetch: ReturnType<typeof mount>['fetch']) =>
  fetch.mock.calls.filter(([, init]) => init?.method !== 'GET');
beforeEach(() => {
  clearCsrf();
  vi.stubGlobal('scrollTo', vi.fn());
  vi.spyOn(window, 'confirm').mockReturnValue(false);
});
describe('Real topic management surfaces', () => {
  it('offers a permission-specific directory and no automatic writes or fake seed terms', async () => {
    const store = vi.spyOn(Storage.prototype, 'setItem');
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount();
    });
    const section = screen.getByRole('region', { name: 'Directorio de temas' });
    expect(section).toBeVisible();
    expect(section).toHaveAttribute('aria-busy', 'false');
    expect(within(section).getByRole('link', { name: topic.name })).toHaveAttribute(
      'href',
      '/admin/topics/' + topicId,
    );
    expect(
      within(section).getByRole('button', { name: 'Subir tema ' + topic.name }),
    ).toBeDisabled();
    expect(writes(rendered.fetch)).toEqual([]);
    expect(store).not.toHaveBeenCalled();
  }, 10000);
  it('treats an empty directory as editable, with no predefined taxonomy', async () => {
    await act(async () => {
      mount(undefined, (url) =>
        url.includes('/admin/catalog/topics?')
          ? json({ ...topicDirectory, items: [], total: 0 })
          : undefined,
      );
    });
    expect(
      screen.getByText('No hay temas con estos filtros. Puedes crear un tema cuando lo necesites.'),
    ).toBeVisible();
    expect(screen.getByRole('link', { name: 'Crear tema' })).toBeVisible();
  }, 10000);
  it.each(['Users.Manage', 'Subscriptions.Manage', ''])(
    'does not allow topic maintenance with unrelated permission %s',
    async (permission) => {
      let rendered!: ReturnType<typeof mount>;
      await act(async () => {
        rendered = mount(undefined, undefined, permission);
      });
      expect(
        screen.getByRole('heading', { name: 'Este espacio requiere autorización.' }),
      ).toBeVisible();
      expect(
        rendered.fetch.mock.calls.some(([url]) => String(url).includes('/admin/catalog/topics')),
      ).toBe(false);
    },
    10000,
  );
  it('routes anonymous access to login without fetching the directory', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount(undefined, undefined, null);
    });
    expect(rendered.router.state.location.pathname).toBe('/login');
    expect(
      rendered.fetch.mock.calls.some(([url]) => String(url).includes('/admin/catalog/topics')),
    ).toBe(false);
  }, 10000);
  it('creates an explicit topic with exact fields and preserved stable slug', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/topics/new');
    });
    const name = screen.getByLabelText('Nombre del tema');
    const slug = screen.getByLabelText('Dirección del tema');
    await userEvent.type(name, 'Idea de QA');
    await userEvent.type(slug, 'idea-de-qa');
    expect(writes(rendered.fetch)).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar tema' }));
    await screen.findByRole('heading', { name: 'Editar tema' });
    expect(JSON.parse(String(writes(rendered.fetch)[0]?.[1]?.body))).toEqual({
      slug: 'idea-de-qa',
      name: 'Idea de QA',
    });
    expect(rendered.router.state.location.pathname).toBe('/admin/topics/' + topicId);
    expect(screen.queryByLabelText('Dirección del tema')).not.toBeInTheDocument();
  }, 10000);
  it('rejects empty fields accessibly and focuses the first invalid input without a POST', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/topics/new');
    });
    await userEvent.click(screen.getByRole('button', { name: 'Guardar tema' }));
    expect(screen.getByLabelText('Nombre del tema')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Nombre del tema')).toHaveFocus();
    expect(screen.getByLabelText('Dirección del tema')).toHaveAttribute('aria-invalid', 'true');
    expect(writes(rendered.fetch)).toHaveLength(0);
  }, 10000);
  it('requires confirmation before archive and retains the topic URL and association help', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/topics/' + topicId);
    });
    await userEvent.click(screen.getByRole('button', { name: 'Archivar tema' }));
    expect(writes(rendered.fetch)).toHaveLength(0);
    expect(screen.getByRole('alert')).toHaveTextContent('asociaciones se conservan');
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar archivo de tema' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Tema archivado');
    expect(JSON.parse(String(writes(rendered.fetch)[0]?.[1]?.body))).toEqual({
      version: topicVersion,
      status: 'archived',
    });
    expect(screen.getByText(topic.slug, { selector: 'span' })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Restaurar tema' }));
    expect(writes(rendered.fetch)).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar restauración de tema' }));
    expect(JSON.parse(String(writes(rendered.fetch)[1]?.[1]?.body))).toEqual({
      version: nextTopicVersion,
      status: 'active',
    });
  }, 10000);
  it('preserves a name on version conflict and refuses reload when discard is cancelled', async () => {
    await act(async () => {
      mount('/admin/topics/' + topicId, (_url, init) =>
        init?.method === 'PUT' ? json({ code: 'concurrency_conflict' }, 409) : undefined,
      );
    });
    const field = screen.getByLabelText('Nombre del tema');
    await userEvent.clear(field);
    await userEvent.type(field, 'Edición conservada');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar tema' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Conservamos tus cambios');
    expect(field).toHaveValue('Edición conservada');
    await userEvent.click(screen.getByRole('button', { name: 'Recargar tema' }));
    expect(window.confirm).toHaveBeenCalled();
    expect(field).toHaveValue('Edición conservada');
  }, 10000);
  it('moves only on click using a constant-size directory CAS command', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount();
    });
    const section = screen.getByRole('region', { name: 'Directorio de temas' });
    await userEvent.click(
      within(section).getByRole('button', { name: 'Bajar tema ' + topic.name }),
    );
    await waitFor(() => expect(writes(rendered.fetch)).toHaveLength(1), { timeout: 3000 });
    expect(JSON.parse(String(writes(rendered.fetch)[0]?.[1]?.body))).toEqual({
      directoryVersion: topicVersion,
      id: topicId,
      beforeId: null,
    });
  }, 10000);
  it('retains already-associated archived topics and only removes them by explicit action', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/content/' + contentId + '/topics', (url, init) =>
        url.endsWith('/content/' + contentId + '/topics') && init?.method === 'GET'
          ? json({ ...contentTopics, items: [{ ...topic, status: 'archived' }] })
          : undefined,
      );
    });
    expect(screen.getByText(topic.name + ' · Archivado (se conserva)')).toBeVisible();
    expect(writes(rendered.fetch)).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: 'Retirar tema ' + topic.name }));
    await userEvent.click(screen.getByRole('button', { name: 'Guardar temas del contenido' }));
    expect(JSON.parse(String(writes(rendered.fetch)[0]?.[1]?.body))).toEqual({
      contentVersion: topicVersion,
      topicIds: [],
    });
    expect(await screen.findByText('Temas del contenido guardados.')).toBeVisible();
  }, 10000);
  it('retries metadata independently without losing a selection or sending a write', async () => {
    let reads = 0;
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/content/' + contentId + '/topics', (url) =>
        url.includes('/admin/catalog/topics?') && ++reads === 1
          ? json({ code: 'service_unavailable' }, 503)
          : undefined,
      );
    });
    expect(screen.getByRole('button', { name: 'Retirar tema ' + topic.name })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar carga de temas' }));
    expect(await screen.findByRole('checkbox', { name: otherTopic.name })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Retirar tema ' + topic.name })).toBeVisible();
    expect(writes(rendered.fetch)).toHaveLength(0);
    expect(reads).toBe(2);
  }, 10000);
  it('blocks duplicate submit while retaining keyboard focus and uses only the selected ids', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/content/' + contentId + '/topics', (_url, init) =>
        init?.method === 'PUT' ? pending : undefined,
      );
    });
    await userEvent.click(screen.getByRole('checkbox', { name: otherTopic.name }));
    const save = screen.getByRole('button', { name: 'Guardar temas del contenido' });
    save.focus();
    await userEvent.keyboard('{Enter}');
    expect(save).toHaveFocus();
    expect(save).toHaveAttribute('aria-disabled', 'true');
    fireEvent.submit(save.closest('form')!);
    expect(writes(rendered.fetch)).toHaveLength(1);
    await act(async () => {
      resolve(
        json({ ...contentTopics, contentVersion: nextTopicVersion, items: [topic, otherTopic] }),
      );
    });
    expect(save).toHaveFocus();
    expect(save).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByText('Temas del contenido guardados.')).toBeVisible();
  }, 10000);
  it('keeps selection and explicit retry after an unavailable or archived-topic conflict', async () => {
    await act(async () => {
      mount('/admin/content/' + contentId + '/topics', (_url, init) =>
        init?.method === 'PUT' ? json({ code: 'topic_unavailable' }, 409) : undefined,
      );
    });
    await userEvent.click(screen.getByRole('checkbox', { name: otherTopic.name }));
    await userEvent.click(screen.getByRole('button', { name: 'Guardar temas del contenido' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Conservamos tu selección');
    expect(screen.getByRole('checkbox', { name: otherTopic.name })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Retirar tema ' + topic.name })).toBeVisible();
  }, 10000);
  it('loads optional public topic metadata only when opened and submits combined public filters', async () => {
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/explore');
    });
    expect(
      rendered.fetch.mock.calls.some(([url]) => String(url).includes('/catalog/topics?')),
    ).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Filtrar por tema' }));
    const selected = await screen.findByLabelText('Tema');
    await userEvent.selectOptions(selected, topic.slug);
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'lecturas');
    await userEvent.type(screen.getByLabelText('Buscar contenido'), '50%_');
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await waitFor(() => expect(rendered.router.state.location.search).toContain('topic=tema-qa'), {
      timeout: 3000,
    });
    const url = new URL(
      String(
        rendered.fetch.mock.calls
          .filter(([url]) => String(url).includes('/catalog/content?'))
          .at(-1)?.[0],
      ),
      'https://qa-topic.test',
    );
    expect(url.searchParams.get('topic')).toBe(topic.slug);
    expect(url.searchParams.get('category')).toBe('lecturas');
    expect(url.searchParams.get('search')).toBe('50%_');
    expect(writes(rendered.fetch)).toHaveLength(0);
  }, 10000);
  it('limits selection to twelve without silently dropping existing topics', async () => {
    const available = Array.from({ length: 13 }, (_, index) => ({
      ...topic,
      id: '40000000-0000-0000-0000-' + String(index + 1).padStart(12, '0'),
      slug: 'limite-' + index,
      name: 'Tema límite ' + index,
      position: index,
    }));
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/content/' + contentId + '/topics', (url, init) => {
        if (url.includes('/admin/catalog/topics?'))
          return json({ ...topicDirectory, items: available, total: 13 });
        if (init?.method === 'PUT') {
          const ids = JSON.parse(String(init.body)).topicIds as string[];
          return json({
            ...contentTopics,
            contentVersion: nextTopicVersion,
            items: available.filter((item) => ids.includes(item.id)),
          });
        }
        return undefined;
      });
    });
    for (const item of available.slice(1, 12))
      await userEvent.click(screen.getByRole('checkbox', { name: item.name }));
    await userEvent.click(screen.getByRole('checkbox', { name: available[12]!.name }));
    expect(screen.getByRole('alert')).toHaveTextContent('Puedes seleccionar hasta 12 temas.');
    expect(screen.getByRole('checkbox', { name: available[12]!.name })).not.toBeChecked();
    expect(screen.getByRole('heading', { name: 'Selección actual (12/12)' })).toBeVisible();
    expect(writes(rendered.fetch)).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar temas del contenido' }));
    await screen.findByText('Temas del contenido guardados.');
    expect(JSON.parse(String(writes(rendered.fetch)[0]?.[1]?.body)).topicIds).toEqual(
      available.slice(0, 12).map((item) => item.id),
    );
  }, 10000);
  it('keeps a stable keyboard retry button across loading and success without stealing a moved focus', async () => {
    let reads = 0;
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/content/' + contentId + '/topics', (url) => {
        if (!url.includes('/admin/catalog/topics?')) return undefined;
        reads++;
        return reads === 1 ? json({ code: 'service_unavailable' }, 503) : pending;
      });
    });
    const retry = screen.getByRole('button', { name: 'Reintentar carga de temas' });
    retry.focus();
    await userEvent.keyboard('{Enter}');
    expect(retry).toHaveFocus();
    expect(retry).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(reads).toBe(2);
    const field = screen.getByLabelText('Buscar temas activos');
    await userEvent.click(field);
    await userEvent.type(field, 'Una idea');
    await act(async () => {
      resolve(json(topicDirectory));
    });
    expect(screen.getByRole('button', { name: 'Actualizar temas' })).toBe(retry);
    expect(field).toHaveFocus();
    expect(field).toHaveValue('Una idea');
    expect(screen.getByRole('button', { name: 'Retirar tema ' + topic.name })).toBeVisible();
    expect(writes(rendered.fetch)).toHaveLength(0);
  }, 10000);
  it('aborts the old topic detail and ignores its late response after changing ids', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/topics/' + topicId, (url) =>
        url.endsWith('/topics/' + topicId)
          ? pending
          : url.endsWith('/topics/' + otherTopic.id)
            ? json(otherTopic)
            : undefined,
      );
    });
    const first = rendered.fetch.mock.calls.find(([url]) =>
      String(url).endsWith('/topics/' + topicId),
    );
    expect(first?.[1]?.signal?.aborted).toBe(false);
    await act(async () => {
      await rendered.router.navigate('/admin/topics/' + otherTopic.id);
    });
    expect(screen.getByLabelText('Nombre del tema')).toHaveValue(otherTopic.name);
    expect(first?.[1]?.signal?.aborted).toBe(true);
    await act(async () => {
      resolve(json(topic));
    });
    expect(screen.getByLabelText('Nombre del tema')).toHaveValue(otherTopic.name);
    expect(rendered.router.state.location.pathname).toBe('/admin/topics/' + otherTopic.id);
    expect(writes(rendered.fetch)).toHaveLength(0);
  }, 10000);
});

describe('topic mutation route continuity', () => {
  it('preserves the route chosen while a topic creation request completes later', async () => {
    let resolve: (value: Response) => void = () => {};
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let rendered!: ReturnType<typeof mount>;
    await act(async () => {
      rendered = mount('/admin/topics/new', (url, init) =>
        url.endsWith('/admin/catalog/topics') && init?.method === 'POST' ? pending : undefined,
      );
    });
    fireEvent.change(screen.getByLabelText('Nombre del tema'), {
      target: { value: 'Idea guardada QA' },
    });
    fireEvent.change(screen.getByLabelText('Dirección del tema'), {
      target: { value: 'idea-guardada-qa' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Guardar tema' }));
    await waitFor(() => expect(writes(rendered.fetch)).toHaveLength(1), { timeout: 3000 });
    vi.mocked(window.confirm).mockReturnValue(true);
    await userEvent.click(screen.getByRole('link', { name: 'Volver a Temas' }));
    await screen.findByRole('heading', { name: 'Temas' });
    expect(window.confirm).toHaveBeenCalledWith(
      'Tienes cambios sin guardar. ¿Salir y descartarlos?',
    );
    expect(rendered.router.state.location.pathname).toBe('/admin/topics');
    await act(async () => {
      resolve(json({ ...topic, name: 'Idea guardada QA', slug: 'idea-guardada-qa' }, 201));
    });
    expect(rendered.router.state.location.pathname).toBe('/admin/topics');
    expect(screen.getByRole('heading', { name: 'Temas' })).toBeVisible();
    expect(writes(rendered.fetch)).toHaveLength(1);
  }, 10000);
});
