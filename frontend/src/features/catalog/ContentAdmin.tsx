import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import {
  Link,
  useNavigate,
  useParams,
  useSearchParams,
  type SetURLSearchParams,
} from 'react-router-dom';
import { hasControlCharacters } from '../../validation/plainText';
import {
  MAX_YOUTUBE_REFERENCE_LENGTH,
  normalizeYouTubeReference,
} from '../../validation/youTubeReference';
import { CollectionEditor } from './CollectionEditor';
import { useUnsavedChanges } from '../../components/useUnsavedChanges';
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
          <p className="eyebrow">Administración editorial</p>
          <h1>Contenidos</h1>
          <p className="lead">Crea, revisa y publica el catálogo de Acrópolis Channel.</p>
        </div>
        <Link className="button button-small" to="/admin/content/new">
          Crear contenido
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
                              Editar
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
    author: item?.author ?? null,
    tags: item?.tags ?? [],
    workText: item?.workText ?? null,
    youTubeId: item?.youTubeId ?? null,
    collectionKind: item?.collectionKind ?? null,
    itemIds: item?.itemIds ?? [],
  });
  const [tagsText, setTagsText] = useState(item?.tags?.join(', ') ?? '');
  const [baseline, setBaseline] = useState(
    JSON.stringify({
      fields,
      duration: item?.durationSeconds?.toString() ?? '',
      tagsText: item?.tags?.join(', ') ?? '',
    }),
  );
  const [duration, setDuration] = useState(item?.durationSeconds?.toString() ?? '');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState('');
  const [pendingStatus, setPendingStatus] = useState<ContentStatus | null>(null);
  const permitNavigation = useUnsavedChanges(
    JSON.stringify({ fields, duration, tagsText }) !== baseline,
  );
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  function update(name: keyof ContentFields, value: string | null) {
    setFields((previous) => ({ ...previous, [name]: value }));
    setSaved('');
  }
  async function save(status: ContentStatus) {
    if (busy || conflict) return;
    const next: Record<string, string> = {};
    const audiovisual = ['documentales', 'videos', 'podcast', 'charlas-online'].includes(
      fields.category,
    );
    const youTubeInput = fields.youTubeId ?? '';
    const youTubeId = normalizeYouTubeReference(youTubeInput);
    const body: ContentFields = {
      ...fields,
      title: fields.title.trim(),
      slug: fields.slug.trim(),
      summary: fields.summary.trim(),
      body: fields.body.trim(),
      durationSeconds: duration ? Number(duration) : null,
      author: fields.author?.trim() || null,
      tags: tagsText
        ? tagsText
            .split(',')
            .map((tag) => tag.trim())
            .filter(Boolean)
        : [],
      workText: fields.category === 'lecturas' ? fields.workText?.trim() || null : null,
      youTubeId: audiovisual ? youTubeId : null,
      collectionKind: fields.category === 'cursos' ? (fields.collectionKind ?? null) : null,
      itemIds: fields.category === 'cursos' && fields.collectionKind ? (fields.itemIds ?? []) : [],
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
    if (body.author && (body.author.length > 180 || hasControlCharacters(body.author)))
      next['author'] = 'Usa hasta 180 caracteres sin controles.';
    if (
      (body.tags?.length ?? 0) > 12 ||
      body.tags?.some((tag) => tag.length > 40 || hasControlCharacters(tag)) ||
      new Set(body.tags?.map((tag) => tag.toLocaleLowerCase())).size !== body.tags?.length
    )
      next['tags'] =
        'Usa hasta 12 etiquetas distintas de hasta 40 caracteres, separadas por comas.';
    if (
      body.workText &&
      (body.workText.length > 500000 || hasControlCharacters(body.workText, true))
    )
      next['workText'] = 'Usa texto plano de hasta 500.000 caracteres sin controles.';
    if (audiovisual && youTubeInput.trim() && !youTubeId)
      next['youTubeId'] =
        'Pega un enlace HTTPS válido de YouTube o su identificador de 11 caracteres.';
    if (
      body.category === 'cursos' &&
      !body.collectionKind &&
      !(current?.category === 'cursos' && !current.collectionKind)
    )
      next['collectionKind'] = 'Selecciona curso o programa.';
    if (status === 'published' && body.collectionKind && !body.itemIds?.length)
      next['itemIds'] = 'Añade al menos un elemento publicado antes de publicar.';
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
      if (!alive.current) return;
      setCurrent(updated);
      const savedFields: ContentFields = {
        title: updated.title,
        slug: updated.slug,
        summary: updated.summary,
        body: updated.body,
        category: updated.category,
        coverAsset: updated.coverAsset,
        durationSeconds: updated.durationSeconds,
        author: updated.author ?? null,
        tags: updated.tags ?? [],
        workText: updated.workText ?? null,
        youTubeId: updated.youTubeId ?? null,
        collectionKind: updated.collectionKind ?? null,
        itemIds: updated.itemIds ?? [],
      };
      const savedDuration = updated.durationSeconds?.toString() ?? '';
      const savedTags = updated.tags?.join(', ') ?? '';
      setFields(savedFields);
      setDuration(savedDuration);
      setTagsText(savedTags);
      setBaseline(
        JSON.stringify({ fields: savedFields, duration: savedDuration, tagsText: savedTags }),
      );
      setSaved(
        updated.status === 'published'
          ? 'Contenido publicado.'
          : updated.status === 'archived'
            ? 'Contenido archivado.'
            : 'Borrador guardado.',
      );
      if (!current) {
        permitNavigation();
        navigate('/admin/content/' + encodeURIComponent(updated.id), { replace: true });
      }
    } catch (failure) {
      if (!alive.current) return;
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
      if (alive.current) setBusy(false);
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
        Gestionar contenidos
      </Link>
      <div className="page-heading">
        <p className="eyebrow">Administración editorial · {statuses[current?.status ?? 'draft']}</p>
        <h1>{current ? 'Editar contenido' : 'Crear contenido'}</h1>
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
              onChange={(event) => {
                const category = event.target.value as CategoryId;
                if (
                  (fields.workText || fields.youTubeId || fields.itemIds?.length) &&
                  !window.confirm(
                    'Cambiar de categoría descartará la obra y los elementos seleccionados. ¿Continuar?',
                  )
                )
                  return;
                setFields((previous) => ({
                  ...previous,
                  category,
                  workText: null,
                  youTubeId: null,
                  collectionKind: category === 'cursos' ? 'course' : null,
                  itemIds: [],
                }));
                setSaved('');
              }}
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
            <label htmlFor="editor-author">Autor o institución</label>
            <input
              id="editor-author"
              maxLength={180}
              value={fields.author ?? ''}
              onChange={(event) => update('author', event.target.value || null)}
              disabled={busy}
              aria-invalid={Boolean(errors['author'])}
              aria-describedby={described('author', true)}
            />
            {feedback('author', 'Opcional. Usa la atribución real de la obra.')}
          </div>
          <div className="field">
            <label htmlFor="editor-tags">Etiquetas</label>
            <input
              id="editor-tags"
              value={tagsText}
              onChange={(event) => {
                setTagsText(event.target.value);
                setSaved('');
              }}
              disabled={busy}
              aria-invalid={Boolean(errors['tags'])}
              aria-describedby={described('tags', true)}
            />
            {feedback('tags', 'Hasta 12 etiquetas distintas, separadas por comas.')}
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
          {fields.category === 'lecturas' && (
            <div className="field editor-wide">
              <label htmlFor="editor-workText">Lectura completa</label>
              <textarea
                id="editor-workText"
                rows={16}
                maxLength={500000}
                value={fields.workText ?? ''}
                onChange={(event) => update('workText', event.target.value || null)}
                disabled={busy}
                aria-invalid={Boolean(errors['workText'])}
                aria-describedby={described('workText', true)}
              />
              {feedback(
                'workText',
                'Texto plano protegido por suscripción. Conserva los párrafos. No admite formato HTML. Hasta 500.000 caracteres.',
              )}
            </div>
          )}
          {['documentales', 'videos', 'podcast', 'charlas-online'].includes(fields.category) && (
            <div className="field editor-wide">
              <label htmlFor="editor-youTubeId">Enlace o identificador de YouTube</label>
              <input
                id="editor-youTubeId"
                maxLength={MAX_YOUTUBE_REFERENCE_LENGTH}
                autoComplete="off"
                spellCheck={false}
                value={fields.youTubeId ?? ''}
                onChange={(event) => update('youTubeId', event.target.value || null)}
                disabled={busy}
                aria-invalid={Boolean(errors['youTubeId'])}
                aria-describedby={described('youTubeId', true)}
              />
              {feedback(
                'youTubeId',
                'Pega el enlace del vídeo de YouTube. También puedes usar su identificador.',
              )}
            </div>
          )}
          {fields.category === 'cursos' && (
            <div className="field editor-wide">
              <label htmlFor="editor-collectionKind">Tipo de colección</label>
              <select
                id="editor-collectionKind"
                value={fields.collectionKind ?? ''}
                disabled={busy}
                aria-invalid={Boolean(errors['collectionKind'])}
                aria-describedby={described('collectionKind')}
                onChange={(event) => {
                  if (
                    fields.itemIds?.length &&
                    !window.confirm(
                      'Cambiar de tipo descartará los elementos seleccionados. ¿Continuar?',
                    )
                  )
                    return;
                  setFields((previous) => ({
                    ...previous,
                    collectionKind: event.target.value as 'course' | 'program',
                    itemIds: [],
                  }));
                  setSaved('');
                }}
              >
                <option value="" disabled>
                  Seleccionar tipo
                </option>
                <option value="course">Curso: obras ordenadas</option>
                <option value="program">Programa: cursos ordenados</option>
              </select>
              {feedback('collectionKind')}
              {fields.collectionKind && (
                <CollectionEditor
                  kind={fields.collectionKind}
                  value={fields.itemIds ?? []}
                  disabled={busy}
                  onChange={(itemIds) => {
                    setFields((previous) => ({ ...previous, itemIds }));
                    setSaved('');
                  }}
                />
              )}
              {feedback('itemIds')}
            </div>
          )}
        </div>
        {!fields.collectionKind &&
          !(fields.category === 'lecturas' ? fields.workText : fields.youTubeId) && (
            <p className="form-notice">
              {fields.category === 'cursos'
                ? 'Esta ficha heredada puede conservarse publicada. Organízala como curso o programa y añade sus elementos para ofrecer una colección disponible.'
                : 'La ficha puede publicarse, pero la obra completa todavía no estará disponible. Añade la lectura o el enlace de YouTube cuando dispongas del material autorizado.'}
            </p>
          )}
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
        {current && (
          <Link
            className="text-link editor-public-link"
            to={'/admin/audit?module=content&contentId=' + encodeURIComponent(current.id)}
          >
            Historial editorial
          </Link>
        )}
        {current && (
          <Link
            className="text-link editor-public-link"
            to={'/admin/content/' + current.id + '/topics'}
          >
            Gestionar temas del contenido
          </Link>
        )}
        {current?.status === 'published' && (
          <Link className="text-link editor-public-link" to={'/content/' + current.slug}>
            Ver ficha pública
          </Link>
        )}
      </form>
    </>
  );
  return item ? form : <div className="workspace catalog-editor">{form}</div>;
}
