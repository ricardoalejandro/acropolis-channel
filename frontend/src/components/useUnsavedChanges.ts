import { useCallback, useEffect, useRef } from 'react';
import { useBlocker } from 'react-router-dom';
export function useUnsavedChanges(dirty: boolean) {
  const permitted = useRef(false);
  const shouldBlock = useCallback(() => {
    if (permitted.current) {
      permitted.current = false;
      return false;
    }
    return dirty;
  }, [dirty]);
  const blocker = useBlocker(shouldBlock);
  useEffect(() => {
    if (blocker.state !== 'blocked') return;
    if (window.confirm('Tienes cambios sin guardar. ¿Salir y descartarlos?')) blocker.proceed();
    else blocker.reset();
  }, [blocker]);
  useEffect(() => {
    if (!dirty) return;
    function beforeUnload(event: BeforeUnloadEvent) {
      event.preventDefault();
      event.returnValue = '';
    }
    window.addEventListener('beforeunload', beforeUnload);
    return () => window.removeEventListener('beforeunload', beforeUnload);
  }, [dirty]);
  return useCallback(() => {
    permitted.current = true;
  }, []);
}
