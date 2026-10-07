import { useState, type ReactNode } from 'react';
import { Link, NavLink, useLocation, useNavigate } from 'react-router-dom';
import { identity } from '../api/identity';
import { useSession } from '../auth/useSession';
import { Brand } from './Brand';
import { Icon } from './Icon';
import { administrativeLinks as adminLinks } from '../auth/permissions';
export function Shell({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const administrative = pathname.startsWith('/admin');
  const { user, loading, notice, setUser } = useSession();
  const navigate = useNavigate();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [menu, setMenu] = useState(false);
  const links = adminLinks.filter((link) => user?.permissions.includes(link.permission));
  async function logout() {
    setBusy(true);
    setError('');
    try {
      await identity.logout();
      setUser(null, 'Cerraste tu sesión correctamente.');
      navigate('/login', { replace: true });
    } catch {
      setError('No pudimos cerrar tu sesión. Inténtalo de nuevo.');
    } finally {
      setBusy(false);
    }
  }
  const logoutButton = (
    <button className="link-button" disabled={busy} onClick={() => void logout()}>
      {busy ? 'Cerrando…' : 'Cerrar sesión'}
    </button>
  );
  const notifications = (
    <>
      {notice && (
        <p className="global-notice" role="status">
          {notice}
        </p>
      )}
      {error && (
        <p className="global-notice" role="alert">
          {error}
        </p>
      )}
    </>
  );
  const skip = (
    <a
      className="skip-link"
      href="#main"
      onClick={(event) => {
        event.preventDefault();
        document.getElementById('main')?.focus();
      }}
    >
      Saltar al contenido
    </a>
  );
  if (administrative)
    return (
      <div className="admin-shell">
        {skip}
        <aside className="admin-sidebar" aria-label="Navegación administrativa">
          <Brand />
          <p className="admin-label">Administración</p>
          <button
            className="admin-menu button button-outline"
            aria-expanded={menu}
            aria-controls="admin-navigation"
            onClick={() => setMenu(!menu)}
          >
            <Icon name="menu" /> Menú de administración
          </button>
          <nav id="admin-navigation" aria-label="Administración" data-open={menu}>
            <NavLink to="/admin" end onClick={() => setMenu(false)}>
              <Icon name="grid" /> Inicio
            </NavLink>
            {links.map((link) => (
              <NavLink key={link.path} to={link.path} onClick={() => setMenu(false)}>
                <Icon name={link.icon} />
                {link.label}
              </NavLink>
            ))}
            {links.length > 0 && (
              <NavLink to="/admin/reports" onClick={() => setMenu(false)}>
                <Icon name="grid" />
                Reportes
              </NavLink>
            )}
            {links.length > 0 && (
              <NavLink to="/admin/audit" onClick={() => setMenu(false)}>
                <Icon name="grid" />
                Auditoría
              </NavLink>
            )}
          </nav>
          <div className="admin-sidebar-foot">
            <Link to="/explore">Volver a la mediateca</Link>
            <Link to="/profile">Mi perfil</Link>
            {user && logoutButton}
          </div>
        </aside>
        <div className="admin-workspace">
          <header className="admin-topbar">
            <span>Espacio de gestión</span>
            {user && (
              <span>
                {user.displayName}
                {user.isOwner && <span className="tag">Propietario</span>}
              </span>
            )}
          </header>
          {notifications}
          <main id="main" tabIndex={-1}>
            {children}
          </main>
        </div>
      </div>
    );
  return (
    <div className="app-shell">
      {skip}
      <header className="site-header">
        <div className="header-inner">
          <Brand />
          <button
            className="public-menu link-button"
            aria-expanded={menu}
            aria-controls="public-navigation"
            onClick={() => setMenu(!menu)}
          >
            <Icon name="menu" /> Menú
          </button>
          <nav id="public-navigation" aria-label="Navegación principal" data-open={menu}>
            <NavLink to="/" end onClick={() => setMenu(false)}>
              Inicio
            </NavLink>
            <NavLink to="/explore" onClick={() => setMenu(false)}>
              Explorar
            </NavLink>
            {!loading &&
              (user ? (
                <>
                  <NavLink to="/profile" onClick={() => setMenu(false)}>
                    Mi perfil
                  </NavLink>
                  <NavLink to="/profile/subscription" onClick={() => setMenu(false)}>
                    Mi suscripción
                  </NavLink>
                  {links.length > 0 && (
                    <Link to="/admin" onClick={() => setMenu(false)}>
                      {user.permissions.includes('Users.Manage')
                        ? 'Administración'
                        : user.permissions.includes('Content.Manage')
                          ? 'Gestionar contenidos'
                          : 'Gestionar suscripciones'}
                    </Link>
                  )}
                  {logoutButton}
                </>
              ) : (
                <Link className="button button-small" to="/login" onClick={() => setMenu(false)}>
                  Ingresar
                </Link>
              ))}
          </nav>
        </div>
      </header>
      {notifications}
      <main id="main" tabIndex={-1}>
        {children}
      </main>
      <footer className="site-footer">
        <div className="footer-inner">
          <Brand />
          <p>
            Filosofía, cultura y voluntariado.
            <br />
            Nueva Acrópolis · Perú
          </p>
          <nav aria-label="Pie de página">
            <Link to="/explore">Explorar la mediateca</Link>
            <Link to="/profile/subscription">Acceso gratuito</Link>
          </nav>
        </div>
      </footer>
    </div>
  );
}
