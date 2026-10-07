import { useCallback, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { subscriptions, subscriptionMessage, type SubscriptionMe } from '../../api/subscriptions';
import { useCatalog } from '../catalog/useCatalog';
export function SubscriptionPage() {
  const { user, loading } = useSession();
  if (loading)
    return (
      <p className="route-loading" role="status">
        Comprobando tu sesión…
      </p>
    );
  return (
    <div className="workspace">
      <div className="page-heading">
        <h1>Tu suscripción</h1>
        <p className="lead">Acceso a lecturas, vídeos, podcasts y cursos de la mediateca.</p>
      </div>
      {!user ? (
        <section className="surface">
          <h2>Una cuenta, muchas formas de aprender.</h2>
          <p>
            Durante esta etapa, el acceso es gratuito. Crea tu cuenta, confirma el correo y activa
            tu suscripción.
          </p>
          <div className="subscription-actions">
            <Link className="button" to="/register">
              Crear mi cuenta
            </Link>
            <Link
              className="button button-outline"
              to="/login"
              state={{ returnTo: '/profile/subscription' }}
            >
              Ingresar
            </Link>
          </div>
        </section>
      ) : (
        <MySubscription key={user.id} />
      )}
    </div>
  );
}
function MySubscription() {
  const [params] = useSearchParams();
  const load = useCallback(() => subscriptions.me(), []);
  const state = useCatalog(load, 'subscription');
  const [current, setCurrent] = useState<SubscriptionMe | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [confirmCancel, setConfirmCancel] = useState(false);
  const data = current ?? state.data;
  const content = params.get('content') ?? '';
  async function act(cancel = false) {
    if (busy || !data || (cancel && !data.subscription)) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const subscription = cancel
        ? await subscriptions.cancel(data.subscription!.version)
        : await subscriptions.activate();
      setCurrent({ subscription, eligibleToActivate: subscription.status !== 'suspended' });
      setConfirmCancel(false);
      setNotice(cancel ? 'Cancelaste tu suscripción gratuita.' : 'Tu acceso gratuito está activo.');
    } catch (failure) {
      setError(subscriptionMessage(failure));
    } finally {
      setBusy(false);
    }
  }
  if (!data)
    return (
      <section className="surface">
        {state.loading ? (
          <p role="status">Consultando tu suscripción…</p>
        ) : (
          <>
            <p className="form-alert" role="alert">
              {state.error}
            </p>
            <button className="button button-outline" onClick={state.reload}>
              Reintentar
            </button>
          </>
        )}
      </section>
    );
  const subscription = data.subscription;
  return (
    <div className="subscription-summary">
      <section className="surface">
        <h2>Acceso gratuito</h2>
        {notice && (
          <p className="success-message" role="status">
            {notice}
          </p>
        )}
        {error && (
          <p className="form-alert" role="alert">
            {error}
          </p>
        )}
        <p className="subscription-consent">
          Durante esta etapa, el acceso es gratuito. Más adelante se introducirán planes de pago;
          cualquier contratación requerirá tu aceptación.
        </p>
        <p>Sin tarjeta, sin cargos y sin fecha de vencimiento para esta suscripción gratuita.</p>
        {!subscription || subscription.status === 'cancelled' ? (
          <div className="subscription-actions">
            {data.eligibleToActivate ? (
              <button className="button" disabled={busy} onClick={() => void act()}>
                {busy ? 'Activando…' : 'Suscribirme gratis'}
              </button>
            ) : (
              <p className="form-alert">
                Confirma tu correo o contacta con Nueva Acrópolis para revisar tu acceso.
              </p>
            )}
          </div>
        ) : subscription.status === 'active' ? (
          <>
            <div className="subscription-actions">
              <Link
                className="button"
                to={/^[a-z0-9]+(-[a-z0-9]+)*$/.test(content) ? '/content/' + content : '/explore'}
              >
                {content ? 'Volver al contenido' : 'Explorar la mediateca'}
              </Link>
              <button
                className="link-button"
                disabled={busy}
                onClick={() => setConfirmCancel(true)}
              >
                Cancelar suscripción
              </button>
            </div>
            {confirmCancel && (
              <div className="editor-confirmation" role="alert">
                <p>
                  Al cancelar perderás el acceso a las obras. Podrás volver a suscribirte gratis
                  durante esta etapa.
                </p>
                <button
                  className="button button-outline"
                  disabled={busy}
                  onClick={() => void act(true)}
                >
                  {busy ? 'Cancelando…' : 'Confirmar cancelación'}
                </button>
                <button className="link-button" onClick={() => setConfirmCancel(false)}>
                  Conservar suscripción
                </button>
              </div>
            )}
          </>
        ) : (
          <p className="form-alert" role="alert">
            La suscripción está suspendida. Contacta con Nueva Acrópolis para revisar tu acceso.
          </p>
        )}
      </section>
      <aside className="subscription-status">
        <h2>Estado de tu acceso</h2>
        <span className={'status-badge status-' + (subscription?.status ?? 'pending')}>
          {!subscription
            ? 'Sin suscripción'
            : subscription.status === 'active'
              ? 'Activa'
              : subscription.status === 'cancelled'
                ? 'Cancelada'
                : 'Suspendida'}
        </span>
        <p>El nivel institucional y la suscripción son independientes.</p>
        <Link to="/profile">Volver a mi perfil</Link>
      </aside>
    </div>
  );
}
