import { useEffect, useState } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import QRCode from 'qrcode';
import { ApiError, type MfaChallenge, type User } from '../../api/identity';
import { mfa, type EnableResult, type Enrollment, type MfaStatus } from '../../api/mfa';
import { useSession } from '../../auth/useSession';
import { AccountForm, type Field } from '../../components/AccountForm';
import './mfa.css';
import { useOperationGuard } from './useOperationGuard';
const codeField: Field = {
  name: 'code',
  label: 'Código del autenticador',
  autoComplete: 'one-time-code',
  help: 'Introduce los seis dígitos actuales. Si ya utilizaste ese código, espera uno nuevo.',
};
const recoveryField: Field = {
  name: 'recoveryCode',
  label: 'Código de recuperación',
  autoComplete: 'off',
  help: 'Cada código se puede utilizar una sola vez.',
};
const currentPassword: Field = {
  name: 'currentPassword',
  label: 'Contraseña actual',
  type: 'password',
  autoComplete: 'current-password',
  passwordPurpose: 'current',
};
function message(error: unknown) {
  return error instanceof ApiError
    ? error.message
    : 'No pudimos completar la acción. Inténtalo de nuevo.';
}
function AuthenticatorQr({ uri }: { uri: string }) {
  const matrix = QRCode.create(uri, { errorCorrectionLevel: 'M' }).modules;
  const path = Array.from(matrix.data, (cell, index) =>
    cell
      ? 'M' + ((index % matrix.size) + 4) + ' ' + (Math.floor(index / matrix.size) + 4) + 'h1v1h-1z'
      : '',
  ).join('');
  return (
    <svg
      className="authenticator-qr"
      width={208}
      height={208}
      viewBox={'0 0 ' + (matrix.size + 8) + ' ' + (matrix.size + 8)}
      role="img"
      aria-label="Código QR para tu aplicación de autenticación"
    >
      <rect width="100%" height="100%" fill="#fff" />
      <path d={path} fill="#16374b" />
    </svg>
  );
}
function RecoveryCodes({ codes, done }: { codes: string[]; done: () => void }) {
  return (
    <section className="mfa-codes" aria-labelledby="recovery-heading">
      <h2 id="recovery-heading">Códigos de recuperación</h2>
      <p>
        Guárdalos ahora en un lugar seguro. Se muestran una sola vez y cada código permite un único
        acceso si no tienes tu autenticador.
      </p>
      <ol aria-label="Tus códigos de recuperación">
        {codes.map((code) => (
          <li key={code}>
            <code>{code}</code>
          </li>
        ))}
      </ol>
      <p className="field-help">
        No compartas estos códigos ni los incluyas en mensajes o capturas. Al salir de esta página
        dejarán de mostrarse.
      </p>
      <button className="button" onClick={done}>
        He guardado mis códigos
      </button>
    </section>
  );
}
function EnrollmentView({
  enrollment,
  done,
  begin,
  pendingChanged,
}: {
  enrollment: Enrollment;
  done: (value: EnableResult) => void;
  begin: () => () => boolean;
  pendingChanged: (pending: boolean) => void;
}) {
  return (
    <div className="mfa-enrollment">
      <p>
        Añade Acrópolis Channel a tu aplicación de autenticación escaneando el QR o introduciendo la
        clave manual.
      </p>
      <AuthenticatorQr uri={enrollment.authenticatorUri} />
      <div className="field">
        <label htmlFor="authenticator-key">Clave del autenticador</label>
        <input
          id="authenticator-key"
          className="secret-key"
          readOnly
          value={enrollment.sharedKey}
          autoComplete="off"
        />
      </div>
      <AccountForm
        fields={[codeField]}
        submit="Activar verificación en dos pasos"
        onSubmit={async (values) => {
          const current = begin();
          pendingChanged(true);
          try {
            const result = await mfa.enable(enrollment.challengeToken, values['code'] as string);
            if (current()) done(result);
          } catch (failure) {
            if (current()) throw failure;
          } finally {
            if (current()) pendingChanged(false);
          }
        }}
      />
    </div>
  );
}
export function MfaLogin({
  challenge,
  authenticated,
  cancel,
}: {
  challenge: MfaChallenge;
  authenticated: (user: User) => void;
  cancel: (notice: string) => void;
}) {
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [result, setResult] = useState<EnableResult | null>(null);
  const [recovery, setRecovery] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const { begin, invalidate } = useOperationGuard();
  const expiry = enrollment?.expiresUtc ?? challenge.expiresUtc;
  useEffect(() => {
    if (result) return;
    const timer = setTimeout(
      () => {
        invalidate();
        cancel('Esta verificación ha caducado. Ingresa de nuevo para continuar.');
      },
      Math.max(0, Date.parse(expiry) - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [expiry, cancel, result, invalidate]);
  if (result)
    return <RecoveryCodes codes={result.recoveryCodes} done={() => authenticated(result.user)} />;
  return (
    <div className="mfa-login">
      {challenge.enrollmentRequired ? (
        <>
          <p className="quiet-note">
            Tu cuenta administra Acrópolis Channel. Configura la verificación en dos pasos para
            proteger su acceso.
          </p>
          {enrollment ? (
            <EnrollmentView
              enrollment={enrollment}
              done={setResult}
              begin={begin}
              pendingChanged={setBusy}
            />
          ) : (
            <>
              <button
                className="button"
                disabled={busy}
                onClick={() => {
                  const current = begin();
                  setBusy(true);
                  setError('');
                  void mfa
                    .enrollment({ challengeToken: challenge.challengeToken })
                    .then((value) => {
                      if (current()) setEnrollment(value);
                    })
                    .catch((failure: unknown) => {
                      if (current()) setError(message(failure));
                    })
                    .finally(() => {
                      if (current()) setBusy(false);
                    });
                }}
              >
                {busy ? 'Preparando…' : 'Configurar autenticador'}
              </button>
              {error && (
                <p className="form-alert" role="alert">
                  {error}
                </p>
              )}
            </>
          )}
        </>
      ) : (
        <>
          <AccountForm
            key={String(recovery)}
            fields={[recovery ? recoveryField : codeField]}
            submit="Verificar e ingresar"
            onSubmit={async (values) => {
              const current = begin();
              setBusy(true);
              try {
                const user = await mfa.verify(
                  challenge.challengeToken,
                  recovery
                    ? { recoveryCode: values['recoveryCode'] as string }
                    : { code: values['code'] as string },
                );
                if (current()) authenticated(user);
              } catch (failure) {
                if (current()) throw failure;
              } finally {
                if (current()) setBusy(false);
              }
            }}
          />
          <button
            className="link-button mfa-switch"
            disabled={busy}
            onClick={() => setRecovery(!recovery)}
          >
            {recovery ? 'Usar mi autenticador' : 'Usar un código de recuperación'}
          </button>
        </>
      )}
      <button
        className="link-button mfa-switch"
        disabled={busy}
        onClick={() => {
          invalidate();
          cancel('');
        }}
      >
        Volver a ingresar
      </button>
    </div>
  );
}
export function MfaSecurity() {
  const { user, loading, setUser } = useSession();
  const navigate = useNavigate();
  const [status, setStatus] = useState<MfaStatus | null>(null);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [codes, setCodes] = useState<{ values: string[]; login: boolean } | null>(null);
  const [mode, setMode] = useState<'regenerate' | 'disable' | null>(null);
  const [recovery, setRecovery] = useState(false);
  const [busy, setBusy] = useState(false);
  const { begin, invalidate } = useOperationGuard();
  const accountId = user?.id;
  useEffect(() => invalidate, [accountId, invalidate]);
  useEffect(() => {
    if (!accountId) return;
    let current = true;
    void mfa.status().then(
      (value) => {
        if (current) {
          setStatus(value);
          setError('');
        }
      },
      (failure: unknown) => {
        if (current) setError(message(failure));
      },
    );
    return () => {
      current = false;
    };
  }, [accountId, retry]);
  useEffect(() => {
    if (!enrollment || codes) return;
    const timer = setTimeout(
      () => {
        invalidate();
        setBusy(false);
        setEnrollment(null);
        setError('La configuración ha caducado. Iníciala de nuevo.');
      },
      Math.max(0, Date.parse(enrollment.expiresUtc) - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [enrollment, codes, invalidate]);
  if (loading)
    return (
      <p className="route-loading" role="status">
        Comprobando tu sesión…
      </p>
    );
  if (!user && !codes) return <Navigate to="/login" replace />;
  return (
    <div className="workspace mfa-security">
      <Link className="text-link back-link" to={user ? '/profile' : '/login'}>
        ← {user ? 'Mi perfil' : 'Volver a ingresar'}
      </Link>
      <div className="page-heading">
        <p className="eyebrow">SEGURIDAD DE TU CUENTA</p>
        <h1>Un paso más de seguridad.</h1>
        <p className="lead">Una contraseña y un código personal para proteger tu acceso.</p>
      </div>
      <div className="surface mfa-surface">
        {codes ? (
          <RecoveryCodes
            codes={codes.values}
            done={() => {
              const goLogin = codes.login;
              setCodes(null);
              navigate(goLogin ? '/login' : '/profile', { replace: true });
            }}
          />
        ) : (
          <>
            {error && (
              <p className="form-alert" role="alert">
                {error}
              </p>
            )}
            {!status ? (
              error ? (
                <button
                  className="button button-outline"
                  onClick={() => {
                    setError('');
                    setRetry(retry + 1);
                  }}
                >
                  Reintentar
                </button>
              ) : (
                <p role="status">Comprobando verificación en dos pasos…</p>
              )
            ) : !status.enabled ? (
              enrollment ? (
                <EnrollmentView
                  enrollment={enrollment}
                  begin={begin}
                  pendingChanged={setBusy}
                  done={(result) => {
                    setEnrollment(null);
                    setUser(result.user);
                    setStatus({
                      ...status,
                      enabled: true,
                      recoveryCodesLeft: result.recoveryCodes.length,
                    });
                    setCodes({ values: result.recoveryCodes, login: false });
                  }}
                />
              ) : (
                <>
                  <h2>Configura tu autenticador.</h2>
                  <p className="section-copy">
                    Usa una aplicación de autenticación para generar códigos de acceso.{' '}
                    {status.required
                      ? 'Esta protección es obligatoria para administrar la plataforma.'
                      : 'La verificación en dos pasos añade protección a tu cuenta.'}
                  </p>
                  <AccountForm
                    fields={[currentPassword]}
                    submit="Configurar autenticador"
                    onSubmit={async (values) => {
                      const current = begin();
                      setBusy(true);
                      try {
                        const value = await mfa.enrollment({
                          currentPassword: values['currentPassword'] as string,
                        });
                        if (current()) setEnrollment(value);
                      } catch (failure) {
                        if (current()) throw failure;
                      } finally {
                        if (current()) setBusy(false);
                      }
                    }}
                  />
                </>
              )
            ) : (
              <>
                <h2>Tu verificación está activa.</h2>
                <p className="section-copy">
                  Conservas {status.recoveryCodesLeft}{' '}
                  {status.recoveryCodesLeft === 1
                    ? 'código de recuperación'
                    : 'códigos de recuperación'}
                  . Cada uno se puede usar una sola vez.
                </p>
                {status.required && (
                  <p className="quiet-note">
                    Las cuentas administrativas deben mantener esta protección.
                  </p>
                )}
                <div className="mfa-actions">
                  <button
                    className="button button-outline"
                    disabled={busy}
                    onClick={() => {
                      setMode('regenerate');
                      setRecovery(false);
                    }}
                  >
                    Renovar códigos de recuperación
                  </button>
                  {!status.required && (
                    <button
                      className="link-button"
                      disabled={busy}
                      onClick={() => {
                        setMode('disable');
                        setRecovery(false);
                      }}
                    >
                      Desactivar verificación en dos pasos
                    </button>
                  )}
                </div>
                {mode && (
                  <section className="mfa-reauth">
                    <h3>
                      {mode === 'regenerate'
                        ? 'Renueva tus códigos.'
                        : 'Confirma la desactivación.'}
                    </h3>
                    <p className="section-copy">
                      {mode === 'regenerate'
                        ? 'Los códigos anteriores dejarán de funcionar y se cerrarán todas tus sesiones.'
                        : 'Tu cuenta quedará protegida sólo por la contraseña y se cerrarán todas tus sesiones.'}
                    </p>
                    <AccountForm
                      key={mode + String(recovery)}
                      fields={[currentPassword, recovery ? recoveryField : codeField]}
                      submit={
                        mode === 'regenerate' ? 'Confirmar renovación' : 'Confirmar desactivación'
                      }
                      onSubmit={async (values) => {
                        const input = {
                          currentPassword: values['currentPassword'] as string,
                          ...(recovery
                            ? { recoveryCode: values['recoveryCode'] as string }
                            : { code: values['code'] as string }),
                        };
                        const current = begin();
                        setBusy(true);
                        try {
                          if (mode === 'regenerate') {
                            const result = await mfa.regenerate(input);
                            if (current()) {
                              setCodes({ values: result.recoveryCodes, login: true });
                              setUser(
                                null,
                                'Códigos renovados. Ingresa de nuevo con tu autenticador.',
                              );
                            }
                          } else {
                            await mfa.disable(input);
                            if (current()) {
                              setUser(
                                null,
                                'Verificación en dos pasos desactivada. Ingresa de nuevo.',
                              );
                              navigate('/login', { replace: true });
                            }
                          }
                        } catch (failure) {
                          if (current()) throw failure;
                        } finally {
                          if (current()) setBusy(false);
                        }
                      }}
                    />
                    <button
                      className="link-button mfa-switch"
                      disabled={busy}
                      onClick={() => setRecovery(!recovery)}
                    >
                      {recovery ? 'Usar mi autenticador' : 'Usar un código de recuperación'}
                    </button>
                    <button
                      className="link-button mfa-switch"
                      disabled={busy}
                      onClick={() => {
                        invalidate();
                        setMode(null);
                      }}
                    >
                      Cancelar
                    </button>
                  </section>
                )}
              </>
            )}
          </>
        )}
      </div>
    </div>
  );
}
