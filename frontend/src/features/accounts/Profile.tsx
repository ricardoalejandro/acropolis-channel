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
        <h1>Hola, {user.displayName.split(' ')[0]}.</h1>
        <p className="lead">Un espacio personal para seguir creciendo.</p>
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
          <section className="surface" aria-labelledby="profile-data">
            <p className="eyebrow">01 · Datos personales</p>
            <h2 id="profile-data">Así te presentamos</h2>
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
            <p className="eyebrow">02 · Seguridad</p>
            <h2 id="profile-security">Cuida tu acceso</h2>
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
          <section className="surface" aria-labelledby="profile-mfa">
            <p className="eyebrow">03 · Verificación en dos pasos</p>
            <h2 id="profile-mfa">Protege tu cuenta.</h2>
            <p className="section-copy">
              Añade un código de tu autenticador y conserva códigos de recuperación para cuando los
              necesites.
            </p>
            <Link className="text-link mfa-profile-link" to="/profile/security">
              Verificación en dos pasos <span aria-hidden="true">→</span>
            </Link>
          </section>
        </div>
      </div>
    </div>
  );
}
