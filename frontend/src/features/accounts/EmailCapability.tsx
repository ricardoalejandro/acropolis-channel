import { useEffect, useState, type ReactNode } from 'react';
import { identity } from '../../api/identity';
export function EmailCapability({ children }: { children: ReactNode }) {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [failed, setFailed] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    void identity.capabilities().then(
      (data) => {
        if (current) setEnabled(data.emailEnabled);
      },
      () => {
        if (current) setFailed(true);
      },
    );
    return () => {
      current = false;
    };
  }, [retry]);
  if (failed)
    return (
      <div className="form-alert" role="alert">
        <p>No pudimos comprobar la disponibilidad del correo. Inténtalo de nuevo.</p>
        <button
          className="button button-outline button-small"
          onClick={() => {
            setFailed(false);
            setRetry(retry + 1);
          }}
        >
          Reintentar
        </button>
      </div>
    );
  if (enabled === null)
    return (
      <p className="quiet-note" role="status">
        Comprobando disponibilidad…
      </p>
    );
  if (!enabled)
    return (
      <div className="quiet-note" role="status">
        <p>El registro y la recuperación por correo no están disponibles temporalmente.</p>
        <p>Si ya tienes una cuenta confirmada, puedes ingresar con normalidad.</p>
      </div>
    );
  return children;
}
