import { useEffect, useRef, useState } from 'react';
import { topics, topicMessage, type PublicTopic, type TopicPage } from '../../api/topics';
export function PublicTopicFilter({
  value,
  change,
}: {
  value: string;
  change: (value: string) => void;
}) {
  const [open, setOpen] = useState(Boolean(value));
  const [page, setPage] = useState(1);
  const [revision, setRevision] = useState(0);
  const [result, setResult] = useState<{
    page: number;
    revision: number;
    data: TopicPage<PublicTopic> | null;
    error: string;
  }>({ page: 0, revision: -1, data: null, error: '' });
  const [retryShown, setRetryShown] = useState(false);
  const loading = open && (result.page !== page || result.revision !== revision);
  const data = result.page === page ? result.data : null;
  const error = loading ? '' : result.error;
  const pending = useRef(false);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    pending.current = true;
    void topics.publicList(page, controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) {
          pending.current = false;
          setResult({ page, revision, data, error: '' });
        }
      },
      (failure) => {
        if (!controller.signal.aborted) {
          pending.current = false;
          setResult((previous) => ({
            page,
            revision,
            data: previous.page === page ? previous.data : null,
            error: topicMessage(failure),
          }));
          setRetryShown(true);
        }
      },
    );
    return () => controller.abort();
  }, [open, page, revision]);
  return (
    <div className="public-topic-filter">
      <button
        className="link-button"
        type="button"
        aria-expanded={open}
        aria-controls="public-topic-options"
        onClick={() => {
          setOpen((value) => !value);
          setRevision((value) => value + 1);
        }}
      >
        Filtrar por tema
      </button>
      {open && (
        <div id="public-topic-options" className="field" aria-busy={loading}>
          <label htmlFor="public-topic-select">Tema</label>
          <select
            id="public-topic-select"
            value={value}
            onChange={(event) => change(event.target.value)}
          >
            <option value="">Todos los temas</option>
            {value && !data?.items.some((topic) => topic.slug === value) && (
              <option value={value}>{value}</option>
            )}
            {data?.items.map((topic) => (
              <option key={topic.id} value={topic.slug}>
                {topic.name}
              </option>
            ))}
          </select>
          {loading && <p role="status">Cargando temas…</p>}
          <div role={error ? 'alert' : undefined}>
            {error && <p>{error}</p>}
            {retryShown && (
              <button
                type="button"
                className="link-button"
                aria-disabled={loading}
                onClick={() => {
                  if (!pending.current) {
                    pending.current = true;
                    setRevision((value) => value + 1);
                  }
                }}
              >
                {loading
                  ? 'Cargando temas…'
                  : error
                    ? 'Reintentar temas'
                    : 'Actualizar temas públicos'}
              </button>
            )}
          </div>
          {data && data.total > 100 && (
            <div className="topic-actions">
              <button
                className="link-button"
                type="button"
                disabled={page <= 1 || loading}
                onClick={() => setPage((value) => value - 1)}
              >
                Temas anteriores
              </button>
              <span>Página {page}</span>
              <button
                className="link-button"
                type="button"
                disabled={page * data.pageSize >= data.total || loading}
                onClick={() => setPage((value) => value + 1)}
              >
                Más temas
              </button>
            </div>
          )}
          {data?.total === 0 && (
            <p>No hay temas disponibles. Puedes seguir buscando por formato o texto.</p>
          )}
        </div>
      )}
    </div>
  );
}
