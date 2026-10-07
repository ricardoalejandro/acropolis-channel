import { Link } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { administrativeLinks as adminLinks } from '../../auth/permissions';
import { Icon } from '../../components/Icon';
export function AdminHome() {
  const { user } = useSession();
  const links = adminLinks.filter((link) => user?.permissions.includes(link.permission));
  return (
    <div className="workspace">
      <div className="page-heading">
        <h1>Administración</h1>
        <p className="lead">Elige la tarea que necesitas realizar.</p>
      </div>
      {links.length ? (
        <>
          <div className="admin-task-grid">
            {links.map((link) => (
              <Link className="surface admin-task" to={link.path} key={link.path}>
                <Icon name={link.icon} />
                <h2>{link.label}</h2>
                <p>
                  {link.path === '/admin/topics'
                    ? 'Mantener temas y organizar la exploración del catálogo.'
                    : link.permission === 'Users.Manage'
                      ? user?.isOwner
                        ? 'Buscar cuentas, gestionar niveles y delegar permisos.'
                        : 'Buscar cuentas y gestionar sus datos y niveles.'
                      : link.permission === 'Content.Manage'
                        ? 'Preparar obras, ordenar colecciones y publicar el catálogo.'
                        : 'Gestionar el acceso gratuito y consultar su historial.'}
                </p>
              </Link>
            ))}
          </div>
          <div className="admin-report-link">
            <Link className="text-link" to="/admin/reports">
              Consultar reportes
            </Link>
            <p>Revisar los conteos actuales de las áreas que gestionas.</p>
          </div>
        </>
      ) : (
        <p className="form-alert">Tu cuenta no tiene permisos administrativos.</p>
      )}
    </div>
  );
}
