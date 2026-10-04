import { useEffect, type ReactNode } from 'react';
import { BrowserRouter, Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { SessionProvider } from './auth/SessionProvider';
import { useSession } from './auth/useSession';
import { Shell } from './components/Shell';
import {
  Login,
  Register,
  EmailPending,
  ForgotPassword,
  EmailAction,
} from './features/accounts/Accounts';
import { Profile } from './features/accounts/Profile';
import { UserDetail, UserList } from './features/admin/Admin';
import { Home } from './features/home/Home';
function RouteFocus() {
  const { pathname } = useLocation();
  useEffect(() => {
    const heading = document.querySelector<HTMLHeadingElement>('main h1');
    if (heading) {
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
    }
    window.scrollTo(0, 0);
  }, [pathname]);
  return null;
}
export function Protected({ children, admin = false }: { children: ReactNode; admin?: boolean }) {
  const { user, loading } = useSession();
  if (loading)
    return (
      <p className="route-loading" role="status">
        Comprobando tu sesión…
      </p>
    );
  if (!user) return <Navigate to="/login" replace />;
  if (admin && !user.permissions.includes('Users.Manage'))
    return (
      <div className="workspace">
        <p className="eyebrow">Acceso restringido</p>
        <h1>Este espacio requiere autorización.</h1>
        <p>Tu cuenta no tiene permisos administrativos.</p>
        <Link className="text-link" to="/profile">
          Ir a mi perfil
        </Link>
      </div>
    );
  return children;
}
export function AppRoutes() {
  return (
    <Shell>
      <RouteFocus />
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route path="/email-pending" element={<EmailPending />} />
        <Route path="/confirm-email" element={<EmailAction key="confirm" />} />
        <Route path="/forgot-password" element={<ForgotPassword />} />
        <Route path="/reset-password" element={<EmailAction key="reset" reset />} />
        <Route
          path="/profile"
          element={
            <Protected>
              <Profile />
            </Protected>
          }
        />
        <Route
          path="/admin/users"
          element={
            <Protected admin>
              <UserList />
            </Protected>
          }
        />
        <Route
          path="/admin/users/:id"
          element={
            <Protected admin>
              <UserDetail />
            </Protected>
          }
        />
        <Route
          path="*"
          element={
            <div className="workspace">
              <p className="eyebrow">404</p>
              <h1>Esta página no está aquí.</h1>
              <Link className="text-link" to="/">
                Volver al inicio
              </Link>
            </div>
          }
        />
      </Routes>
    </Shell>
  );
}
export default function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <AppRoutes />
      </SessionProvider>
    </BrowserRouter>
  );
}
