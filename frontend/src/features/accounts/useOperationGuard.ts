import { useCallback, useEffect, useRef } from 'react';
export function useOperationGuard() {
  const revision = useRef(0);
  const mounted = useRef(false);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      revision.current += 1;
    };
  }, []);
  const invalidate = useCallback(() => {
    revision.current += 1;
  }, []);
  const begin = useCallback(() => {
    const current = (revision.current += 1);
    return () => mounted.current && current === revision.current;
  }, []);
  return { begin, invalidate };
}
