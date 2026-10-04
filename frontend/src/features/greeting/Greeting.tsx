import { useEffect, useState } from 'react';
import { getGreeting, GreetingError } from '../../api/greeting';

type State =
  | { phase: 'loading' }
  | { phase: 'success'; message: string }
  | { phase: 'error'; message: string };

export function Greeting() {
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<State>({ phase: 'loading' });

  useEffect(() => {
    const controller = new AbortController();
    void getGreeting({ signal: controller.signal })
      .then(({ message }) => {
        if (!controller.signal.aborted) setState({ phase: 'success', message });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        const message =
          error instanceof GreetingError
            ? error.message
            : 'No pudimos cargar el saludo. Inténtalo nuevamente.';
        setState({ phase: 'error', message });
      });
    return () => controller.abort();
  }, [attempt]);

  function retry() {
    setState({ phase: 'loading' });
    setAttempt((value) => value + 1);
  }

  return (
    <section
      className="greeting-card"
      aria-labelledby="greeting-title"
      aria-busy={state.phase === 'loading'}
    >
      <p className="eyebrow">Acrópolis Channel</p>
      <h2 id="greeting-title">Un nuevo comienzo</h2>
      <div className="greeting-content">
        {state.phase === 'loading' && (
          <p role="status" className="loading">
            Cargando…
          </p>
        )}
        {state.phase === 'success' && (
          <div role="status">
            <p className="greeting-message">{state.message}</p>
            <p className="card-copy">Bienvenido a este espacio de encuentro.</p>
          </div>
        )}
        {state.phase === 'error' && (
          <>
            <p role="alert" className="error-message">
              {state.message}
            </p>
            <button type="button" onClick={retry}>
              Reintentar
            </button>
          </>
        )}
      </div>
      <div className="card-decoration" aria-hidden="true">
        <span />
        <span />
        <span />
      </div>
    </section>
  );
}
