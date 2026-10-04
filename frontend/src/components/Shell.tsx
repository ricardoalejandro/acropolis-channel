import { useState, type ReactNode } from 'react';
import { Link, NavLink, useNavigate } from 'react-router-dom';
import { identity } from '../api/identity';
import { useSession } from '../auth/useSession';
import { Brand } from './Brand';
export function Shell({ children }: { children: ReactNode }) {
  const { user, loading, notice, setUser } = useSession();
  const navigate = useNavigate();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
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
  return (
    <div className="app-shell">
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
      <header className="site-header">
        <div className="header-inner">
          <Brand />
          <nav aria-label="Navegación principal">
            <NavLink className="nav-home" to="/">
              Inicio
            </NavLink>
            {!loading &&
              (user ? (
                <>
                  <NavLink to="/profile">Mi perfil</NavLink>
                  {user.permissions.includes('Users.Manage') && (
                    <NavLink to="/admin/users">Administración</NavLink>
                  )}
                  <button className="link-button" disabled={busy} onClick={() => void logout()}>
                    {busy ? 'Cerrando…' : 'Cerrar sesión'}
                  </button>
                </>
              ) : (
                <Link className="button button-small" to="/login">
                  Ingresar <span aria-hidden="true">↗</span>
                </Link>
              ))}
          </nav>
        </div>
      </header>
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
      <main id="main" tabIndex={-1}>
        {children}
      </main>
      <footer className="site-footer">
        <div className="footer-inner">
          <Brand />
          <p>
            Filosofía, cultura y voluntariado.
            <br />
            Un espacio para aprender a vivir.
          </p>
          <span>Nueva Acrópolis · Perú</span>
        </div>
      </footer>
    </div>
  );
}
