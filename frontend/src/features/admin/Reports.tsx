import { type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { categories } from '../../api/catalog';
import {
  reports,
  reportMessage,
  catalogStatuses,
  contentKinds,
  type ReportCount,
  type CatalogReport,
} from '../../api/reports';
import { useCatalog } from '../catalog/useCatalog';
import './reports.css';
const number = new Intl.NumberFormat('es-PE');
const date = new Intl.DateTimeFormat('es-PE', { dateStyle: 'medium', timeStyle: 'short' });
const identityLabels = {
  active: 'Activos',
  pending: 'Pendientes de confirmación',
  disabled: 'Deshabilitados',
};
const contentLabels = { draft: 'Borradores', published: 'Publicados', archived: 'Archivados' };
const subscriptionLabels = { active: 'Activas', cancelled: 'Canceladas', suspended: 'Suspendidas' };
const kindLabels = { work: 'Obras', course: 'Cursos', program: 'Programas' };
function Counts({
  rows,
  labels,
  caption,
  dimension = 'Estado',
}: {
  rows: ReportCount[];
  labels?: Record<string, string>;
  caption: string;
  dimension?: string;
}) {
  return (
    <table className="report-counts">
      <caption>{caption}</caption>
      <thead>
        <tr>
          <th scope="col">{dimension}</th>
          <th scope="col">Cantidad</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.key}>
            <th scope="row">{labels?.[row.key] ?? row.key}</th>
            <td>{number.format(row.count)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
function CatalogMatrix({ report, kind = false }: { report: CatalogReport; kind?: boolean }) {
  const rows = kind ? contentKinds.map((id) => ({ id, label: kindLabels[id] })) : categories;
  return (
    <div
      className="report-table-scroll"
      role="region"
      aria-label={kind ? 'Contenidos por tipo y estado' : 'Contenidos por categoría y estado'}
      tabIndex={0}
    >
      <table className="report-matrix">
        <caption>{kind ? 'Por tipo de contenido' : 'Por categoría'}</caption>
        <thead>
          <tr>
            <th scope="col">{kind ? 'Tipo' : 'Categoría'}</th>
            {catalogStatuses.map((status) => (
              <th scope="col" key={status}>
                {contentLabels[status]}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <th scope="row">{row.label}</th>
              {catalogStatuses.map((status) => {
                const bucket = kind
                  ? report.byKindAndStatus.find(
                      (item) => item.kind === row.id && item.status === status,
                    )
                  : report.byCategoryAndStatus.find(
                      (item) => item.category === row.id && item.status === status,
                    );
                return <td key={status}>{number.format(bucket!.count)}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
function ReportPanel<T extends { total: number; generatedUtc: string }>({
  title,
  module,
  load,
  children,
}: {
  title: string;
  module: string;
  load: () => Promise<T>;
  children: (data: T) => ReactNode;
}) {
  const state = useCatalog(load, module);
  return (
    <section
      className="surface report-panel"
      aria-labelledby={'report-' + module}
      aria-busy={state.loading}
    >
      <div className="report-heading">
        <h2 id={'report-' + module}>{title}</h2>
        <button
          className="button button-outline button-small"
          disabled={state.loading}
          onClick={state.reload}
        >
          {state.failure ? 'Reintentar ' : 'Actualizar '}
          {title}
        </button>
      </div>
      {state.loading ? (
        <p role="status">Cargando reporte de {title.toLowerCase()}…</p>
      ) : state.failure ? (
        <p className="form-alert" role="alert">
          {reportMessage(state.failure)}
        </p>
      ) : (
        state.data && (
          <>
            <dl className="report-total">
              <dt>Total de {title.toLowerCase()}</dt>
              <dd>{number.format(state.data.total)}</dd>
            </dl>
            {state.data.total === 0 && (
              <p className="report-empty">Todavía no hay registros de {title.toLowerCase()}.</p>
            )}
            {children(state.data)}
            <p className="report-updated">
              Consultado:{' '}
              <time dateTime={state.data.generatedUtc}>
                {date.format(new Date(state.data.generatedUtc))}
              </time>
            </p>
          </>
        )
      )}
    </section>
  );
}
export function ReportsPage() {
  const { user } = useSession();
  const users = user?.permissions.includes('Users.Manage');
  const content = user?.permissions.includes('Content.Manage');
  const subscriptions = user?.permissions.includes('Subscriptions.Manage');
  const allowed = users || content || subscriptions;
  return (
    <div className="workspace reports-workspace">
      <div className="page-heading">
        <h1>Reportes</h1>
        <p className="lead">Estado actual de la plataforma.</p>
        {content && <p className="admin-report-link"><Link className="text-link" to="/admin/reports/consumption">Consultar consumo registrado →</Link></p>}
        {subscriptions && <p className="admin-report-link"><Link className="text-link" to="/admin/reports/subscription-events">Ver movimientos de suscripción →</Link></p>}
      </div>
      {allowed ? (
        <>
          <p className="report-scope">
            Los conteos corresponden al momento de cada consulta. Cada sección se actualiza por
            separado.
          </p>
          <div className="reports-grid">
            {users && (
              <ReportPanel title="Usuarios" module="identity" load={reports.identity}>
                {(report) => (
                  <>
                    <Counts
                      rows={report.byStatus}
                      labels={identityLabels}
                      caption="Usuarios por estado"
                    />
                    <Counts
                      rows={report.byLevel}
                      caption="Usuarios por nivel institucional"
                      dimension="Nivel"
                    />
                    <p className="report-note">
                      Una cuenta puede tener varios niveles. Sus conteos no se suman para obtener el
                      total de usuarios.
                    </p>
                  </>
                )}
              </ReportPanel>
            )}
            {subscriptions && (
              <ReportPanel
                title="Suscripciones"
                module="subscriptions"
                load={reports.subscriptions}
              >
                {(report) => (
                  <>
                    <Counts
                      rows={report.byStatus}
                      labels={subscriptionLabels}
                      caption="Suscripciones por estado"
                    />
                    <p className="report-note">
                      Se cuentan suscripciones registradas. El acceso también requiere una cuenta
                      activa y confirmada.
                    </p>
                  </>
                )}
              </ReportPanel>
            )}
            {content && (
              <ReportPanel title="Contenidos" module="catalog" load={reports.catalog}>
                {(report) => (
                  <>
                    <Counts
                      rows={report.byStatus}
                      labels={contentLabels}
                      caption="Contenidos por estado"
                    />
                    <CatalogMatrix report={report} />
                    <CatalogMatrix report={report} kind />
                  </>
                )}
              </ReportPanel>
            )}
          </div>
        </>
      ) : (
        <div className="form-alert" role="alert">
          <p>Tu cuenta no tiene permisos para consultar reportes.</p>
          <Link to="/profile">Ir a mi perfil</Link>
        </div>
      )}
    </div>
  );
}
