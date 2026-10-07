import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError, identity, type User } from '../api/identity';
import { SessionContext } from './SessionContext';
export function SessionProvider({ children }: { children: ReactNode }) {
  const [user, writeUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState('');
  const generation = useRef(0);
  const setUser = useCallback((value: User | null, message = '') => {
    generation.current += 1;
    writeUser(value);
    setLoading(false);
    setNotice(message);
  }, []);
  const beginUserUpdate = useCallback(() => {
    const revision = generation.current;
    return (value: User) => {
      if (revision !== generation.current) return false;
      setUser(value);
      return true;
    };
  }, [setUser]);
  useEffect(() => {
    const revision = (generation.current += 1);
    void identity
      .me()
      .then((value) => {
        if (revision === generation.current) {
          writeUser(value);
          setNotice('');
        }
      })
      .catch((error: unknown) => {
        if (revision === generation.current) {
          writeUser(null);
          if (!(error instanceof ApiError && error.status === 401))
            setNotice(
              'No pudimos comprobar tu sesión. Recarga la página para volver a intentarlo.',
            );
        }
      })
      .finally(() => {
        if (revision === generation.current) setLoading(false);
      });
    return () => {
      generation.current += 1;
    };
  }, []);
  useEffect(() => {
    const expire = () => setUser(null, 'Tu sesión ha finalizado. Ingresa de nuevo para continuar.');
    window.addEventListener('acropolis:session-expired', expire);
    return () => window.removeEventListener('acropolis:session-expired', expire);
  }, [setUser]);
  return (
    <SessionContext value={{ user, loading, notice, setUser, beginUserUpdate }}>
      {children}
    </SessionContext>
  );
}
