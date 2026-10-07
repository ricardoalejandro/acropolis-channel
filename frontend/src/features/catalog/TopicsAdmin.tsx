import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { topics, topicMessage, validTopicName, validTopicSlug, type AdminTopic, type ContentTopics, type TopicDirectory } from '../../api/topics';
import { ApiError } from '../../api/identity';
import { useUnsavedChanges } from '../../components/useUnsavedChanges';
import { useOperationGuard } from '../accounts/useOperationGuard';
import './topics.css';
function useTopicLoad<T>(load: (signal: AbortSignal) => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [revision, setRevision] = useState(0); const pending = useRef(true);
  useEffect(() => {
    const controller = new AbortController(); pending.current = true; setLoading(true); setError('');
    void load(controller.signal).then(value => { if (!controller.signal.aborted) { setData(value); pending.current = false; setLoading(false); } }, failure => { if (!controller.signal.aborted) { setError(topicMessage(failure)); pending.current = false; setLoading(false); } });
    return () => controller.abort();
  }, [load, revision]);
  return { data, loading, error, reload: () => { if (!pending.current) { pending.current = true; setRevision(value => value + 1); } } };
}
function LoadNotice({ loading, error, retry }: { loading: boolean; error: string; retry: () => void }) {
  const [retryShown, setRetryShown] = useState(false);
  useEffect(() => { if (error) setRetryShown(true); }, [error]);
  return <>{loading && <p role="status">Cargando temas…</p>}<div className={error ? 'form-alert' : undefined} role={error ? 'alert' : undefined}>{error && <p>{error}</p>}{(error || retryShown) && <button className="button button-outline button-small" type="button" aria-disabled={loading} onClick={() => { if (!loading) retry(); }}>{loading ? 'Cargando temas…' : error ? 'Reintentar carga de temas' : 'Actualizar temas'}</button>}</div></>;
}
export function TopicList() {
  const [params] = useSearchParams(); return <TopicListView key={params.toString()} />;
}
function TopicListView() {
  const [params, setParams] = useSearchParams();
  const raw = Number(params.get('page')); const page = Number.isInteger(raw) && raw >= 1 && raw <= 1000000 ? raw : 1;
  const querySearch = (params.get('search') ?? '').slice(0, 100); const queryStatus = params.get('status') === 'archived' ? 'archived' : params.get('status') === 'active' ? 'active' : '';
  const [search, setSearch] = useState(querySearch); const [status, setStatus] = useState(queryStatus);
  const load = useCallback((signal: AbortSignal) => topics.list({ page, search: querySearch, status: queryStatus }, signal), [page, querySearch, queryStatus]);
  const state = useTopicLoad(load); const operation = useOperationGuard(); const pending = useRef(false); const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  function change(nextPage: number, filters = false) {
    const next = new URLSearchParams(); const text = filters ? search.trim() : querySearch; const selected = filters ? status : queryStatus;
    if (text) next.set('search', text); if (selected) next.set('status', selected); if (nextPage > 1) next.set('page', String(nextPage)); setParams(next);
  }
  async function move(directory: TopicDirectory, index: number, direction: 'up' | 'down') {
    if (pending.current || state.loading || querySearch || queryStatus) return;
    pending.current = true; setBusy(true); setError(''); setNotice(''); const active = operation.begin();
    try {
      const offset = direction === 'up' ? index - 1 : index + 2;
      let beforeId: string | null = null;
      if (offset >= 0 && offset < directory.items.length) beforeId = directory.items[offset]!.id;
      else if (direction === 'up') {
        const previous = await topics.list({ page: page - 1 }); beforeId = previous.items.at(-1)?.id ?? null;
        if (previous.directoryVersion !== directory.directoryVersion || !beforeId) throw new ApiError(409, 'concurrency_conflict');
      } else if (page * directory.pageSize < directory.total) {
        const next = await topics.list({ page: page + 1 }); beforeId = next.items[offset - directory.pageSize]?.id ?? null;
        if (next.directoryVersion !== directory.directoryVersion) throw new ApiError(409, 'concurrency_conflict');
      }
      if (!active()) return;
      await topics.move(directory.directoryVersion, directory.items[index]!.id, beforeId);
      if (!active()) return;
      setNotice('Orden actualizado.'); state.reload();
    } catch (failure) { if (active()) setError(topicMessage(failure)); }
    finally { pending.current = false; if (active()) setBusy(false); }
  }
  return <div className="workspace"><div className="page-heading"><h1>Temas</h1><p className="lead">Organiza ideas para explorar la mediateca. Los formatos y las etiquetas del contenido se conservan.</p><Link className="button button-small" to="/admin/topics/new">Crear tema</Link></div>
    <form className="catalog-filters" onSubmit={event => { event.preventDefault(); change(1, true); }}><div className="field"><label htmlFor="topic-search">Buscar tema</label><input id="topic-search" type="search" maxLength={100} value={search} onChange={event => setSearch(event.target.value)} /></div><div className="field"><label htmlFor="topic-status">Estado de tema</label><select id="topic-status" value={status} onChange={event => setStatus(event.target.value)}><option value="">Todos</option><option value="active">Activos</option><option value="archived">Archivados</option></select></div><button className="button button-small">Buscar temas</button></form>
    <section className="surface topic-directory" aria-labelledby="topic-directory-heading" aria-busy={state.loading || busy}><h2 id="topic-directory-heading">Directorio de temas</h2><p>Para ordenar, deja la búsqueda y el estado sin filtros. Archivar conserva las asociaciones existentes.</p><LoadNotice loading={state.loading} error={state.error} retry={state.reload} />{error && <div className="form-alert" role="alert"><p>{error}</p><button type="button" className="button button-outline button-small" onClick={state.reload}>Recargar directorio</button></div>}{notice && <p role="status">{notice}</p>}
    {state.data && <><p role="status">{state.data.total} {state.data.total === 1 ? 'tema' : 'temas'}</p>{state.data.items.length ? <ol className="topic-list" start={(page - 1) * state.data.pageSize + 1}>{state.data.items.map((topic, index) => <li key={topic.id}><div><Link className="text-link" to={'/admin/topics/' + topic.id}>{topic.name}</Link><p className="muted">{topic.slug} · {topic.status === 'active' ? 'Activo' : 'Archivado'}</p></div><div className="topic-actions"><button type="button" className="button button-outline button-small" disabled={Boolean(querySearch || queryStatus) || ((page - 1) * state.data!.pageSize + index === 0)} aria-disabled={busy || state.loading} aria-label={'Subir tema ' + topic.name} onClick={() => void move(state.data!, index, 'up')}>Subir</button><button type="button" className="button button-outline button-small" disabled={Boolean(querySearch || queryStatus) || (page - 1) * state.data!.pageSize + index + 1 >= state.data!.total} aria-disabled={busy || state.loading} aria-label={'Bajar tema ' + topic.name} onClick={() => void move(state.data!, index, 'down')}>Bajar</button></div></li>)}</ol> : <p>No hay temas con estos filtros. Puedes crear un tema cuando lo necesites.</p>}
    <nav className="pagination" aria-label="Paginación de temas"><button className="button button-outline button-small" disabled={page <= 1 || busy || state.loading} onClick={() => change(page - 1)}>Anterior</button><span>Página {page}</span><button className="button button-outline button-small" disabled={page * state.data.pageSize >= state.data.total || busy || state.loading} onClick={() => change(page + 1)}>Siguiente</button></nav></>}
    </section></div>;
}
export function TopicEditor() {
  const { id } = useParams(); return id ? <ExistingTopic key={id} id={id} /> : <TopicForm key="new" />;
}
function ExistingTopic({ id }: { id: string }) {
  const load = useCallback((signal: AbortSignal) => topics.detail(id, signal), [id]); const state = useTopicLoad(load); const dirty = useRef(false);
  const dirtyChanged = useCallback((value: boolean) => { dirty.current = value; }, []);
  function retry() { if (!dirty.current || window.confirm('¿Recargar y sustituir tus cambios sin guardar?')) state.reload(); }
  return <div className="workspace">{!state.data && <h1>Editar tema</h1>}<LoadNotice loading={state.loading} error={state.error} retry={retry} />{state.data && <TopicForm key={state.data.version} item={state.data} reload={state.reload} dirtyChanged={dirtyChanged} />}</div>;
}
function TopicForm({ item, reload, dirtyChanged }: { item?: AdminTopic; reload?: () => void; dirtyChanged?: (value: boolean) => void }) {
  const navigate = useNavigate(); const operation = useOperationGuard(); const [current, setCurrent] = useState(item); const [name, setName] = useState(item?.name ?? ''); const [slug, setSlug] = useState(item?.slug ?? '');
  const [error, setError] = useState(''); const [fields, setFields] = useState<Record<string, string>>({}); const [notice, setNotice] = useState(''); const [confirm, setConfirm] = useState(false); const [busy, setBusy] = useState(false); const pending = useRef(false); const form = useRef<HTMLFormElement>(null);
  const dirty = name !== (current?.name ?? '') || slug !== (current?.slug ?? ''); const allowNavigation = useUnsavedChanges(dirty);
  useEffect(() => { dirtyChanged?.(dirty); return () => dirtyChanged?.(false); }, [dirty, dirtyChanged]);
  function reloadConfirmed() { if (!dirty || window.confirm('¿Recargar y sustituir tus cambios sin guardar?')) reload?.(); }
  async function write(stateChange = false) {
    if (pending.current) return;
    const invalid: Record<string, string> = {}; if (!validTopicName(name)) invalid['name'] = 'Usa entre 2 y 180 caracteres sin controles.'; if (!current && !validTopicSlug(slug)) invalid['slug'] = 'Usa entre 2 y 160 letras minúsculas, números y guiones simples.';
    setFields(invalid); setError(''); setNotice(''); if (Object.keys(invalid).length) { form.current?.querySelector<HTMLElement>(Object.keys(invalid)[0] === 'name' ? '#topic-name' : '#topic-slug')?.focus(); return; }
    pending.current = true; setBusy(true); const active = operation.begin();
    try {
      const saved = current ? stateChange ? await topics.state(current.id, current.version, current.status === 'active' ? 'archived' : 'active') : await topics.update(current.id, current.version, name.trim()) : await topics.create(slug, name.trim());
      if (!active()) return;
      setCurrent(saved); setName(saved.name); setSlug(saved.slug); setConfirm(false); setNotice(stateChange ? saved.status === 'archived' ? 'Tema archivado. Las asociaciones se conservan.' : 'Tema restaurado.' : 'Tema guardado.');
      if (!current) { allowNavigation(); navigate('/admin/topics/' + saved.id, { replace: true }); }
    } catch (failure) { if (!active()) return; setError(topicMessage(failure)); if (failure instanceof ApiError) setFields(Object.fromEntries(Object.entries(failure.fields).map(([key, values]) => [key, values.join(' ')]))); }
    finally { pending.current = false; if (active()) setBusy(false); }
  }
  function submit(event: FormEvent) { event.preventDefault(); void write(); }
  return <div className={item ? '' : 'workspace'}><Link className="text-link back-link" to="/admin/topics">Volver a Temas</Link><div className="page-heading"><h1>{current ? 'Editar tema' : 'Crear tema'}</h1><p className="lead">Un tema agrupa contenidos por idea; conserva los seis formatos de la mediateca.</p></div><form ref={form} className="surface topic-form" onSubmit={submit} noValidate aria-busy={busy}>{error && <div className="form-alert" role="alert"><p>{error}</p>{reload && <button type="button" className="button button-outline button-small" onClick={reloadConfirmed}>Recargar tema</button>}</div>}{notice && <p role="status">{notice}</p>}<div className="field"><label htmlFor="topic-name">Nombre del tema</label><input id="topic-name" maxLength={180} value={name} aria-invalid={Boolean(fields['name'])} aria-describedby={fields['name'] ? 'topic-name-error' : undefined} onChange={event => setName(event.target.value)} readOnly={busy} />{fields['name'] && <p className="field-error" id="topic-name-error">{fields['name']}</p>}</div>{current ? <p><strong>Dirección:</strong> <span className="topic-slug">{current.slug}</span>. Se conserva para mantener los enlaces.</p> : <div className="field"><label htmlFor="topic-slug">Dirección del tema</label><input id="topic-slug" maxLength={160} value={slug} aria-invalid={Boolean(fields['slug'])} aria-describedby="topic-slug-help" onChange={event => setSlug(event.target.value)} readOnly={busy} /><p className={fields['slug'] ? 'field-error' : 'field-help'} id="topic-slug-help">{fields['slug'] ?? 'Minúsculas, números y guiones entre palabras. Se conservará después de crear el tema.'}</p></div>}<div className="topic-actions"><button className="button" aria-disabled={busy}>{busy ? 'Guardando…' : 'Guardar tema'}</button>{current && <button type="button" className="button button-outline" disabled={dirty} aria-disabled={busy} onClick={() => { if (!pending.current) setConfirm(true); }}>{current.status === 'active' ? 'Archivar tema' : 'Restaurar tema'}</button>}</div>{dirty && current && <p className="field-help">Guarda el nombre antes de cambiar el estado.</p>}{confirm && current && <div className="editor-confirmation" role="alert"><p>{current.status === 'active' ? 'El tema dejará de aparecer como filtro público. Sus contenidos y asociaciones se conservan.' : 'El tema volverá a estar disponible como filtro público.'}</p><button type="button" className="button button-small" aria-disabled={busy} onClick={() => void write(true)}>{current.status === 'active' ? 'Confirmar archivo de tema' : 'Confirmar restauración de tema'}</button><button type="button" className="link-button" aria-disabled={busy} onClick={() => { if (!pending.current) setConfirm(false); }}>Cancelar</button></div>}</form></div>;
}
export function ContentTopicsEditor() {
  const { id } = useParams(); return id ? <AssignedTopics key={id} id={id} /> : null;
}
function AssignedTopics({ id }: { id: string }) {
  const load = useCallback((signal: AbortSignal) => topics.assigned(id, signal), [id]); const state = useTopicLoad(load); const dirty = useRef(false);
  const dirtyChanged = useCallback((value: boolean) => { dirty.current = value; }, []);
  function retry() { if (!dirty.current || window.confirm('¿Recargar y sustituir tu selección sin guardar?')) state.reload(); }
  return <div className="workspace"><Link className="text-link back-link" to={'/admin/content/' + id}>Volver al contenido</Link><div className="page-heading"><h1>Temas del contenido</h1><p className="lead">Selecciona hasta 12 temas. El formato y las etiquetas se conservan.</p></div><LoadNotice loading={state.loading} error={state.error} retry={retry} />{state.data && <AssignmentForm key={state.data.contentVersion} id={id} initial={state.data} reload={state.reload} dirtyChanged={dirtyChanged} />}</div>;
}
function AssignmentForm({ id, initial, reload, dirtyChanged }: { id: string; initial: ContentTopics; reload: () => void; dirtyChanged: (value: boolean) => void }) {
  const operation = useOperationGuard(); const [current, setCurrent] = useState(initial); const [selected, setSelected] = useState(initial.items.map(x => x.id)); const [known, setKnown] = useState<AdminTopic[]>(initial.items); const [page, setPage] = useState(1); const [search, setSearch] = useState(''); const [applied, setApplied] = useState('');
  const load = useCallback((signal: AbortSignal) => topics.list({ page, search: applied, status: 'active' }, signal), [page, applied]); const directory = useTopicLoad(load);
  const [error, setError] = useState(''); const [notice, setNotice] = useState(''); const [busy, setBusy] = useState(false); const pending = useRef(false);
  const dirty = [...selected].sort().join(',') !== current.items.map(x => x.id).sort().join(','); useUnsavedChanges(dirty);
  useEffect(() => { dirtyChanged(dirty); return () => dirtyChanged(false); }, [dirty, dirtyChanged]);
  function toggle(topic: AdminTopic, checked: boolean) {
    if (pending.current) return; setError(''); setNotice('');
    if (checked && selected.length >= 12) { setError('Puedes seleccionar hasta 12 temas.'); return; }
    setSelected(values => checked ? [...values, topic.id] : values.filter(value => value !== topic.id));
    setKnown(values => values.some(value => value.id === topic.id) ? values : [...values, topic]);
  }
  async function save(event: FormEvent) {
    event.preventDefault(); if (pending.current || !dirty) return; pending.current = true; setBusy(true); setError(''); setNotice(''); const active = operation.begin();
    try { const saved = await topics.assign(id, current.contentVersion, selected); if (!active()) return; setCurrent(saved); setSelected(saved.items.map(x => x.id)); setKnown(saved.items); setNotice('Temas del contenido guardados.'); }
    catch (failure) { if (active()) setError(topicMessage(failure)); }
    finally { pending.current = false; if (active()) setBusy(false); }
  }
  return <section className="surface topic-assignment" aria-labelledby="assigned-heading"><h2 id="assigned-heading">Seleccionar temas</h2><p>Un tema archivado ya asociado puede conservarse o retirarse explícitamente.</p><form className="catalog-filters" onSubmit={event => { event.preventDefault(); setPage(1); setApplied(search.trim()); }}><div className="field"><label htmlFor="assignment-search">Buscar temas activos</label><input id="assignment-search" type="search" maxLength={100} value={search} onChange={event => setSearch(event.target.value)} /></div><button className="button button-small">Buscar temas activos</button></form><LoadNotice loading={directory.loading} error={directory.error} retry={directory.reload} />
  <form onSubmit={save} aria-busy={busy}>{error && <div className="form-alert" role="alert"><p>{error}</p><button className="button button-outline button-small" type="button" onClick={() => { if (!pending.current && (!dirty || window.confirm('¿Recargar y sustituir tu selección sin guardar?'))) reload(); }}>Recargar asociaciones</button></div>}{notice && <p role="status">{notice}</p>}
  <fieldset className="topic-options"><legend>Temas activos disponibles</legend>{directory.data?.items.map(topic => <label key={topic.id}><input type="checkbox" checked={selected.includes(topic.id)} disabled={busy || directory.loading} onChange={event => toggle(topic, event.target.checked)} /><span>{topic.name}</span></label>)}{directory.data?.items.length === 0 && <p>No hay temas activos con esta búsqueda.</p>}</fieldset>
  <h3>Selección actual ({selected.length}/12)</h3>{selected.length ? <ul className="topic-selection">{selected.map(value => { const topic = known.find(x => x.id === value); return <li key={value}><span>{topic?.name ?? value}{topic?.status === 'archived' ? ' · Archivado (se conserva)' : ''}</span><button type="button" className="link-button" aria-disabled={busy} aria-label={'Retirar tema ' + (topic?.name ?? value)} onClick={() => { if (!pending.current) setSelected(values => values.filter(x => x !== value)); }}>Retirar</button></li>; })}</ul> : <p>Sin temas seleccionados.</p>}
  <button className="button" aria-disabled={busy || !dirty}>{busy ? 'Guardando…' : 'Guardar temas del contenido'}</button></form>
  {directory.data && <nav className="pagination" aria-label="Paginación de temas activos"><button className="button button-outline button-small" type="button" disabled={page <= 1 || busy || directory.loading} onClick={() => setPage(value => value - 1)}>Anterior</button><span>Página {page}</span><button className="button button-outline button-small" type="button" disabled={page * directory.data.pageSize >= directory.data.total || busy || directory.loading} onClick={() => setPage(value => value + 1)}>Siguiente</button></nav>}
  </section>;
}
