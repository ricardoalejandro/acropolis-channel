import { useCallback, useRef, useState } from 'react';
import { catalog, type AdminSummary, type CollectionKind } from '../../api/catalog';
import { CatalogState, Pagination } from './Catalog';
import { useCatalog } from './useCatalog';
export function CollectionEditor({
  kind,
  value,
  onChange,
  disabled = false,
}: {
  kind: CollectionKind;
  value: string[];
  onChange: (ids: string[]) => void;
  disabled?: boolean;
}) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState({ search: '', page: 1 });
  const load = useCallback(
    () =>
      catalog.adminList({
        status: 'published',
        search: query.search,
        ...(kind === 'program' ? { category: 'cursos' } : {}),
        page: query.page,
        pageSize: 20,
      }),
    [kind, query],
  );
  const state = useCatalog(load, kind + ':' + JSON.stringify(query));
  const cache = useRef(new Map<string, AdminSummary>());
  const idsKey = [...value].sort().join(',');
  const selectedLoad = useCallback(async () => {
    const ids = idsKey ? idsKey.split(',') : [];
    const missing = ids.filter((id) => !cache.current.has(id));
    for (let start = 0; start < missing.length; start += 5) {
      const batch = await Promise.all(
        missing.slice(start, start + 5).map((id) => catalog.adminSummary(id)),
      );
      for (const item of batch) cache.current.set(item.id, item);
    }
    return ids
      .map((id) => cache.current.get(id))
      .filter((item): item is AdminSummary => Boolean(item));
  }, [idsKey]);
  const selected = useCatalog(selectedLoad, idsKey);
  const options =
    state.data?.items.filter((item) =>
      kind === 'program'
        ? item.collectionKind === 'course'
        : !item.collectionKind && item.category !== 'cursos',
    ) ?? [];
  function move(index: number, offset: number) {
    const ids = [...value];
    const nextIndex = index + offset;
    const current = ids[index];
    const next = ids[nextIndex];
    if (!current || !next) return;
    ids[index] = next;
    ids[nextIndex] = current;
    onChange(ids);
  }
  return (
    <section className="collection-editor" aria-labelledby="collection-items-heading">
      <h3 id="collection-items-heading">
        {kind === 'program' ? 'Cursos del programa' : 'Obras del curso'}
      </h3>
      <p className="field-help">
        Añade {kind === 'program' ? 'cursos publicados' : 'obras publicadas'} y elige el orden.
        Máximo 100. La publicación comprueba que cada elemento sigue disponible.
      </p>
      {selected.error && <p role="alert">{selected.error}</p>}
      {value.length > 0 && (
        <button
          className="link-button"
          type="button"
          disabled={disabled || selected.loading}
          onClick={() => {
            cache.current.clear();
            selected.reload();
          }}
        >
          Actualizar títulos y estados
        </button>
      )}
      {value.length ? (
        <ol className="collection-selection" aria-label="Elementos seleccionados">
          {value.map((id, index) => {
            const title = selected.data?.find((item) => item.id === id)?.title ?? id;
            return (
              <li key={id}>
                <strong>{title}</strong>
                <div className="compact-actions">
                  <button
                    className="link-button"
                    type="button"
                    disabled={disabled || index === 0}
                    aria-label={'Subir ' + title}
                    onClick={() => move(index, -1)}
                  >
                    Subir
                  </button>
                  <button
                    className="link-button"
                    type="button"
                    disabled={disabled || index === value.length - 1}
                    aria-label={'Bajar ' + title}
                    onClick={() => move(index, 1)}
                  >
                    Bajar
                  </button>
                  <button
                    className="link-button"
                    type="button"
                    disabled={disabled}
                    aria-label={'Quitar ' + title}
                    onClick={() => onChange(value.filter((item) => item !== id))}
                  >
                    Quitar
                  </button>
                </div>
              </li>
            );
          })}
        </ol>
      ) : (
        <p className="form-notice">Todavía no has añadido elementos.</p>
      )}
      <div className="collection-search">
        <div className="field">
          <label htmlFor="collection-search">Buscar elementos publicados</label>
          <input
            id="collection-search"
            type="search"
            value={search}
            disabled={disabled}
            onChange={(event) => setSearch(event.target.value)}
            onKeyDown={(event) => {
              if (event.key !== 'Enter' || event.nativeEvent.isComposing) return;
              event.preventDefault();
              setQuery({ search: search.trim(), page: 1 });
            }}
          />
        </div>
        <button
          className="button button-outline button-small"
          type="button"
          disabled={disabled}
          onClick={() => setQuery({ search: search.trim(), page: 1 })}
        >
          Buscar elementos
        </button>
      </div>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        <ul className="collection-options">
          {options.map((item) => (
            <li key={item.id}>
              <span>{item.title}</span>
              <button
                className="link-button"
                type="button"
                disabled={disabled || value.includes(item.id) || value.length >= 100}
                aria-label={'Añadir ' + item.title}
                onClick={() => {
                  cache.current.set(item.id, item);
                  onChange([...value, item.id]);
                }}
              >
                {value.includes(item.id) ? 'Añadido' : 'Añadir'}
              </button>
            </li>
          ))}
        </ul>
        {state.data && !options.length && (
          <p className="field-help">
            No hay elementos compatibles en esta página. Busca por título o consulta la siguiente.
          </p>
        )}
        {state.data && (
          <Pagination
            page={state.data.page}
            pageSize={state.data.pageSize}
            total={state.data.total}
            change={(page) => setQuery((previous) => ({ ...previous, page }))}
          />
        )}
      </CatalogState>
    </section>
  );
}
