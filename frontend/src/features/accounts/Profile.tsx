import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { clearCsrf, identity, request } from '../../api/identity';
import { useSession } from '../../auth/useSession';
import { AccountForm } from '../../components/AccountForm';
export function Profile() {
  const { user, setUser } = useSession();
  const navigate = useNavigate();
  const [saved, setSaved] = useState(false);
  if (!user) return null;
  return (
    <div className="workspace profile-workspace">
      <div className="page-heading">
        <p className="eyebrow">Tu cuenta</p>
        <h1>Mi perfil</h1>
        <p className="lead">Gestiona tus datos y la seguridad de tu cuenta.</p>
      </div>
      <div className="profile-grid">
        <aside className="profile-card">
          <span className="avatar" aria-hidden="true">
            {user.displayName.charAt(0).toUpperCase()}
          </span>
          <h2>{user.displayName}</h2>
          <p className="wrap-anywhere">{user.email}</p>
          <div className="tag-list">
            {user.levels.map((level) => (
              <span className="tag" key={level}>
                {level}
              </span>
            ))}
          </div>
          <p className="field-help">
            Los niveles institucionales son administrados por Nueva Acrópolis.
          </p>
        </aside>
        <div className="profile-sections">
          <section className="surface">
            <h2>Mi suscripción</h2>
            <p className="section-copy">
              Activa o gestiona tu acceso gratuito a las obras de la mediateca.
            </p>
            <Link className="button button-outline" to="/profile/subscription">
              Ver mi suscripción
            </Link>
          </section>
          <section className="surface" aria-labelledby="profile-data">
            <p className="eyebrow">Datos personales</p>
            <h2 id="profile-data">Datos personales</h2>
            {saved && (
              <p className="success-message" role="status">
                Guardamos tus cambios.
              </p>
            )}
            <AccountForm
              fields={[
                {
                  name: 'displayName',
                  label: 'Nombre visible',
                  autoComplete: 'name',
                  initial: user.displayName,
                },
              ]}
              submit="Guardar cambios"
              onSubmit={async (values) => {
                setUser(await identity.updateProfile(values['displayName'] as string));
                setSaved(true);
              }}
            />
          </section>
          <section className="surface" aria-labelledby="profile-security">
            <p className="eyebrow">Seguridad</p>
            <h2 id="profile-security">Contraseña</h2>
            <p className="section-copy">
              Al cambiar la contraseña se cerrarán todas tus sesiones. Después podrás ingresar con
              la nueva.
            </p>
            <AccountForm
              fields={[
                {
                  name: 'currentPassword',
                  label: 'Contraseña actual',
                  type: 'password',
                  autoComplete: 'current-password',
                },
                {
                  name: 'newPassword',
                  label: 'Nueva contraseña',
                  type: 'password',
                  autoComplete: 'new-password',
                  help: 'Entre 15 y 128 caracteres. Una frase larga es una buena opción.',
                },
                {
                  name: 'confirmPassword',
                  label: 'Confirmar contraseña',
                  type: 'password',
                  autoComplete: 'new-password',
                },
              ]}
              submit="Cambiar contraseña"
              onSubmit={async (values) => {
                await request('/identity/change-password', {
                  currentPassword: values['currentPassword'],
                  newPassword: values['newPassword'],
                });
                clearCsrf();
                setUser(null, 'Contraseña actualizada. Ingresa de nuevo con tu nueva contraseña.');
                navigate('/login', { replace: true });
              }}
            />
          </section>
          <section className="surface" aria-labelledby="profile-consumption">
            <h2 id="profile-consumption">Registro de consumo</h2>
            <p className="section-copy">Cuando está habilitado, el registro de consumo conserva el detalle vinculado a tu cuenta durante 90 fechas UTC, incluida la actual, y estadísticas generales por obra y día durante 1 año (365 fechas UTC). Son observaciones del navegador; el tiempo registrado puede solaparse entre sesiones. El detalle sólo puede consultarse desde el backoffice con los permisos y la verificación en dos pasos necesarios.</p>
          </section>
          <section className="surface" aria-labelledby="profile-mfa">
            <p className="eyebrow">Verificación en dos pasos</p>
            <h2 id="profile-mfa">Verificación en dos pasos</h2>
            <p className="section-copy">
              Añade un código de tu autenticador y conserva códigos de recuperación para cuando los
              necesites.
            </p>
            <Link className="text-link mfa-profile-link" to="/profile/security">
              Verificación en dos pasos
            </Link>
          </section>
        </div>
      </div>
    </div>
  );
}
