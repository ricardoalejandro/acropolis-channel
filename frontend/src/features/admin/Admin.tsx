import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  ApiError,
  identity,
  levels,
  type Level,
  type User,
  type UserPage,
} from '../../api/identity';
import { useSession } from '../../auth/useSession';
const statusLabels: Record<string, string> = {
  pending: 'Pendiente',
  active: 'Activo',
  disabled: 'Deshabilitado',
};
export function UserList() {
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [level, setLevel] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<UserPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [query, setQuery] = useState({ search: '', status: '', level: '', page: 1 });
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true;
    void identity
      .users(query.search, query.status, query.level, query.page)
      .then((result) => {
        if (current) {
          setData(result);
          setLoading(false);
        }
      })
      .catch((failure: unknown) => {
        if (current) {
          setError(
            failure instanceof ApiError ? failure.message : 'No pudimos cargar los usuarios.',
          );
          setLoading(false);
        }
      });
    return () => {
      current = false;
    };
  }, [query, retry]);
  function filter(nextPage = 1) {
    setLoading(true);
    setError('');
    setPage(nextPage);
    setQuery({ search: search.trim(), status, level, page: nextPage });
  }
  return (
    <div className="workspace admin-workspace">
      <div className="page-heading">
        <p className="eyebrow">Administración</p>
        <h1>Personas que conectan.</h1>
        <p className="lead">Gestiona las cuentas y sus niveles institucionales.</p>
      </div>
      <section className="surface users-surface" aria-label="Usuarios">
        <form
          className="filters"
          onSubmit={(event) => {
            event.preventDefault();
            filter();
          }}
        >
          <div className="field">
            <label htmlFor="user-search">Buscar usuarios</label>
            <input
              id="user-search"
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Nombre o correo"
            />
          </div>
          <div className="field">
            <label htmlFor="status-filter">Estado</label>
            <select
              id="status-filter"
              value={status}
              onChange={(event) => setStatus(event.target.value)}
            >
              <option value="">Todos</option>
              {Object.entries(statusLabels).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor="level-filter">Nivel institucional</label>
            <select
              id="level-filter"
              value={level}
              onChange={(event) => setLevel(event.target.value)}
            >
              <option value="">Todos</option>
              {levels.map((item) => (
                <option key={item}>{item}</option>
              ))}
            </select>
          </div>
          <button className="button button-small" type="submit">
            Buscar
          </button>
        </form>
        {loading ? (
          <p className="empty-state" role="status">
            Cargando usuarios…
          </p>
        ) : error ? (
          <div className="empty-state">
            <p role="alert">{error}</p>
            <button
              className="button button-outline"
              onClick={() => {
                setLoading(true);
                setError('');
                setRetry(retry + 1);
              }}
            >
              Reintentar
            </button>
          </div>
        ) : (
          data && (
            <>
              <div className="results-count" role="status">
                {data.total} {data.total === 1 ? 'usuario' : 'usuarios'}
              </div>
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>Persona</th>
                      <th>Nivel institucional</th>
                      <th>Estado</th>
                      <th>
                        <span className="sr-only">Acciones</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((user) => (
                      <tr key={user.id}>
                        <td>
                          <strong>{user.displayName}</strong>
                          <span className="table-email">{user.email}</span>
                        </td>
                        <td>
                          <div className="tag-list">
                            {user.levels.map((item) => (
                              <span className="tag" key={item}>
                                {item}
                              </span>
                            ))}
                          </div>
                        </td>
                        <td>
                          <span className={'status-badge status-' + user.status}>
                            {statusLabels[user.status]}
                          </span>
                        </td>
                        <td>
                          <Link
                            className="text-link"
                            to={'/admin/users/' + encodeURIComponent(user.id)}
                            aria-label={'Ver usuario ' + user.displayName}
                          >
                            Ver <span aria-hidden="true">↗</span>
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {!data.items.length && (
                <p className="empty-state">No hay usuarios con estos filtros.</p>
              )}
              <nav className="pagination" aria-label="Paginación de usuarios">
                <button
                  className="button button-outline button-small"
                  disabled={page <= 1}
                  onClick={() => filter(page - 1)}
                >
                  Anterior
                </button>
                <span>Página {page}</span>
                <button
                  className="button button-outline button-small"
                  disabled={page * data.pageSize >= data.total}
                  onClick={() => filter(page + 1)}
                >
                  Siguiente
                </button>
              </nav>
            </>
          )
        )}
      </section>
    </div>
  );
}
export function UserDetail() {
  const { user: sessionUser, setUser: setSessionUser } = useSession();
  const navigate = useNavigate();
  const { id = '' } = useParams();
  const [user, setUser] = useState<User | null>(null);
  const [name, setName] = useState('');
  const [status, setStatus] = useState('pending');
  const [selected, setSelected] = useState<Level[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const loadRevision = useRef(0);
  const load = useCallback(() => {
    const revision = (loadRevision.current += 1);
    return identity
      .user(id)
      .then((result) => {
        if (revision !== loadRevision.current) return;
        setError('');
        setSaved(false);
        setUser(result);
        setName(result.displayName);
        setStatus(result.status);
        setSelected(result.levels);
      })
      .catch((failure: unknown) => {
        if (revision === loadRevision.current)
          setError(failure instanceof ApiError ? failure.message : 'No pudimos cargar el usuario.');
      });
  }, [id]);
  useEffect(() => {
    void load();
    return () => {
      loadRevision.current += 1;
    };
  }, [load]);
  async function save() {
    if (!user || user.id !== id || busy) return;
    if (name.trim().length < 2 || name.trim().length > 100) {
      setError('El nombre debe tener entre 2 y 100 caracteres.');
      return;
    }
    setBusy(true);
    setError('');
    setSaved(false);
    try {
      const updated = await identity.updateUser(id, {
        version: user.version,
        displayName: name.trim(),
        levels: selected,
        ...(status !== 'pending' ? { status } : {}),
      });
      setUser(updated);
      setName(updated.displayName);
      setStatus(updated.status);
      setSelected(updated.levels);
      setSaved(true);
      if (updated.id === sessionUser?.id) {
        setSessionUser(null, 'Actualizamos tu cuenta. Ingresa de nuevo para continuar.');
        navigate('/login', { replace: true });
      }
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'No pudimos guardar los cambios.');
    } finally {
      setBusy(false);
    }
  }
  const visibleUser = user?.id === id ? user : null;
  return (
    <div className="workspace">
      <Link className="text-link back-link" to="/admin/users">
        ← Volver a usuarios
      </Link>
      <div className="page-heading">
        <p className="eyebrow">Administración · Cuenta</p>
        <h1>{visibleUser ? visibleUser.displayName : 'Detalle de usuario'}</h1>
      </div>
      {error && (
        <div className="form-alert" role="alert">
          {error}
          <button className="link-button" onClick={() => void load()}>
            Recargar datos
          </button>
        </div>
      )}
      {!visibleUser ? (
        !error && <p role="status">Cargando usuario…</p>
      ) : (
        <div className="detail-grid">
          <aside className="profile-card">
            <span className="avatar" aria-hidden="true">
              {visibleUser.displayName.charAt(0).toUpperCase()}
            </span>
            <h2>{visibleUser.displayName}</h2>
            <p className="wrap-anywhere">{visibleUser.email}</p>
            <span className={'status-badge status-' + visibleUser.status}>
              {statusLabels[visibleUser.status]}
            </span>
            <p className="field-help">
              Correo {visibleUser.emailConfirmed ? 'confirmado' : 'pendiente de confirmar'}
            </p>
            {visibleUser.permissions.includes('Users.Manage') && (
              <p className="admin-permission">Permiso administrativo: gestión de usuarios.</p>
            )}
            <p className="field-help">
              Los permisos administrativos se gestionan mediante un procedimiento operativo
              separado.
            </p>
          </aside>
          <section className="surface">
            <h2>Datos y acceso</h2>
            <p className="section-copy">
              El nivel institucional y los permisos administrativos son independientes.
            </p>
            {saved && (
              <p className="success-message" role="status">
                Guardamos los cambios del usuario.
              </p>
            )}
            <form
              className="account-form"
              onSubmit={(event) => {
                event.preventDefault();
                void save();
              }}
              aria-busy={busy}
            >
              <div className="field">
                <label htmlFor="admin-name">Nombre visible</label>
                <input
                  id="admin-name"
                  value={name}
                  disabled={busy}
                  onChange={(event) => setName(event.target.value)}
                />
              </div>
              <div className="field">
                <label htmlFor="admin-status">Estado de la cuenta</label>
                <select
                  id="admin-status"
                  disabled={busy}
                  value={status}
                  onChange={(event) => setStatus(event.target.value)}
                >
                  {status === 'pending' && (
                    <option value="pending" disabled>
                      Pendiente
                    </option>
                  )}
                  <option value="active">Activo</option>
                  <option value="disabled">Deshabilitado</option>
                </select>
                <p className="field-help">
                  Deshabilitar una cuenta revoca su acceso y sus sesiones. El correo debe
                  confirmarse antes de ingresar.
                </p>
              </div>
              <fieldset disabled={busy}>
                <legend>Niveles institucionales</legend>
                <div className="level-options">
                  {levels.map((item) => (
                    <label className="check-option" key={item}>
                      <input
                        type="checkbox"
                        checked={selected.includes(item)}
                        onChange={(event) =>
                          setSelected(
                            event.target.checked
                              ? [...selected, item]
                              : selected.filter((value) => value !== item),
                          )
                        }
                      />
                      {item}
                    </label>
                  ))}
                </div>
              </fieldset>
              <button className="button" disabled={busy} type="submit">
                {busy ? 'Guardando…' : 'Guardar usuario'}
                <span aria-hidden="true">→</span>
              </button>
            </form>
          </section>
        </div>
      )}
    </div>
  );
}
