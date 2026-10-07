import { StrictMode, type ReactNode } from 'react';
import { createMemoryRouter } from 'react-router-dom';
import { SessionProvider } from '../auth/SessionProvider';
import { render } from '@testing-library/react';
import { afterEach } from 'vitest';
import App, { AppRoutes } from '../App';
import { createAppRouter } from '../router';
const routers = new Set<ReturnType<typeof createAppRouter>>();
afterEach(() => {
  for (const router of routers) router.dispose();
  routers.clear();
});
export function renderApp(strict = false) {
  const router = createAppRouter();
  routers.add(router);
  const application = <App router={router} />;
  return { ...render(strict ? <StrictMode>{application}</StrictMode> : application), router };
}

export function renderMemoryApp(path: string, element: ReactNode = <AppRoutes />) {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: <SessionProvider>{element}</SessionProvider>,
      },
    ],
    { initialEntries: [path] },
  );
  routers.add(router);
  return { ...render(<App router={router} />), router };
}
