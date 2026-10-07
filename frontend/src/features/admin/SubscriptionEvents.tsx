import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { reportMessage } from '../../api/reports';
import {
  inclusiveEventInterval,
  shiftEventDate,
  subscriptionEvents,
  type SubscriptionEventKey,
  type SubscriptionEventReport,
} from '../../api/subscriptionEvents';
import './subscription-events.css';
const number = new Intl.NumberFormat('es-PE');
const day = new Intl.DateTimeFormat('es-PE', { dateStyle: 'medium', timeZone: 'UTC' });
const instant = new Intl.DateTimeFormat('es-PE', {
  dateStyle: 'medium',
  timeStyle: 'short',
  timeZone: 'UTC',
});
const labels: Record<SubscriptionEventKey, string> = {
  activated: 'Primera activación',
  assigned: 'Asignación manual de plan',
  renewed: 'Renovación manual de plan',
  reactivated: 'Reactivación por el usuario',
  cancelled: 'Cancelación por el usuario',
  updated_active_cancelled: 'Activa → cancelada',
  updated_active_suspended: 'Activa → suspendida',
  updated_cancelled_active: 'Cancelada → activa',
  updated_cancelled_suspended: 'Cancelada → suspendida',
  updated_suspended_active: 'Suspendida → activa',
  updated_suspended_cancelled: 'Suspendida → cancelada',
  recovery_suspended: 'Suspensión por recuperación',
  unclassified: 'Otros eventos registrados',
};
function initialDates() {
  const through = new Date().toISOString().slice(0, 10);
  return { through, from: shiftEventDate(through, -29) ?? through };
}
function formattedDay(value: string) {
  return day.format(new Date(value + 'T00:00:00Z'));
}
export function SubscriptionEventsPage() {
  const { user } = useSession();
  const allowed = user?.permissions.includes('Subscriptions.Manage');
  const [dates, setDates] = useState(initialDates);
  const [data, setData] = useState<SubscriptionEventReport | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const version = useRef(0);
  const controller = useRef<AbortController | null>(null);
  useEffect(
    () => () => {
      version.current += 1;
      controller.current?.abort();
    },
    [],
  );
  function change(field: 'from' | 'through', value: string) {
    setDates((current) => ({ ...current, [field]: value }));
    setData(null);
    setError('');
  }
  async function consult(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending.current || !allowed) return;
    const interval = inclusiveEventInterval(dates.from, dates.through);
    if (!interval) {
      setError('Elige un período válido de entre 1 y 366 días.');
      setData(null);
      return;
    }
    const current = ++version.current;
    const request = new AbortController();
    controller.current = request;
    pending.current = true;
    setBusy(true);
    setData(null);
    setError('');
    try {
      const report = await subscriptionEvents(interval, request.signal);
      if (version.current === current) setData(report);
    } catch (failure) {
      if (version.current === current) setError(reportMessage(failure));
    } finally {
      if (version.current === current) {
        pending.current = false;
        controller.current = null;
        setBusy(false);
      }
    }
  }
  const max = data ? Math.max(1, ...data.days.map((row) => row.totalEvents)) : 1;
  return (
    <div className="workspace subscription-events-workspace">
      <Link className="text-link" to="/admin/reports">
        ← Volver a reportes
      </Link>
      <div className="page-heading event-page-heading">
        <p className="eyebrow">Actividad registrada</p>
        <h1>Movimientos de suscripción</h1>
        <p className="lead">Cómo cambia el acceso, a lo largo del tiempo.</p>
      </div>
      {allowed ? (
        <>
          <form
            className="surface event-period"
            aria-label="Período de movimientos"
            onSubmit={(event) => void consult(event)}
            noValidate
          >
            <p id="event-period-help">
              Elige de 1 a 366 días. Se incluyen ambos días seleccionados; las fechas se consultan
              en UTC.
            </p>
            <div className="event-period-fields">
              <div className="field">
                <label htmlFor="event-from">Desde</label>
                <input
                  id="event-from"
                  type="date"
                  value={dates.from}
                  disabled={busy}
                  onChange={(event) => change('from', event.target.value)}
                  aria-describedby="event-period-help"
                  required
                />
              </div>
              <div className="field">
                <label htmlFor="event-through">Hasta (incluido)</label>
                <input
                  id="event-through"
                  type="date"
                  value={dates.through}
                  disabled={busy}
                  onChange={(event) => change('through', event.target.value)}
                  aria-describedby="event-period-help"
                  required
                />
              </div>
              <button className="button button-small" disabled={busy} type="submit">
                {busy ? 'Consultando…' : 'Consultar período'}
              </button>
            </div>
            {error && (
              <p className="form-alert" role="alert">
                {error}
              </p>
            )}
          </form>
          {busy && (
            <p className="event-load" role="status">
              Consultando los movimientos del período…
            </p>
          )}
          {data ? (
            <section className="surface event-results" aria-labelledby="event-result-title">
              <div className="event-results-heading">
                <div>
                  <h2 id="event-result-title">Cambios registrados</h2>
                  <p>
                    {formattedDay(data.fromUtc.slice(0, 10))} —{' '}
                    {formattedDay(shiftEventDate(data.toUtc.slice(0, 10), -1)!)}
                  </p>
                </div>
                <dl className="event-total">
                  <dt>Eventos en el período</dt>
                  <dd>{number.format(data.totalEvents)}</dd>
                </dl>
              </div>
              <p className="event-scope">
                Un usuario puede generar varios eventos. Estos conteos no equivalen a personas
                únicas ni suscripciones vigentes. Las asignaciones y renovaciones registradas son operaciones manuales, sin acreditar cobros.
              </p>
              {data.totalEvents === 0 && (
                <p className="event-empty" role="status">
                  No hay movimientos registrados en este período.
                </p>
              )}
              <div className="event-breakdown">
                <table>
                  <caption>Activaciones y otros movimientos</caption>
                  <thead>
                    <tr>
                      <th scope="col">Evento</th>
                      <th scope="col">Cantidad</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.byEvent
                      .filter((row) => !row.key.startsWith('updated_'))
                      .map((row) => (
                        <tr key={row.key}>
                          <th scope="row">{labels[row.key]}</th>
                          <td>{number.format(row.count)}</td>
                        </tr>
                      ))}
                  </tbody>
                </table>
                <table>
                  <caption>Cambios administrativos de estado</caption>
                  <thead>
                    <tr>
                      <th scope="col">Cambio</th>
                      <th scope="col">Cantidad</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.byEvent
                      .filter((row) => row.key.startsWith('updated_'))
                      .map((row) => (
                        <tr key={row.key}>
                          <th scope="row">{labels[row.key]}</th>
                          <td>{number.format(row.count)}</td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </div>
              <details className="event-daily">
                <summary>
                  Ver detalle diario ({data.days.length} {data.days.length === 1 ? 'día' : 'días'})
                </summary>
                <table>
                  <caption>Eventos registrados por día UTC</caption>
                  <thead>
                    <tr>
                      <th scope="col">Fecha</th>
                      <th scope="col">Eventos</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.days.map((row) => (
                      <tr key={row.dayUtc}>
                        <th scope="row">
                          <time dateTime={row.dayUtc}>{formattedDay(row.dayUtc)}</time>
                        </th>
                        <td>
                          <span
                            className="event-bar"
                            aria-hidden="true"
                            style={{ width: (row.totalEvents / max) * 100 + '%' }}
                          />
                          <span>{number.format(row.totalEvents)}</span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </details>
              <p className="event-updated">
                Consultado:{' '}
                <time dateTime={data.generatedUtc}>
                  {instant.format(new Date(data.generatedUtc))}
                </time>{' '}
                UTC. Sólo se cuentan los eventos disponibles, sin reconstruir períodos anteriores.
              </p>
            </section>
          ) : (
            !busy &&
            !error && (
              <p className="event-intro">
                Selecciona un período y pulsa «Consultar período» para ver sus movimientos.
              </p>
            )
          )}
        </>
      ) : (
        <p className="form-alert" role="alert">
          Tu cuenta no tiene permiso para consultar los movimientos de suscripciones.
        </p>
      )}
    </div>
  );
}
