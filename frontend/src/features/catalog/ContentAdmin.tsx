import { useCallback, useState, type FormEvent } from 'react';
import {
  Link,
  useNavigate,
  useParams,
  useSearchParams,
  type SetURLSearchParams,
} from 'react-router-dom';
import { ApiError } from '../../api/identity';
import {
  catalog,
  catalogMessage,
  categories,
  covers,
  categoryLabel,
  contentQuery,
  type AdminContent,
  type ContentFields,
  type ContentStatus,
  type CoverAsset,
  type CategoryId,
} from '../../api/catalog';
import { CatalogFilters, CatalogState, Pagination } from './Catalog';
import { useCatalog } from './useCatalog';
const statuses: Record<ContentStatus, string> = {
  draft: 'Borrador',
  published: 'Publicado',
  archived: 'Archivado',
};
const coverLabels: Record<CoverAsset, string> = {
  'hero-acropolis': 'Filosofía · ilustración escultórica',
  'editorial-reading': 'Lectura · detalle de un libro',
  'editorial-podcast': 'Podcast · micrófono',
  'editorial-dialogue': 'Diálogo · espacio de conversación',
  'editorial-nature': 'Naturaleza · paisaje ilustrado',
};
export function ContentAdminList() {
  const [params, setParams] = useSearchParams();
  return <ContentAdminListView key={params.toString()} params={params} setParams={setParams} />;
}
function ContentAdminListView({
  params,
  setParams,
}: {
  params: URLSearchParams;
  setParams: SetURLSearchParams;
}) {
  const query = contentQuery(params);
  const initialStatus = ['draft', 'published', 'archived'].includes(params.get('status') ?? '')
    ? (params.get('status') as string)
    : '';
  const [search, setSearch] = useState(query.search ?? '');
  const [category, setCategory] = useState(query.category ?? '');
  const [status, setStatus] = useState(initialStatus);
  const key = params.toString();
  const load = useCallback(
    () => catalog.adminList({ ...contentQuery(new URLSearchParams(key)), status: initialStatus }),
    [key, initialStatus],
  );
  const state = useCatalog(load, key);
  function change(page = 1, applied = false) {
    const next = new URLSearchParams();
    const selectedSearch = applied ? (query.search ?? '') : search.trim();
    const selectedCategory = applied ? (query.category ?? '') : category;
    if (selectedSearch) next.set('search', selectedSearch);
    if (selectedCategory) next.set('category', selectedCategory);
    const selectedStatus = applied ? initialStatus : status;
    if (selectedStatus) next.set('status', selectedStatus);
    if (page > 1) next.set('page', String(page));
    setParams(next);
  }
  return (
    <div className="workspace catalog-admin">
      <div className="catalog-admin-heading">
        <div className="page-heading">
          <p className="eyebrow">ADMINISTRACIÓN EDITORIAL</p>
          <h1>Ideas para compartir.</h1>
          <p className="lead">Crea, revisa y publica el catálogo de Acrópolis Channel.</p>
        </div>
        <Link className="button button-small" to="/admin/content/new">
          Crear contenido <span aria-hidden="true">↗</span>
        </Link>
      </div>
      <section className="surface">
        <CatalogFilters
          search={search}
          setSearch={setSearch}
          category={category}
          setCategory={setCategory}
          onSubmit={() => change()}
        >
          <div className="field">
            <label htmlFor="content-status">Estado</label>
            <select
              id="content-status"
              value={status}
              onChange={(event) => setStatus(event.target.value)}
            >
              <option value="">Todos los estados</option>
              {Object.entries(statuses).map(([id, label]) => (
                <option value={id} key={id}>
                  {label}
                </option>
              ))}
            </select>
          </div>
        </CatalogFilters>
        <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
          {state.data && (
            <>
              <p className="results-count" role="status">
                {state.data.total} {state.data.total === 1 ? 'contenido' : 'contenidos'}
              </p>
              {state.data.items.length ? (
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Contenido</th>
                        <th>Categoría</th>
                        <th>Estado</th>
                        <th>
                          <span className="sr-only">Acciones</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {state.data.items.map((item) => (
                        <tr key={item.id}>
                          <td>
                            <strong>{item.title}</strong>
                            <span className="table-email">/{item.slug}</span>
                          </td>
                          <td>{categoryLabel(item.category)}</td>
                          <td>
                            <span className={'status-badge catalog-status-' + item.status}>
                              {statuses[item.status]}
                            </span>
                          </td>
                          <td>
                            <Link
                              className="text-link"
                              to={'/admin/content/' + encodeURIComponent(item.id)}
                              aria-label={'Editar contenido ' + item.title}
                            >
                              Editar ↗
                            </Link>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <div className="catalog-empty">
                  <h2>No hay contenidos con estos filtros.</h2>
                  <p>
                    Los nuevos contenidos se crean como borrador y sólo aparecen en el catálogo al
                    publicarlos.
                  </p>
                </div>
              )}
              <Pagination
                page={state.data.page}
                pageSize={state.data.pageSize}
                total={state.data.total}
                change={(page) => change(page, true)}
              />
            </>
          )}
        </CatalogState>
      </section>
    </div>
  );
}
export function ContentEditor() {
  const { id } = useParams();
  return id ? <ExistingEditor key={id} id={id} /> : <ContentEditorForm key="new" />;
}
function ExistingEditor({ id }: { id: string }) {
  const load = useCallback(() => catalog.adminDetail(id), [id]);
  const state = useCatalog(load, id);
  return (
    <div className="workspace catalog-editor">
      {!state.data && <h1>Editar contenido</h1>}
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <ContentEditorForm
            key={state.data.id + state.data.version}
            item={state.data}
            reload={state.reload}
          />
        )}
      </CatalogState>
    </div>
  );
}
function ContentEditorForm({ item, reload }: { item?: AdminContent; reload?: () => void }) {
  const navigate = useNavigate();
  const [current, setCurrent] = useState(item);
  const [fields, setFields] = useState<ContentFields>({
    title: item?.title ?? '',
    slug: item?.slug ?? '',
    category: item?.category ?? 'lecturas',
    summary: item?.summary ?? '',
    body: item?.body ?? '',
    coverAsset: item?.coverAsset ?? null,
    durationSeconds: item?.durationSeconds ?? null,
  });
  const [duration, setDuration] = useState(item?.durationSeconds?.toString() ?? '');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState('');
  const [pendingStatus, setPendingStatus] = useState<ContentStatus | null>(null);
  function update(name: keyof ContentFields, value: string | null) {
    setFields((previous) => ({ ...previous, [name]: value }));
    setSaved('');
  }
  async function save(status: ContentStatus) {
    if (busy || conflict) return;
    const next: Record<string, string> = {};
    const body: ContentFields = {
      ...fields,
      title: fields.title.trim(),
      slug: fields.slug.trim(),
      summary: fields.summary.trim(),
      body: fields.body.trim(),
      durationSeconds: duration ? Number(duration) : null,
    };
    if (body.title.length < 2 || body.title.length > 180)
      next['title'] = 'Usa entre 2 y 180 caracteres.';
    if (
      body.slug.length < 2 ||
      body.slug.length > 160 ||
      !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(body.slug)
    )
      next['slug'] = 'Usa entre 2 y 160 letras minúsculas, números y guiones entre palabras.';
    if (body.summary.length > 600 || (status === 'published' && !body.summary))
      next['summary'] =
        status === 'published'
          ? 'Para publicar, añade un resumen de hasta 600 caracteres.'
          : 'Usa hasta 600 caracteres.';
    if (body.body.length > 50000 || (status === 'published' && !body.body))
      next['body'] =
        status === 'published'
          ? 'Para publicar, añade una sinopsis editorial de hasta 50.000 caracteres.'
          : 'Usa hasta 50.000 caracteres.';
    if (duration && (!/^\d+$/.test(duration) || Number(duration) < 1 || Number(duration) > 86400))
      next['durationSeconds'] = 'Usa un número entero entre 1 y 86.400 segundos.';
    setErrors(next);
    setError('');
    setSaved('');
    setPendingStatus(null);
    if (Object.keys(next).length) {
      document.getElementById('editor-' + Object.keys(next)[0])?.focus();
      return;
    }
    setBusy(true);
    try {
      const updated = current
        ? await catalog.update(current.id, body, status, current.version)
        : await catalog.create(body);
      setCurrent(updated);
      setFields({
        title: updated.title,
        slug: updated.slug,
        summary: updated.summary,
        body: updated.body,
        category: updated.category,
        coverAsset: updated.coverAsset,
        durationSeconds: updated.durationSeconds,
      });
      setDuration(updated.durationSeconds?.toString() ?? '');
      setSaved(
        updated.status === 'published'
          ? 'Contenido publicado.'
          : updated.status === 'archived'
            ? 'Contenido archivado.'
            : 'Borrador guardado.',
      );
      if (!current) navigate('/admin/content/' + encodeURIComponent(updated.id), { replace: true });
    } catch (failure) {
      setError(catalogMessage(failure));
      if (failure instanceof ApiError) {
        setErrors(
          Object.fromEntries(
            Object.keys(failure.fields).map((field) => [
              field.charAt(0).toLowerCase() + field.slice(1),
              'Revisa el valor de este campo.',
            ]),
          ),
        );
        setConflict(failure.code === 'concurrency_conflict');
      }
    } finally {
      setBusy(false);
    }
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    void save(current?.status ?? 'draft');
  }
  function feedback(name: string, help?: string) {
    return (
      (errors[name] || help) && (
        <p id={'editor-' + name + '-help'} className={errors[name] ? 'field-error' : 'field-help'}>
          {errors[name] ?? help}
        </p>
      )
    );
  }
  function described(name: string, help = false) {
    return errors[name] || help ? 'editor-' + name + '-help' : undefined;
  }
  const form = (
    <>
      <Link className="text-link back-link" to="/admin/content">
        ← Gestionar contenidos
      </Link>
      <div className="page-heading">
        <p className="eyebrow">ADMINISTRACIÓN EDITORIAL · {statuses[current?.status ?? 'draft']}</p>
        <h1>{current ? 'Dar forma a una idea.' : 'Una nueva idea.'}</h1>
        <p className="lead">
          Prepara una ficha clara, útil y cuidada. Sólo los contenidos publicados son públicos.
        </p>
      </div>
      <form className="surface content-editor-form" onSubmit={submit} noValidate aria-busy={busy}>
        {error && (
          <div className="form-alert" role="alert">
            {error}
          </div>
        )}
        {saved && (
          <p className="success-message" role="status">
            {saved}
          </p>
        )}
        {conflict && (
          <div className="editor-conflict">
            <p>Tus cambios siguen aquí. Recargar los sustituirá por la versión más reciente.</p>
            <button
              className="button button-outline button-small"
              type="button"
              onClick={() => setPendingStatus('draft')}
            >
              Recargar datos
            </button>
          </div>
        )}
        {conflict && pendingStatus === 'draft' && (
          <div className="editor-confirmation" role="alert">
            <p>¿Recargar y sustituir tus cambios sin guardar?</p>
            <button className="button button-small" type="button" onClick={reload}>
              Recargar y sustituir cambios
            </button>
            <button className="link-button" type="button" onClick={() => setPendingStatus(null)}>
              Conservar mis cambios
            </button>
          </div>
        )}
        <div className="editor-grid">
          <div className="field editor-wide">
            <label htmlFor="editor-title">Título</label>
            <input
              id="editor-title"
              maxLength={180}
              value={fields.title}
              onChange={(event) => update('title', event.target.value)}
              aria-invalid={Boolean(errors['title'])}
              aria-describedby={described('title')}
              disabled={busy}
            />
            {feedback('title')}
          </div>
          <div className="field">
            <label htmlFor="editor-slug">Dirección del contenido</label>
            <input
              id="editor-slug"
              maxLength={160}
              value={fields.slug}
              onChange={(event) => update('slug', event.target.value)}
              readOnly={Boolean(current?.publishedUtc)}
              aria-invalid={Boolean(errors['slug'])}
              aria-describedby={described('slug', true)}
              disabled={busy}
            />
            {feedback(
              'slug',
              current?.publishedUtc
                ? 'La dirección se conserva después de la primera publicación.'
                : 'Letras minúsculas, números y guiones. Ejemplo: filosofia-en-la-vida.',
            )}
          </div>
          <div className="field">
            <label htmlFor="editor-category">Categoría del contenido</label>
            <select
              id="editor-category"
              aria-invalid={Boolean(errors['category'])}
              aria-describedby={described('category')}
              value={fields.category}
              onChange={(event) => update('category', event.target.value as CategoryId)}
              disabled={busy}
            >
              {categories.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.label}
                </option>
              ))}
            </select>
            {feedback('category')}
          </div>
          <div className="field">
            <label htmlFor="editor-cover">Imagen editorial</label>
            <select
              id="editor-cover"
              aria-invalid={Boolean(errors['coverAsset'])}
              aria-describedby={described('coverAsset')}
              value={fields.coverAsset ?? ''}
              onChange={(event) => update('coverAsset', event.target.value || null)}
              disabled={busy}
            >
              <option value="">Sin imagen</option>
              {covers.map((cover) => (
                <option key={cover} value={cover}>
                  {coverLabels[cover]}
                </option>
              ))}
            </select>
            {feedback('coverAsset')}
          </div>
          <div className="field">
            <label htmlFor="editor-durationSeconds">Duración (segundos)</label>
            <input
              id="editor-durationSeconds"
              type="number"
              min={1}
              max={86400}
              step={1}
              value={duration}
              onChange={(event) => {
                setDuration(event.target.value);
                setSaved('');
              }}
              aria-invalid={Boolean(errors['durationSeconds'])}
              aria-describedby={described('durationSeconds', true)}
              disabled={busy}
            />
            {feedback('durationSeconds', 'Opcional. Completa sólo si conoces la duración real.')}
          </div>
          <div className="field editor-wide">
            <label htmlFor="editor-summary">Resumen</label>
            <textarea
              id="editor-summary"
              maxLength={600}
              rows={3}
              value={fields.summary}
              onChange={(event) => update('summary', event.target.value)}
              aria-invalid={Boolean(errors['summary'])}
              aria-describedby={described('summary', true)}
              disabled={busy}
            />
            {feedback('summary', 'Hasta 600 caracteres. Se muestra en tarjetas y fichas públicas.')}
          </div>
          <div className="field editor-wide">
            <label htmlFor="editor-body">Sinopsis ampliada</label>
            <textarea
              id="editor-body"
              maxLength={50000}
              rows={10}
              value={fields.body}
              onChange={(event) => update('body', event.target.value)}
              aria-invalid={Boolean(errors['body'])}
              aria-describedby={described('body', true)}
              disabled={busy}
            />
            {feedback(
              'body',
              'Texto editorial público, sin HTML. No incluyas lecturas completas ni material restringido.',
            )}
          </div>
        </div>
        {fields.coverAsset && (
          <div className="editor-cover-review">
            <img
              src={'/images/' + fields.coverAsset + '.webp'}
              width="1672"
              height="941"
              alt="Vista de la imagen editorial seleccionada"
              loading="lazy"
            />
            <p>Ilustración editorial del proyecto.</p>
          </div>
        )}
        <div className="editor-actions">
          <button className="button" disabled={busy || conflict} type="submit">
            {busy ? 'Guardando…' : current ? 'Guardar cambios' : 'Guardar borrador'}
          </button>
          {current && !conflict && (
            <>
              {current.status === 'draft' && (
                <button
                  className="button button-outline"
                  type="button"
                  disabled={busy}
                  onClick={() => setPendingStatus('published')}
                >
                  Publicar contenido
                </button>
              )}
              {current.status === 'published' && (
                <>
                  <button
                    className="button button-outline"
                    type="button"
                    disabled={busy}
                    onClick={() => setPendingStatus('draft')}
                  >
                    Retirar publicación
                  </button>
                  <button
                    className="link-button"
                    type="button"
                    disabled={busy}
                    onClick={() => setPendingStatus('archived')}
                  >
                    Archivar contenido
                  </button>
                </>
              )}
              {current.status === 'archived' && (
                <button
                  className="button button-outline"
                  type="button"
                  disabled={busy}
                  onClick={() => setPendingStatus('draft')}
                >
                  Volver a borrador
                </button>
              )}
            </>
          )}
        </div>
        {!conflict && pendingStatus && (
          <div className="editor-confirmation" role="alert">
            <p>
              {pendingStatus === 'published'
                ? 'El resumen y la sinopsis serán visibles para todas las personas.'
                : 'El contenido dejará de aparecer en el catálogo público.'}
            </p>
            <button
              className="button button-small"
              type="button"
              disabled={busy}
              onClick={() => void save(pendingStatus)}
            >
              {pendingStatus === 'published'
                ? 'Confirmar publicación'
                : pendingStatus === 'archived'
                  ? 'Confirmar archivo'
                  : 'Confirmar borrador'}
            </button>
            <button className="link-button" type="button" onClick={() => setPendingStatus(null)}>
              Cancelar
            </button>
          </div>
        )}
        {current?.status === 'published' && (
          <Link className="text-link editor-public-link" to={'/content/' + current.slug}>
            Ver ficha pública ↗
          </Link>
        )}
      </form>
    </>
  );
  return item ? form : <div className="workspace catalog-editor">{form}</div>;
}
