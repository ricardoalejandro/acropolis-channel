import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { clearCsrf } from '../../api/identity';
import { admin, user, json } from '../../test/fixtures';
import { draftContent, publishedContent } from '../../test/catalogFixtures';
type Override = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function mount(path: string, permission = true, override?: Override) {
  let item = { ...publishedContent };
  window.history.replaceState({}, '', path);
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const changed = override?.(url, init);
    if (changed) return changed;
    if (url.endsWith('/identity/me'))
      return json(permission ? { ...admin, permissions: ['Content.Manage'] } : user);
    if (url.endsWith('/csrf')) return json({ token: 'csrf' });
    if (url.endsWith('/subscriptions/me'))
      return json({ subscription: null, eligibleToActivate: true });
    if (url.includes('/consumption/content/')) return json({ code: 'subscription_required' }, 403);
    if (url.includes('/content?'))
      return json({
        items: [item],
        total: 21,
        page: Number(new URL(url, 'https://test.test').searchParams.get('page')),
        pageSize: 20,
      });
    if (url === '/api/v1/admin/content' && init?.method === 'POST') {
      item = { ...draftContent, ...JSON.parse(String(init.body)), id: 'new-content' };
      return json(item, 201);
    }
    if (url.includes('/admin/content/')) {
      if (init?.method === 'PUT') {
        const fields = JSON.parse(String(init.body));
        item = {
          ...item,
          ...fields,
          publishedUtc:
            item.publishedUtc ??
            (fields.status === 'published' ? publishedContent.publishedUtc : null),
          version: 'updated-version',
        };
      }
      return json(item);
    }
    if (url.includes('/catalog/content/')) return json(item);
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetch);
  renderApp();
  return fetch;
}
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value } });
}
function draft(path: string, init?: RequestInit) {
  return path.includes('/admin/content/') && init?.method === 'GET'
    ? json(draftContent)
    : undefined;
}
beforeEach(() => {
  clearCsrf();
  vi.stubGlobal('scrollTo', vi.fn());
});
describe('Published catalogue journeys', () => {
  it('shows all official categories and server titles on the home without fixture copy', async () => {
    mount('/');
    const main = within(document.querySelector('main')!);
    const title = await main.findByText(publishedContent.title, { selector: 'h3' });
    expect(title).toHaveAccessibleName(publishedContent.title);
    const categories = within(document.querySelector('.catalog-categories')!);
    const links = categories.getAllByRole('link');
    const names = ['Lecturas', 'Documentales', 'Videos', 'Podcast', 'Charlas online', 'Cursos'];
    expect(links).toHaveLength(names.length);
    for (const [index, name] of names.entries()) {
      expect(links[index]).toBeInTheDocument();
      expect(links[index]).toHaveAccessibleName(name);
    }
    expect(screen.queryByText('Contenido de demostración')).not.toBeInTheDocument();
    expect(main.queryByRole('button', { name: /Comprar|Reproducir/ })).not.toBeInTheDocument();
  }, 10000);
  it('shows an honest empty home and a retriable API failure', async () => {
    let failed = true;
    mount('/', true, (path) =>
      path.includes('/catalog/content?')
        ? failed
          ? json({}, 503)
          : json({ items: [], total: 0, page: 1, pageSize: 3 })
        : undefined,
    );
    await screen.findByRole('alert');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByRole('heading', { name: 'El catálogo está por comenzar.' });
  }, 10000);
  it('loads and persists search, category and pagination with browser navigation', async () => {
    const fetch = mount('/explore?category=videos&search=filosofia');
    expect(screen.getByRole('status')).toHaveTextContent('Cargando contenidos');
    await screen.findByRole('heading', { name: publishedContent.title });
    expect(screen.getByLabelText('Categoría')).toHaveValue('videos');
    fill('Buscar contenido', 'otra idea');
    fireEvent.change(screen.getByLabelText('Categoría'), { target: { value: 'podcast' } });
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await screen.findByRole('heading', { name: publishedContent.title });
    expect(window.location.search).toContain('search=otra+idea&category=podcast');
    fill('Buscar contenido', 'búsqueda sin aplicar');
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }));
    await screen.findByText('Página 2');
    expect(window.location.search).toContain('search=otra+idea');
    expect(screen.getByLabelText('Buscar contenido')).toHaveValue('otra idea');
    await userEvent.click(screen.getByRole('button', { name: 'Anterior' }));
    await screen.findByText('Página 1');
    expect(
      fetch.mock.calls.some(([path]) => String(path).includes('category=podcast&page=2')),
    ).toBe(true);
    window.history.replaceState({}, '', '/explore?category=lecturas&search=lectura');
    fireEvent(window, new PopStateEvent('popstate'));
    await screen.findByDisplayValue('lectura');
    expect(screen.getByLabelText('Categoría')).toHaveValue('lecturas');
  }, 10000);
  it('shows empty filtered results and an empty catalogue without invented titles', async () => {
    mount('/explore?search=no', true, (path) =>
      path.includes('/content?') ? json({ items: [], total: 0, page: 1, pageSize: 20 }) : undefined,
    );
    await screen.findByRole('heading', { name: 'No encontramos coincidencias.' });
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled();
    fill('Buscar contenido', '');
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await screen.findByRole('heading', { name: 'El catálogo está por comenzar.' });
  });
  it('escapes public synopsis HTML and requires access before loading restricted media', async () => {
    mount('/content/filosofia-vida', true, (path) =>
      path.includes('/catalog/content/')
        ? json({
            ...publishedContent,
            body: '<script>window.secret = true</script>',
            durationSeconds: null,
            coverAsset: null,
          })
        : undefined,
    );
    await screen.findByRole('heading', { name: publishedContent.title, level: 1 });
    expect(screen.getByText('<script>window.secret = true</script>')).toBeInTheDocument();
    expect(document.querySelector('main script')).toBeNull();
    await screen.findByRole('heading', { name: 'Revisa tu suscripción.' });
    expect(screen.getByRole('link', { name: 'Ver mi suscripción' })).toHaveAttribute(
      'href',
      '/profile/subscription?content=filosofia-vida',
    );
    expect(document.querySelector('video, audio, iframe')).toBeNull();
    expect(
      screen.queryByRole('button', { name: /Reproducir|Comprar|Suscribirme/ }),
    ).not.toBeInTheDocument();
  });
  it('renders a real cover and duration, and safely retries a missing content response', async () => {
    let missing = true;
    mount('/content/filosofia-vida', true, (path) =>
      path.includes('/catalog/content/') && missing
        ? json({ code: 'not_found', detail: 'secret' }, 404)
        : undefined,
    );
    await screen.findByRole('alert');
    expect(screen.queryByText('secret')).not.toBeInTheDocument();
    missing = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByRole('heading', { name: publishedContent.title, level: 1 });
    expect(screen.getByText('2 min 25 s')).toBeInTheDocument();
    expect(document.querySelector('.catalog-detail-cover')).toHaveAttribute(
      'src',
      '/images/editorial-dialogue.webp',
    );
  });
  it('does not grant editorial administration to institutional levels or Users.Manage', async () => {
    mount('/admin/content', false);
    await screen.findByRole('heading', { name: 'Este espacio requiere autorización.' });
    expect(screen.queryByRole('link', { name: /Gestionar contenidos/ })).not.toBeInTheDocument();
  });
  it('renders a neutral cover and omits absent metadata on a real card', async () => {
    mount('/explore', true, (path) =>
      path.includes('/content?')
        ? json({
            items: [{ ...publishedContent, coverAsset: null, durationSeconds: null, summary: '' }],
            total: 1,
            page: 1,
            pageSize: 20,
          })
        : undefined,
    );
    await screen.findByRole('heading', { name: publishedContent.title });
    const headings = within(document.querySelector('main')!).getAllByRole('heading');
    expect(headings.map((heading) => heading.tagName)).toEqual(['H1', 'H2', 'H3']);
    expect(headings[1]).toHaveAccessibleName('Resultados de la búsqueda');
    expect(document.querySelector('.catalog-no-image')).toBeInTheDocument();
    expect(document.querySelector('.catalog-duration')).toBeNull();
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled();
  });
});
describe('Editorial administration and concurrency', () => {
  it('lists, filters, and paginates without fetching bodies for every row', async () => {
    const fetch = mount('/admin/content');
    await screen.findByRole(
      'link',
      { name: 'Editar contenido ' + publishedContent.title },
      { timeout: 3000 },
    );
    fill('Buscar contenido', 'filosofia');
    fireEvent.change(screen.getByLabelText('Estado'), { target: { value: 'published' } });
    fireEvent.change(screen.getByLabelText('Categoría'), { target: { value: 'videos' } });
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await screen.findByRole(
      'link',
      { name: 'Editar contenido ' + publishedContent.title },
      { timeout: 3000 },
    );
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }));
    await screen.findByText('Página 2');
    expect(fetch.mock.calls.some(([path]) => String(path).includes('status=published'))).toBe(true);
    expect(screen.getByRole('link', { name: /Crear contenido/ })).toHaveAttribute(
      'href',
      '/admin/content/new',
    );
  }, 10000);
  it('shows administrative empty and error states with explicit retry', async () => {
    let failed = true;
    mount('/admin/content', true, (path) =>
      path.includes('/admin/content?')
        ? failed
          ? json({}, 503)
          : json({ items: [], total: 0, page: 1, pageSize: 20 })
        : undefined,
    );
    await screen.findByRole('alert');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }));
    await screen.findByRole('heading', { name: 'No hay contenidos con estos filtros.' });
  });
  it('creates only a draft with allowed metadata and no publishing request', async () => {
    const fetch = mount('/admin/content/new');
    await screen.findByLabelText('Título');
    expect(
      screen.getByRole('checkbox', { name: 'Disponible con el plan Gratuito' }),
    ).not.toBeChecked();
    fill('Título', 'Una nueva idea');
    fill('Enlace de la página en Acrópolis', 'una-nueva-idea');
    fireEvent.change(screen.getByLabelText('Categoría del contenido'), {
      target: { value: 'podcast' },
    });
    fireEvent.change(screen.getByLabelText('Imagen editorial'), {
      target: { value: 'editorial-podcast' },
    });
    fill('Duración (segundos)', '120');
    fill('Resumen', 'Resumen editorial');
    fill('Sinopsis ampliada', 'Sinopsis editorial');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    await screen.findByRole('heading', { name: 'Editar contenido' });
    expect(window.location.pathname).toBe('/admin/content/new-content');
    const call = fetch.mock.calls.find(
      ([path, options]) => String(path) === '/api/v1/admin/content' && options?.method === 'POST',
    );
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({
      title: 'Una nueva idea',
      slug: 'una-nueva-idea',
      category: 'podcast',
      isFree: false,
      coverAsset: 'editorial-podcast',
      durationSeconds: 120,
      summary: 'Resumen editorial',
      body: 'Sinopsis editorial',
      author: null,
      tags: [],
      workText: null,
      youTubeId: null,
      collectionKind: null,
      itemIds: [],
    });
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'PUT')).toBe(false);
  });
  it('generates the page link from a Spanish title and submits it without an extra required edit', async () => {
    const fetch = mount('/admin/content/new');
    await screen.findByLabelText('Título');
    fill('Título', '  ¿Filosofía, acción y niñez?  ');
    const slug = screen.getByLabelText('Enlace de la página en Acrópolis');
    expect(slug).toHaveValue('filosofia-accion-y-ninez');
    expect(slug).toHaveAccessibleDescription(/no el enlace de YouTube/);
    fill('Título', 'Filosofía para jóvenes');
    expect(slug).toHaveValue('filosofia-para-jovenes');
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    await screen.findByRole('heading', { name: 'Editar contenido' });
    const call = fetch.mock.calls.find(([, options]) => options?.method === 'POST');
    expect(JSON.parse(String(call?.[1]?.body))).toMatchObject({
      title: 'Filosofía para jóvenes',
      slug: 'filosofia-para-jovenes',
    });
  });
  it('keeps manual page-link input while composing and normalizes only after leaving the field', async () => {
    mount('/admin/content/new');
    await screen.findByLabelText('Título');
    fill('Título', 'Una nueva idea');
    const slug = screen.getByLabelText('Enlace de la página en Acrópolis');
    expect(slug).toHaveAttribute('autocapitalize', 'none');
    expect(slug).toHaveAttribute('spellcheck', 'false');
    fireEvent.focus(slug);
    fireEvent.compositionStart(slug);
    fireEvent.change(slug, { target: { value: 'Mi Dirección Elegida' } });
    expect(slug).toHaveValue('Mi Dirección Elegida');
    fireEvent.blur(slug);
    expect(slug).toHaveValue('Mi Dirección Elegida');
    fireEvent.compositionEnd(slug);
    fireEvent.focus(slug);
    fireEvent.blur(slug);
    expect(slug).toHaveValue('mi-direccion-elegida');
    fill('Título', 'Otro título');
    expect(slug).toHaveValue('mi-direccion-elegida');
    fill('Enlace de la página en Acrópolis', '');
    fill('Título', 'No sustituir mi edición');
    expect(slug).toHaveValue('');
  });
  it('normalizes a manual page link on keyboard submission without rewriting it while typing', async () => {
    const fetch = mount('/admin/content/new');
    const title = await screen.findByLabelText('Título');
    fill('Título', 'Mi nueva ficha');
    fill('Enlace de la página en Acrópolis', 'Youtube123artejsjskks');
    expect(screen.getByLabelText('Enlace de la página en Acrópolis')).toHaveValue(
      'Youtube123artejsjskks',
    );
    fireEvent.submit(title.closest('form')!);
    await screen.findByRole('heading', { name: 'Editar contenido' });
    const call = fetch.mock.calls.find(([, options]) => options?.method === 'POST');
    expect(JSON.parse(String(call?.[1]?.body))).toMatchObject({ slug: 'youtube123artejsjskks' });
  });
  it('explains a YouTube URL pasted in the page link instead of creating a misleading address', async () => {
    const fetch = mount('/admin/content/new');
    await screen.findByLabelText('Título');
    fill('Título', 'Vídeo de filosofía');
    const slug = screen.getByLabelText('Enlace de la página en Acrópolis');
    fill('Enlace de la página en Acrópolis', 'https://www.youtube.com/watch?v=M7lc1UVf-VE');
    fireEvent.blur(slug);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    expect(slug).toHaveFocus();
    expect(slug).toHaveValue('https://www.youtube.com/watch?v=M7lc1UVf-VE');
    expect(slug).toHaveAccessibleDescription(/Pega el vídeo en el campo de YouTube/);
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
  });
  it('retains an existing draft page link when its title changes', async () => {
    const fetch = mount('/admin/content/content-one', true, draft);
    await screen.findByLabelText('Título');
    fill('Título', 'Título revisado del borrador');
    expect(screen.getByLabelText('Enlace de la página en Acrópolis')).toHaveValue(
      draftContent.slug,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Borrador guardado.');
    const call = fetch.mock.calls.find(([, options]) => options?.method === 'PUT');
    expect(JSON.parse(String(call?.[1]?.body))).toMatchObject({
      title: 'Título revisado del borrador',
      slug: draftContent.slug,
      version: draftContent.version,
    });
  });
  it('validates drafts and publication requirements before any mutation', async () => {
    const fetch = mount('/admin/content/new');
    await screen.findByLabelText('Título');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar borrador' }));
    expect(screen.getByLabelText('Título')).toHaveFocus();
    expect(screen.getByText(/Usa entre 2 y 160/)).toBeInTheDocument();
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
  });
  it('requires synopsis and summary for publication and clears cancelled confirmation', async () => {
    let fetch!: ReturnType<typeof mount>;
    await act(async () => {
      fetch = mount('/admin/content/content-one', true, draft);
    });
    const content = within(document.querySelector('main')!);
    const titleField = await content.findByLabelText('Título');
    const editorForm = titleField.closest('form');
    if (!editorForm) throw new Error('Content editor form is missing.');
    const fields = within(editorForm);
    const publishContent = fields.getByRole('button', { name: 'Publicar contenido' });
    const summaryField = fields.getByLabelText('Resumen');
    const synopsisField = fields.getByLabelText('Sinopsis ampliada');
    await userEvent.click(publishContent);
    const firstConfirmation = within(fields.getByRole('alert'));
    await userEvent.click(firstConfirmation.getByRole('button', { name: 'Cancelar' }));
    expect(screen.queryByRole('button', { name: 'Confirmar publicación' })).not.toBeInTheDocument();
    await userEvent.click(publishContent);
    const nextConfirmation = within(fields.getByRole('alert'));
    await userEvent.click(nextConfirmation.getByRole('button', { name: 'Confirmar publicación' }));
    expect(summaryField).toHaveAttribute('aria-invalid', 'true');
    expect(synopsisField).toHaveAttribute('aria-invalid', 'true');
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'PUT')).toBe(false);
  });
  it('publishes with the loaded version and freezes the first published slug', async () => {
    const fetch = mount('/admin/content/content-one', true, draft);
    await screen.findByLabelText('Título');
    fill('Resumen', 'Resumen real');
    fill('Sinopsis ampliada', 'Sinopsis editorial pública');
    await userEvent.click(screen.getByRole('button', { name: 'Publicar contenido' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar publicación' }));
    await screen.findByText('Contenido publicado.');
    const call = fetch.mock.calls.find(([, options]) => options?.method === 'PUT');
    expect(JSON.parse(String(call?.[1]?.body))).toMatchObject({
      status: 'published',
      version: 'version-one',
    });
    expect(screen.getByLabelText('Enlace de la página en Acrópolis')).toHaveAttribute('readonly');
    fill('Título', 'Un título nuevo después de publicar');
    expect(screen.getByLabelText('Enlace de la página en Acrópolis')).toHaveValue(
      publishedContent.slug,
    );
    expect(screen.getByRole('link', { name: /Ver ficha pública/ })).toHaveAttribute(
      'href',
      '/content/filosofia-vida',
    );
  });
  it('retains fields on conflict and requires confirmation before discarding them', async () => {
    mount('/admin/content/content-one', true, (path, options) =>
      path.includes('/admin/content/') && options?.method === 'PUT'
        ? json({ code: 'concurrency_conflict', detail: 'private' }, 409)
        : undefined,
    );
    await screen.findByLabelText('Título');
    fill('Título', 'Mis cambios sin guardar');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText(/Conservamos tus cambios/);
    expect(screen.getByLabelText('Título')).toHaveValue('Mis cambios sin guardar');
    expect(screen.getByRole('button', { name: 'Guardar cambios' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Recargar datos' }));
    await userEvent.click(screen.getByRole('button', { name: 'Conservar mis cambios' }));
    expect(screen.getByLabelText('Título')).toHaveValue('Mis cambios sin guardar');
    await userEvent.click(screen.getByRole('button', { name: 'Recargar datos' }));
    await userEvent.click(screen.getByRole('button', { name: 'Recargar y sustituir cambios' }));
    await screen.findByDisplayValue(publishedContent.title);
    expect(screen.queryByText('private')).not.toBeInTheDocument();
  }, 10000);
  it('maps field errors without displaying server detail and permits an explicit retry', async () => {
    let rejected = true;
    mount('/admin/content/content-one', true, (path, options) =>
      path.includes('/admin/content/') && options?.method === 'PUT' && rejected
        ? json({ code: 'slug_conflict', fieldErrors: { Slug: ['secret'] } }, 409)
        : undefined,
    );
    await screen.findByLabelText('Título');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText(/otro contenido/);
    expect(screen.getByLabelText('Enlace de la página en Acrópolis')).toHaveAccessibleDescription(
      'Revisa el valor de este campo.',
    );
    rejected = false;
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }));
    await screen.findByText('Contenido publicado.');
  }, 10000);
  it('withdraws, archives and restores to draft using explicit transitions without deletion', async () => {
    let fetch!: ReturnType<typeof mount>;
    await act(async () => {
      fetch = mount('/admin/content/content-one');
    });
    await screen.findByLabelText('Título');
    await userEvent.click(screen.getByRole('button', { name: 'Archivar contenido' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar archivo' }));
    await screen.findByText('Contenido archivado.');
    expect(screen.queryByRole('button', { name: 'Publicar contenido' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Volver a borrador' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar borrador' }));
    await screen.findByText('Borrador guardado.');
    await userEvent.click(screen.getByRole('button', { name: 'Publicar contenido' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar publicación' }));
    await screen.findByText('Contenido publicado.');
    await userEvent.click(screen.getByRole('button', { name: 'Retirar publicación' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar borrador' }));
    await screen.findByText('Borrador guardado.');
    expect(fetch.mock.calls.filter(([, options]) => options?.method === 'PUT')).toHaveLength(4);
    expect(fetch.mock.calls.some(([, options]) => options?.method === 'DELETE')).toBe(false);
    for (const [, options] of fetch.mock.calls.filter(([, options]) => options?.method === 'PUT')) {
      expect(Object.keys(JSON.parse(String(options?.body))).sort()).toEqual([
        'author',
        'body',
        'category',
        'collectionKind',
        'coverAsset',
        'durationSeconds',
        'isFree',
        'itemIds',
        'slug',
        'status',
        'summary',
        'tags',
        'title',
        'version',
        'workText',
        'youTubeId',
      ]);
      expect(JSON.parse(String(options?.body))).toMatchObject({ isFree: true });
    }
  }, 10000);
});
