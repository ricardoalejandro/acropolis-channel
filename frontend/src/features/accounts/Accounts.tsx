import { useEffect, useState, type ReactNode } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { identity, request, type MfaChallenge } from '../../api/identity';
import { useSession } from '../../auth/useSession';
import { readEmailLink } from './emailLink';
import { AccountForm, type Field } from '../../components/AccountForm';
import { EmailCapability } from './EmailCapability';
import { MfaLogin } from './Mfa';
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
  const location = useLocation();
  const routeState = location.state as { returnTo?: unknown } | null;
  const returnTo =
    typeof routeState?.returnTo === 'string' &&
    /^(?:\/content\/[a-z0-9-]+|\/profile\/subscription)$/.test(routeState.returnTo)
      ? routeState.returnTo
      : '/profile';
  const [challenge, setChallenge] = useState<MfaChallenge | null>(null);
  const [loginNotice, setLoginNotice] = useState('');
  if (!loading && user) return <Navigate to={returnTo} replace />;
  if (challenge)
    return (
      <AccountLayout
        eyebrow="Verificación de acceso"
        title="Confirma que eres tú."
        description="Completa la verificación en dos pasos para continuar."
      >
        <MfaLogin
          challenge={challenge}
          authenticated={(value) => {
            setChallenge(null);
            setUser(value);
            navigate(returnTo, { replace: true });
          }}
          cancel={(notice) => {
            setChallenge(null);
            setLoginNotice(notice);
          }}
        />
      </AccountLayout>
    );
  return (
    <AccountLayout
      eyebrow="Tu espacio"
      title="Ingresar"
      description="Ingresa para continuar en Acrópolis Channel."
    >
      {loginNotice && (
        <p className="form-alert" role="alert">
          {loginNotice}
        </p>
      )}
      <AccountForm
        fields={[email, password]}
        submit="Ingresar"
        onSubmit={async (values) => {
          const result = await identity.login(
            values['email'] as string,
            values['password'] as string,
          );
          setLoginNotice('');
          if ('mfaRequired' in result) setChallenge(result);
          else {
            setUser(result);
            navigate(returnTo, { replace: true });
          }
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
      title="Crear cuenta"
      description="Crea tu cuenta y comienza a formar parte de este espacio."
    >
      <EmailCapability>
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
            navigate('/email-pending', {
              state: { kind: 'registration', email: values['email'] },
            });
          }}
        />
      </EmailCapability>
      <p className="account-switch">
        ¿Ya tienes cuenta? <Link to="/login">Ingresar</Link>
      </p>
    </AccountLayout>
  );
}
function registrationEmailFrom(state: unknown): string | null {
  if (typeof state !== 'object' || state === null || Array.isArray(state)) return null;
  const context = state as Record<string, unknown>;
  if (context['kind'] !== 'registration' || typeof context['email'] !== 'string') return null;
  const address = context['email'].trim();
  return address.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(address) ? address : null;
}

export function EmailPending() {
  const location = useLocation();
  return (
    <PendingEmailContent
      key={location.key}
      registrationEmail={registrationEmailFrom(location.state)}
    />
  );
}

function PendingEmailContent({ registrationEmail }: { registrationEmail: string | null }) {
  const [sent, setSent] = useState(false);
  return (
    <AccountLayout
      eyebrow="Un paso más"
      title="Revisa tu correo."
      description="Para ingresar necesitas confirmar tu correo electrónico. Revisa también la carpeta de correo no deseado."
    >
      <div className="quiet-note">
        {registrationEmail && (
          <p>
            Correo utilizado:
            <strong className="pending-email-address">{registrationEmail}</strong>
          </p>
        )}
        <p>Si tu cuenta necesita confirmación, recibirás las instrucciones en esa dirección.</p>
      </div>
      {sent && (
        <p className="success-message" role="status">
          Si corresponde, recibirás un nuevo enlace. Revisa tu correo.
        </p>
      )}
      <EmailCapability>
        <AccountForm
          fields={registrationEmail ? [] : [email]}
          submit="Reenviar enlace"
          submitVariant={registrationEmail ? 'secondary' : 'primary'}
          onSubmit={async (values) => {
            setSent(false);
            await request('/identity/resend-confirmation', {
              email: registrationEmail ?? values['email'],
            });
            setSent(true);
          }}
        >
          {registrationEmail && <p className="field-help">¿No recibiste el mensaje?</p>}
          <p className="field-help">Puedes solicitar un nuevo enlace una vez por minuto.</p>
        </AccountForm>
      </EmailCapability>
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
      title="Recuperar contraseña"
      description="Escribe tu correo y te enviaremos las instrucciones para recuperar el acceso."
    >
      <EmailCapability>
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
      </EmailCapability>
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
      title={reset ? 'Nueva contraseña' : 'Confirmar correo'}
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
