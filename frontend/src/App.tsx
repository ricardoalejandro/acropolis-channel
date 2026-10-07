import { useEffect, type ReactNode } from 'react';
import {
  RouterProvider,
  type RouterProviderProps,
  Link,
  Navigate,
  Route,
  Routes,
  useLocation,
} from 'react-router-dom';
import { useSession } from './auth/useSession';
import { Shell } from './components/Shell';
import {
  Login,
  Register,
  EmailPending,
  ForgotPassword,
  EmailAction,
} from './features/accounts/Accounts';
import { MfaSecurity } from './features/accounts/Mfa';
import { Profile } from './features/accounts/Profile';
import { UserDetail, UserList } from './features/admin/Admin';
import { AdminHome } from './features/admin/AdminHome';
import { ReportsPage } from './features/admin/Reports';
import {
  AccountConsumptionPage,
  ConsumptionReportsPage,
} from './features/admin/ConsumptionReports';
import { SubscriptionEventsPage } from './features/admin/SubscriptionEvents';
import { AuditPage } from './features/admin/Audit';
import { SubscriptionPage } from './features/subscriptions/Subscription';
import { SubscriptionAccountAssignment } from './features/subscriptions/SubscriptionPlanAssignment';
import {
  SubscriptionAdminList,
  SubscriptionAdminDetail,
} from './features/subscriptions/SubscriptionAdmin';
import { Home } from './features/home/Home';
import { Explore, ContentPage } from './features/catalog/Catalog';
import { ContentAdminList, ContentEditor } from './features/catalog/ContentAdmin';
import { TopicList, TopicEditor, ContentTopicsEditor } from './features/catalog/TopicsAdmin';
function RouteFocus() {
  const { pathname } = useLocation();
  useEffect(() => {
    const heading = document.querySelector<HTMLHeadingElement>('main h1');
    if (heading) {
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
    } else {
      document.querySelector<HTMLElement>('main')?.focus({ preventScroll: true });
    }
    window.scrollTo(0, 0);
  }, [pathname]);
  return null;
}
export function Protected({
  children,
  admin = false,
  permission,
}: {
  children: ReactNode;
  admin?: boolean;
  permission?: string;
}) {
  const { user, loading } = useSession();
  if (loading)
    return (
      <p className="route-loading" role="status">
        Comprobando tu sesión…
      </p>
    );
  if (!user) return <Navigate to="/login" replace />;
  if ((admin || permission) && !user.permissions.includes(permission ?? 'Users.Manage'))
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
        <Route path="/explore" element={<Explore />} />
        <Route path="/content/:slug" element={<ContentPage />} />
        <Route
          path="/admin/content"
          element={
            <Protected permission="Content.Manage">
              <ContentAdminList />
            </Protected>
          }
        />
        <Route
          path="/admin/content/new"
          element={
            <Protected permission="Content.Manage">
              <ContentEditor />
            </Protected>
          }
        />
        <Route
          path="/admin/content/:id"
          element={
            <Protected permission="Content.Manage">
              <ContentEditor />
            </Protected>
          }
        />
        <Route
          path="/admin/topics"
          element={
            <Protected permission="Content.Manage">
              <TopicList />
            </Protected>
          }
        />
        <Route
          path="/admin/topics/new"
          element={
            <Protected permission="Content.Manage">
              <TopicEditor />
            </Protected>
          }
        />
        <Route
          path="/admin/topics/:id"
          element={
            <Protected permission="Content.Manage">
              <TopicEditor />
            </Protected>
          }
        />
        <Route
          path="/admin/content/:id/topics"
          element={
            <Protected permission="Content.Manage">
              <ContentTopicsEditor />
            </Protected>
          }
        />
        <Route path="/profile/subscription" element={<SubscriptionPage />} />
        <Route
          path="/admin"
          element={
            <Protected>
              <AdminHome />
            </Protected>
          }
        />
        <Route
          path="/admin/reports"
          element={
            <Protected>
              <ReportsPage />
            </Protected>
          }
        />
        <Route
          path="/admin/reports/consumption"
          element={
            <Protected permission="Content.Manage">
              <ConsumptionReportsPage />
            </Protected>
          }
        />
        <Route
          path="/admin/users/:id/consumption"
          element={
            <Protected permission="Users.Manage">
              <AccountConsumptionPage />
            </Protected>
          }
        />
        <Route
          path="/admin/reports/subscription-events"
          element={
            <Protected permission="Subscriptions.Manage">
              <SubscriptionEventsPage />
            </Protected>
          }
        />
        <Route
          path="/admin/audit"
          element={
            <Protected>
              <AuditPage />
            </Protected>
          }
        />
        <Route
          path="/admin/subscriptions"
          element={
            <Protected permission="Subscriptions.Manage">
              <SubscriptionAdminList />
            </Protected>
          }
        />
        <Route
          path="/admin/subscriptions/accounts/:userId"
          element={
            <Protected permission="Subscriptions.Manage">
              <SubscriptionAccountAssignment />
            </Protected>
          }
        />
        <Route
          path="/admin/subscriptions/:id"
          element={
            <Protected permission="Subscriptions.Manage">
              <SubscriptionAdminDetail />
            </Protected>
          }
        />
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route path="/email-pending" element={<EmailPending />} />
        <Route path="/confirm-email" element={<EmailAction key="confirm" />} />
        <Route path="/forgot-password" element={<ForgotPassword />} />
        <Route path="/reset-password" element={<EmailAction key="reset" reset />} />
        <Route path="/profile/security" element={<MfaSecurity />} />
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
export default function App({ router }: { router: RouterProviderProps['router'] }) {
  return <RouterProvider router={router} />;
}
