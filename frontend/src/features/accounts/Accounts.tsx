import { useEffect, useState, type ReactNode } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { identity, request } from '../../api/identity';
import { useSession } from '../../auth/useSession';
import { readEmailLink } from './emailLink';
import { AccountForm, type Field } from '../../components/AccountForm';
const email: Field = {
  name: 'email',
  label: 'Correo electrónico',
  type: 'email',
  autoComplete: 'email',
};
const displayName: Field = { name: 'displayName', label: 'Nombre visible', autoComplete: 'name' };
const password: Field = {
  name: 'password',
  label: 'Contraseña',
  type: 'password',
  autoComplete: 'current-password',
};
const newPassword: Field = {
  name: 'newPassword',
  label: 'Nueva contraseña',
  type: 'password',
  autoComplete: 'new-password',
  help: 'Entre 15 y 128 caracteres. Una frase larga es una buena opción.',
};
const confirmation: Field = {
  name: 'confirmPassword',
  label: 'Confirmar contraseña',
  type: 'password',
  autoComplete: 'new-password',
};
export function AccountLayout({
  eyebrow,
  title,
  description,
  children,
}: {
  eyebrow: string;
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <section className="account-layout">
      <aside className="account-editorial" aria-label="Acrópolis Channel">
        <span className="editorial-index">APRENDER A VIVIR</span>
        <div>
          <p className="eyebrow">Filosofía para el día a día</p>
          <h2>
            La sabiduría <br />
            empieza con <br />
            <em>una pregunta.</em>
          </h2>
          <p>Un espacio para descubrir ideas, ampliar tu mirada y conectar con lo esencial.</p>
        </div>
        <span className="editorial-foot">Nueva Acrópolis · Perú</span>
      </aside>
      <div className="account-panel">
        <p className="eyebrow">{eyebrow}</p>
        <h1>{title}</h1>
        <p className="lead">{description}</p>
        {children}
      </div>
    </section>
  );
}
export function Login() {
  const { user, loading, setUser } = useSession();
  const navigate = useNavigate();
  if (!loading && user) return <Navigate to="/profile" replace />;
  return (
    <AccountLayout
      eyebrow="Tu espacio"
      title="Qué bueno verte."
      description="Ingresa para continuar en Acrópolis Channel."
    >
      <AccountForm
        fields={[email, password]}
        submit="Ingresar"
        onSubmit={async (values) => {
          setUser(await identity.login(values['email'] as string, values['password'] as string));
          navigate('/profile', { replace: true });
        }}
      >
        <Link className="text-link form-secondary" to="/forgot-password">
          ¿Olvidaste tu contraseña?
        </Link>
      </AccountForm>
      <p className="account-switch">
        ¿Aún no tienes cuenta? <Link to="/register">Crear una cuenta</Link>
      </p>
      <p className="account-note">
        ¿Te falta confirmar el correo? <Link to="/email-pending">Solicitar un nuevo enlace</Link>
      </p>
    </AccountLayout>
  );
}
export function Register() {
  const navigate = useNavigate();
  return (
    <AccountLayout
      eyebrow="Bienvenido a Acrópolis"
      title="Abre tu mirada."
      description="Crea tu cuenta y comienza a formar parte de este espacio."
    >
      <AccountForm
        fields={[
          displayName,
          email,
          { ...password, autoComplete: 'new-password', help: 'Entre 15 y 128 caracteres.' },
          confirmation,
        ]}
        submit="Crear cuenta"
        onSubmit={async (values) => {
          await request('/identity/register', {
            displayName: values['displayName'],
            email: values['email'],
            password: values['password'],
          });
          navigate('/email-pending');
        }}
      />
      <p className="account-switch">
        ¿Ya tienes cuenta? <Link to="/login">Ingresar</Link>
      </p>
    </AccountLayout>
  );
}
export function EmailPending() {
  const [sent, setSent] = useState(false);
  return (
    <AccountLayout
      eyebrow="Un paso más"
      title="Revisa tu correo."
      description="Para ingresar necesitas confirmar tu correo electrónico. Revisa también la carpeta de correo no deseado."
    >
      <p className="quiet-note">
        Si corresponde, recibirás un correo con las instrucciones. Por seguridad, no indicamos si
        una dirección ya tiene una cuenta.
      </p>
      {sent && (
        <p className="success-message" role="status">
          Si corresponde, recibirás un nuevo enlace. Revisa tu correo.
        </p>
      )}
      <AccountForm
        fields={[email]}
        submit="Reenviar enlace"
        onSubmit={async (values) => {
          await request('/identity/resend-confirmation', { email: values['email'] });
          setSent(true);
        }}
      />
      <p className="account-switch">
        <Link to="/login">Volver a ingresar</Link>
      </p>
    </AccountLayout>
  );
}
export function ForgotPassword() {
  const [sent, setSent] = useState(false);
  return (
    <AccountLayout
      eyebrow="Recupera tu acceso"
      title="Volvamos a empezar."
      description="Escribe tu correo y te enviaremos las instrucciones para recuperar el acceso."
    >
      {sent ? (
        <div className="completion" role="status">
          <span aria-hidden="true">✓</span>
          <h2>Revisa tu correo</h2>
          <p>Si corresponde, recibirás un correo con las instrucciones.</p>
          <Link className="button button-outline" to="/login">
            Volver a ingresar
          </Link>
        </div>
      ) : (
        <AccountForm
          fields={[email]}
          submit="Enviar instrucciones"
          onSubmit={async (values) => {
            await request('/identity/forgot-password', { email: values['email'] });
            setSent(true);
          }}
        />
      )}
    </AccountLayout>
  );
}
export function EmailAction({ reset = false }: { reset?: boolean }) {
  const [link] = useState(readEmailLink);
  const [done, setDone] = useState(false);
  useEffect(() => {
    window.history.replaceState(window.history.state, '', window.location.pathname);
  }, []);
  return (
    <AccountLayout
      eyebrow={reset ? 'Recupera tu acceso' : 'Confirmación de correo'}
      title={reset ? 'Una nueva contraseña.' : 'Confirma tu correo.'}
      description={
        reset
          ? 'Elige una contraseña larga y personal para volver a ingresar.'
          : 'Confirma esta dirección para terminar de crear tu cuenta.'
      }
    >
      {done ? (
        <div className="completion" role="status">
          <span aria-hidden="true">✓</span>
          <h2>{reset ? 'Contraseña actualizada' : 'Correo confirmado'}</h2>
          <p>Ya puedes ingresar con tus datos.</p>
          <Link className="button" to="/login">
            Ingresar
          </Link>
        </div>
      ) : !link.userId || !link.token ? (
        <div className="form-alert" role="alert">
          <p>Este enlace no es válido o ha caducado. Solicita uno nuevo.</p>
          <Link to={reset ? '/forgot-password' : '/email-pending'}>Solicitar un nuevo enlace</Link>
        </div>
      ) : (
        <AccountForm
          fields={reset ? [newPassword, confirmation] : []}
          submit={reset ? 'Restablecer contraseña' : 'Confirmar mi correo'}
          onSubmit={async (values) => {
            await request(reset ? '/identity/reset-password' : '/identity/confirm-email', {
              ...link,
              ...(reset ? { newPassword: values['newPassword'] } : {}),
            });
            setDone(true);
          }}
        />
      )}
      {!done && (
        <p className="account-note">
          <Link to={reset ? '/forgot-password' : '/email-pending'}>Solicitar un nuevo enlace</Link>
        </p>
      )}
    </AccountLayout>
  );
}
