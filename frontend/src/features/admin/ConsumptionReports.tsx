import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { categoryLabel } from '../../api/catalog';
import { reportMessage } from '../../api/reports';
import { consumptionActivity, consumptionQuery, utcConsumptionWindow, type AccountConsumptionReport, type ConsumptionContent, type ConsumptionContentPage, type ConsumptionMetrics, type ConsumptionQuery, type ConsumptionReport } from '../../api/consumptionActivity';
import './consumptionReports.css';
const number = new Intl.NumberFormat('es-PE');
const percent = new Intl.NumberFormat('es-PE', { maximumFractionDigits: 1 });
const timestamp = new Intl.DateTimeFormat('es-PE', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' });
const sourceLabels = { reading: 'Lecturas', youtube: 'YouTube' };
function initialQuery(): ConsumptionQuery { return utcConsumptionWindow(7); }
function duration(milliseconds: number) {
  const seconds = Math.floor(milliseconds / 1000);
  const minutes = Math.floor(seconds / 60);
  return minutes ? number.format(minutes) + ' min ' + number.format(seconds % 60) + ' s' : number.format(seconds) + ' s';
}
function progress(metrics: ConsumptionMetrics) { return metrics.meanProgressBasisPoints === null ? 'Sin muestras comparables' : percent.format(metrics.meanProgressBasisPoints / 100) + '%'; }
function accounts(metrics: ConsumptionMetrics) { return metrics.accountsWithActivity === null ? 'No disponible' : number.format(metrics.accountsWithActivity); }
function MetricSummary({ metrics }: { metrics: ConsumptionMetrics }) {
  return <dl className="consumption-totals">
    <div><dt>Inicios registrados</dt><dd>{number.format(metrics.starts)}</dd></div>
    <div><dt>Cuentas con actividad</dt><dd>{accounts(metrics)}</dd></div>
    <div><dt>Tiempo registrado</dt><dd>{duration(metrics.creditedMs)}</dd></div>
    <div><dt>Fin comunicado</dt><dd>{number.format(metrics.endedReports)}</dd></div>
    <div><dt>Cobertura media de las muestras</dt><dd>{progress(metrics)}</dd></div>
  </dl>;
}
function MetricTable({ caption, rows }: { caption: string; rows: { label: string; metrics: ConsumptionMetrics }[] }) {
  return <div className="consumption-table-scroll" role="region" aria-label={caption} tabIndex={0}><table><caption>{caption}</caption><thead><tr><th scope="col">Grupo</th><th scope="col">Inicios</th><th scope="col">Cuentas</th><th scope="col">Tiempo registrado</th><th scope="col">Cobertura de muestras</th><th scope="col">Fines comunicados</th></tr></thead><tbody>{rows.map((row) => <tr key={row.label}><th scope="row">{row.label}</th><td>{number.format(row.metrics.starts)}</td><td>{accounts(row.metrics)}</td><td>{duration(row.metrics.creditedMs)}</td><td>{progress(row.metrics)}</td><td>{number.format(row.metrics.endedReports)}</td></tr>)}</tbody></table></div>;
}
function ContentTable({ items }: { items: ConsumptionContent[] }) {
  return <div className="consumption-table-scroll" role="region" aria-label="Consumo por obra y versión" tabIndex={0}><table><caption>Por obra y versión editorial</caption><thead><tr><th scope="col">Obra · título actual</th><th scope="col">Categoría al iniciar</th><th scope="col">Inicios</th><th scope="col">Cuentas</th><th scope="col">Tiempo registrado</th><th scope="col">Cobertura de muestras</th></tr></thead><tbody>{items.map((item) => <tr key={item.contentId + item.contentVersion}><th scope="row"><Link to={'/admin/content/' + encodeURIComponent(item.contentId)}>{item.title ?? 'Ficha no disponible'}</Link><small>Versión editorial <code>{item.contentVersion}</code></small><small>{item.status === 'published' ? 'Publicado' : item.status === 'draft' ? 'Borrador' : item.status === 'archived' ? 'Archivado' : 'Sin ficha actual'} · {sourceLabels[item.sourceKind]}</small></th><td>{categoryLabel(item.categoryAtStart)}</td><td>{number.format(item.metrics.starts)}</td><td>{accounts(item.metrics)}</td><td>{duration(item.metrics.creditedMs)}</td><td>{progress(item.metrics)}</td></tr>)}</tbody></table></div>;
}
export function AccountConsumptionPage() { const { id } = useParams(); return <ConsumptionReportsPage key={id} account />; }
export function ConsumptionReportsPage({ account = false }: { account?: boolean }) {
  const { id = '' } = useParams(); const { user } = useSession();
  const allowed = user?.permissions.includes('Content.Manage') && (!account || user.permissions.includes('Users.Manage'));
  const [query, setQuery] = useState<ConsumptionQuery>(initialQuery);
  const [applied, setApplied] = useState<ConsumptionQuery | null>(null);
  const [data, setData] = useState<{ report: ConsumptionReport | AccountConsumptionReport; page: ConsumptionContentPage | AccountConsumptionReport } | null>(null);
  const [busy, setBusy] = useState(false); const [error, setError] = useState('');
  const attempted = useRef<ConsumptionQuery | null>(null); const pending = useRef(false); const control = useRef<AbortController | null>(null); const serial = useRef(0); const current = useRef(true);
  useEffect(() => { current.current = true; return () => { current.current = false; control.current?.abort(); serial.current++; }; }, []);
  async function load(selected: ConsumptionQuery) {
    if (!allowed || pending.current) return;
    attempted.current = { ...selected };
    try { consumptionQuery(selected, true); } catch { setError('Selecciona fechas UTC válidas: un periodo de 1 a 366 días.'); return; }
    const invocation = ++serial.current; control.current?.abort(); const controller = new AbortController(); control.current = controller;
    pending.current = true; setBusy(true); setError(''); setData(null);
    try {
      const loaded = account
        ? await consumptionActivity.account(id, selected, controller.signal).then((report) => ({ report, page: report }))
        : await Promise.all([consumptionActivity.report(selected, controller.signal), consumptionActivity.content(selected, controller.signal)]).then(([report, page]) => ({ report, page }));
      if (current.current && invocation === serial.current) { setData(loaded); setApplied({ ...selected }); }
    } catch (failure) { if (current.current && invocation === serial.current) setError(reportMessage(failure)); }
    finally { if (current.current && invocation === serial.current) { pending.current = false; setBusy(false); } }
  }
  const report = data?.report; const page = data?.page;
  return <div className="workspace consumption-reports">
    <Link className="text-link back-link" to={account ? '/admin/users/' + encodeURIComponent(id) : '/admin/reports'}>{account ? 'Volver a la cuenta' : 'Volver a reportes'}</Link>
    <div className="page-heading"><h1>{account ? 'Consumo de esta cuenta' : 'Consumo registrado'}</h1><p className="lead">Lecturas y reproducción observadas en la mediateca.</p></div>
    {!allowed ? <p className="form-alert" role="alert">Tu cuenta no tiene los permisos necesarios para consultar este reporte.</p> : <>
      <div className="consumption-presets" role="group" aria-label="Periodos de fechas UTC">{([7, 30, 90] as const).map((days) => <button className="button button-outline button-small" disabled={busy} key={days} onClick={() => setQuery(utcConsumptionWindow(days))}>{days} días</button>)}{!account && <button className="button button-outline button-small" disabled={busy} onClick={() => setQuery(utcConsumptionWindow(365))}>1 año (365 días)</button>}</div>
      <form className="consumption-query" aria-label="Consultar consumo" onSubmit={(event) => { event.preventDefault(); void load({ ...query, page: 1 }); }}>
        <div className="field"><label htmlFor="consumption-from">Desde (UTC)</label><input id="consumption-from" type="date" value={query.from} disabled={busy} onChange={(event) => setQuery({ ...query, from: event.target.value })} required /></div>
        <div className="field"><label htmlFor="consumption-to">Hasta (sin incluir, UTC)</label><input id="consumption-to" type="date" value={query.to} disabled={busy} onChange={(event) => setQuery({ ...query, to: event.target.value })} required /></div>
        <button className="button" disabled={busy}>{busy ? 'Consultando…' : 'Consultar consumo'}</button>
      </form>
      <p className="report-note">El detalle vinculado a cuentas se conserva 90 fechas UTC, incluida la actual; las estadísticas generales por obra y día, 1 año (365 fechas UTC). Las cifras anteriores a la ventana disponible no representan todo el consumo real.</p>
      {busy && <p role="status">Consultando consumo registrado…</p>}
      {error && <div className="form-alert" role="alert">{error}<button className="link-button" disabled={busy} onClick={() => void load(attempted.current ?? { ...query, page: 1 })}>Reintentar consulta</button></div>}
      {report && page && <>
        <section className="surface" aria-labelledby="consumption-summary"><h2 id="consumption-summary">Resumen del periodo</h2><p>Del {report.fromUtc.slice(0, 10)} al {report.toUtc.slice(0, 10)} (límite final excluido), UTC.</p><MetricSummary metrics={report.summary} />
          {!report.recordingEnabled && <p className="form-notice">El registro de consumo está deshabilitado. Pueden existir registros anteriores dentro de su plazo de conservación.</p>}
          {report.summary.starts === 0 && report.summary.recordedPulses === 0 && <p>Sin actividad registrada en este periodo.</p>}
          <p className="report-note">El tiempo suma intervalos de sesiones y puede solaparse entre pestañas. Es una señal del navegador, no una prueba de atención. La cobertura media usa {number.format(report.summary.knownProgressSamples)} muestras comparables; {number.format(report.summary.unknownProgressSamples)} no tienen porcentaje válido. Un fin comunicado no significa haber recorrido el 100%.</p>
          <p className="report-note">Las cuentas con actividad son distintas dentro de cada intervalo o grupo y no se suman entre días u obras. «No disponible» indica que el periodo supera la ventana de detalle.</p>
          <p className="report-updated">Datos disponibles desde <time dateTime={report.availableFromUtc}>{timestamp.format(new Date(report.availableFromUtc))} UTC</time>.{report.detailAvailableFromUtc && <> Cuentas distintas disponibles desde <time dateTime={report.detailAvailableFromUtc}>{timestamp.format(new Date(report.detailAvailableFromUtc))} UTC</time>.</>}</p>
          <p className="report-updated">Consultado: <time dateTime={report.generatedUtc}>{timestamp.format(new Date(report.generatedUtc))} UTC</time>.</p>
        </section>
        {'byKind' in report && <section className="surface"><h2>Tipos y categorías</h2><MetricTable caption="Por tipo de señal" rows={report.byKind.map((row) => ({ label: sourceLabels[row.key as keyof typeof sourceLabels], metrics: row.metrics }))} /><MetricTable caption="Por categoría al iniciar" rows={report.byCategory.map((row) => ({ label: categoryLabel(row.key), metrics: row.metrics }))} /></section>}
        <section className="surface"><h2>Actividad diaria</h2><MetricTable caption="Fechas UTC" rows={report.daily.map((row) => ({ label: row.dayUtc, metrics: row.metrics }))} /></section>
        <section className="surface"><h2>Obras consultadas</h2>{page.items.length ? <ContentTable items={page.items} /> : <p>No hay obras registradas en esta página.</p>}
          <nav className="pagination" aria-label="Paginación del consumo por obra"><button className="button button-outline button-small" disabled={busy || page.page <= 1} onClick={() => applied && void load({ ...applied, page: page.page - 1 })}>Anterior</button><span>Página {page.page} · {number.format(page.total)} versiones de obras</span><button className="button button-outline button-small" disabled={busy || page.page * page.pageSize >= page.total} onClick={() => applied && void load({ ...applied, page: page.page + 1 })}>Siguiente</button></nav>
          <p className="report-note">Cada consulta y página es una instantánea independiente. El título y estado corresponden a la ficha actual.</p>
          <p className="report-updated">Página consultada: <time dateTime={page.generatedUtc}>{timestamp.format(new Date(page.generatedUtc))} UTC</time>.</p>
        </section>
      </>}
    </>}
  </div>;
}
