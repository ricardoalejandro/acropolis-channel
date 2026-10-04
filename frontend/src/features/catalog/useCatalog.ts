import { useEffect, useState } from 'react';
import { catalogMessage } from '../../api/catalog';
export function useCatalog<T>(load: () => Promise<T>, key: string) {
  const [state, setState] = useState<{
    key: string;
    data: T | null;
    loading: boolean;
    error: string;
  }>({
    key,
    data: null,
    loading: true,
    error: '',
  });
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    void load().then(
      (data) => {
        if (current) setState({ key, data, loading: false, error: '' });
      },
      (error: unknown) => {
        if (current) setState({ key, data: null, loading: false, error: catalogMessage(error) });
      },
    );
    return () => {
      current = false;
    };
  }, [load, key, retry]);
  return {
    ...(state.key === key ? state : { data: null, loading: true, error: '' }),
    reload: () => {
      setState({ key, data: null, loading: true, error: '' });
      setRetry(retry + 1);
    },
  };
}
