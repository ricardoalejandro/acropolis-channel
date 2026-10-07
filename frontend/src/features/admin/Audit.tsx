import { useCallback, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { audit, auditModules, auditActionLabels, type AuditModule } from '../../api/audit';
import { CatalogState, Pagination } from '../catalog/Catalog';
import { useCatalog } from '../catalog/useCatalog';
export function AuditPage() {
  const { user } = useSession();
  const [params, setParams] = useSearchParams();
  const allowed = auditModules.filter((module) => user?.permissions.includes(module.permission));
  const selected = allowed.find((module) => module.id === params.get('module')) ?? allowed[0];
  if (!selected)
    return (
      <div className="workspace">
        <h1>Auditoría</h1>
        <p className="form-alert">Tu cuenta no tiene permisos para consultar la auditoría.</p>
      </div>
    );
  return (
    <div className="workspace">
      <div className="page-heading">
        <h1>Auditoría</h1>
        <p className="lead">Historial de cambios, de sólo lectura.</p>
      </div>
      <nav className="audit-tabs" aria-label="Módulo de auditoría">
        {allowed.map((module) => (
          <button
            className={'button ' + (selected.id === module.id ? '' : 'button-outline')}
            key={module.id}
            aria-current={selected.id === module.id ? 'page' : undefined}
            onClick={() => setParams({ module: module.id })}
          >
            {module.label}
          </button>
        ))}
      </nav>
      <AuditView
        key={selected.id + params.toString()}
        module={selected.id}
        params={params}
        setParams={setParams}
      />
    </div>
  );
}
function AuditView({
  module,
  params,
  setParams,
}: {
  module: AuditModule;
  params: URLSearchParams;
  setParams: (params: URLSearchParams) => void;
}) {
  const config = auditModules.find((item) => item.id === module)!;
  const [from, setFrom] = useState((params.get('fromUtc') ?? '').slice(0, 16));
  const [to, setTo] = useState((params.get('toUtc') ?? '').slice(0, 16));
  const [action, setAction] = useState(params.get('action') ?? '');
  const [objectId, setObjectId] = useState(params.get(config.objectKey) ?? '');
  const [error, setError] = useState('');
  const key = params.toString();
  const load = useCallback(() => audit.list(module, new URLSearchParams(key)), [module, key]);
  const state = useCatalog(load, key);
  function filter(page = 1) {
    setError('');
    const start = from ? Date.parse(from + ':00Z') : null;
    const end = to ? Date.parse(to + ':00Z') : null;
    if (
      (start !== null && !Number.isFinite(start)) ||
      (end !== null && !Number.isFinite(end)) ||
      (start !== null && end !== null && start > end)
    ) {
      setError('Revisa el rango de fechas: Desde debe ser anterior a Hasta.');
      return;
    }
    const next = new URLSearchParams({ module });
    if (start !== null) next.set('fromUtc', new Date(start).toISOString());
    if (end !== null) next.set('toUtc', new Date(end).toISOString());
    if (action) next.set('action', action);
    if (objectId.trim()) next.set(config.objectKey, objectId.trim());
    if (page > 1) next.set('page', String(page));
    setParams(next);
  }
  return (
    <section className="surface">
      <form
        className="audit-filters"
        onSubmit={(event) => {
          event.preventDefault();
          filter();
        }}
      >
        <div className="field">
          <label htmlFor="audit-from">Desde (UTC)</label>
          <input
            id="audit-from"
            type="datetime-local"
            value={from}
            onChange={(event) => setFrom(event.target.value)}
          />
        </div>
        <div className="field">
          <label htmlFor="audit-to">Hasta (UTC)</label>
          <input
            id="audit-to"
            type="datetime-local"
            value={to}
            onChange={(event) => setTo(event.target.value)}
          />
        </div>
        <div className="field">
          <label htmlFor="audit-action">Acción</label>
          <select
            id="audit-action"
            value={action}
            onChange={(event) => setAction(event.target.value)}
          >
            <option value="">Todas</option>
            {config.actions.map((item) => (
              <option key={item} value={item}>
                {auditActionLabels[item] ?? item}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="audit-object">
            ID de{' '}
            {module === 'users' ? 'usuario' : module === 'content' ? 'contenido' : 'suscripción'}
          </label>
          <input
            id="audit-object"
            value={objectId}
            onChange={(event) => setObjectId(event.target.value)}
          />
        </div>
        <button className="button" type="submit">
          Filtrar
        </button>
      </form>
      {error && (
        <p className="form-alert" role="alert">
          {error}
        </p>
      )}
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <>
            <p className="results-count" role="status">
              {state.data.total} cambios registrados
            </p>
            {state.data.items.length ? (
              <div
                className="table-scroll"
                role="region"
                aria-label="Historial de cambios"
                tabIndex={0}
              >
                <table>
                  <thead>
                    <tr>
                      <th>Fecha UTC</th>
                      <th>Acción</th>
                      <th>Objeto</th>
                      <th>Autor del cambio</th>
                      <th>Detalle</th>
                    </tr>
                  </thead>
                  <tbody>
                    {state.data.items.map((item) => (
                      <tr key={item.id}>
                        <td>
                          {new Date(item.createdUtc).toISOString().replace('T', ' ').slice(0, 19)}
                        </td>
                        <td title={item.action}>
                          {auditActionLabels[item.action] ?? 'Cambio registrado'}
                        </td>
                        <td className="wrap-anywhere">{item.objectId}</td>
                        <td className="wrap-anywhere">{item.actorId}</td>
                        <td className="wrap-anywhere">{item.description}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="empty-state">No hay cambios con estos filtros.</p>
            )}
            <Pagination
              page={state.data.page}
              pageSize={state.data.pageSize}
              total={state.data.total}
              change={(page) => {
                const next = new URLSearchParams(params);
                next.set('module', module);
                if (page > 1) next.set('page', String(page));
                else next.delete('page');
                setParams(next);
              }}
            />
          </>
        )}
      </CatalogState>
    </section>
  );
}
