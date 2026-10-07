import { useEffect, useRef, useState } from 'react';
import { userAccess, userAccessMessage, type UserAccess } from '../../api/userAccess';
import './last-sign-in.css';
type State = {
  id: string;
  loading: boolean;
  record: UserAccess | null;
  failure: unknown;
  hasAction: boolean;
};
const date = new Intl.DateTimeFormat('es-PE', {
  dateStyle: 'medium',
  timeStyle: 'short',
  timeZone: 'America/Lima',
});
export function LastSignIn({ userId }: { userId: string }) {
  const [state, setState] = useState<State>({
    id: userId,
    loading: true,
    record: null,
    failure: null,
    hasAction: false,
  });
  const [attempt, setAttempt] = useState(0);
  const inFlight = useRef(true);
  useEffect(() => {
    const controller = new AbortController();
    let current = true;
    inFlight.current = true;
    void userAccess(userId, controller.signal).then(
      (record) => {
        if (current && !controller.signal.aborted) {
          inFlight.current = false;
          setState({ id: userId, loading: false, record, failure: null, hasAction: attempt > 0 });
        }
      },
      (failure: unknown) => {
        if (current && !controller.signal.aborted) {
          inFlight.current = false;
          setState({ id: userId, loading: false, record: null, failure, hasAction: true });
        }
      },
    );
    return () => {
      current = false;
      controller.abort();
    };
  }, [userId, attempt]);
  const visible =
    state.id === userId
      ? state
      : { id: userId, loading: true, record: null, failure: null, hasAction: false };
  function retry() {
    if (inFlight.current || visible.loading) return;
    inFlight.current = true;
    setState({ id: userId, loading: true, record: null, failure: null, hasAction: true });
    setAttempt((value) => value + 1);
  }
  return (
    <section
      className="last-sign-in"
      aria-labelledby="last-sign-in-heading"
      aria-describedby="last-sign-in-help"
      aria-busy={visible.loading}
    >
      <h3 id="last-sign-in-heading">Último ingreso registrado</h3>
      {visible.loading ? (
        <p role="status">Consultando último ingreso…</p>
      ) : visible.failure ? (
        <p className="form-alert" role="alert">
          {userAccessMessage(visible.failure)}
        </p>
      ) : (
        visible.record && (
          <p className="last-sign-in-value">
            {visible.record.lastSignInUtc === null ? (
              'Sin registro disponible'
            ) : (
              <time dateTime={visible.record.lastSignInUtc}>
                {date.format(new Date(visible.record.lastSignInUtc))}
              </time>
            )}
          </p>
        )
      )}
      {visible.hasAction && (
        <button
          type="button"
          className="button button-outline button-small"
          aria-disabled={visible.loading}
          onClick={retry}
        >
          {visible.failure || visible.loading
            ? 'Reintentar último ingreso'
            : 'Actualizar último ingreso'}
        </button>
      )}
      <p className="field-help" id="last-sign-in-help">
        Última nueva sesión autenticada registrada; no corresponde a la última visita ni
        reproducción.
      </p>
      {visible.record?.lastSignInUtc && <p className="field-help">Hora del Perú.</p>}
    </section>
  );
}
