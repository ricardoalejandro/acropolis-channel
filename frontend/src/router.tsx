import { createBrowserRouter } from 'react-router-dom';
import { AppRoutes } from './App';
import { SessionProvider } from './auth/SessionProvider';
export function createAppRouter() {
  return createBrowserRouter([
    {
      path: '*',
      element: (
        <SessionProvider>
          <AppRoutes />
        </SessionProvider>
      ),
      errorElement: (
        <main className="workspace">
          <h1>No pudimos abrir esta página.</h1>
          <p>Recarga la página para volver a intentarlo.</p>
          <a className="text-link" href="/">
            Volver al inicio
          </a>
        </main>
      ),
    },
  ]);
}
