import { useCallback, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import {
  subscriptions,
  subscriptionMessage,
  subscriptionPlanLabels,
  subscriptionStateLabels,
  subscriptionInstant,
  type SubscriptionMe,
} from '../../api/subscriptions';
import { useCatalog } from '../catalog/useCatalog';
import { useOperationGuard } from '../accounts/useOperationGuard';
import './subscriptions.css';
export function PlanConditions() {
  return (
    <section className="subscription-plans" aria-labelledby="subscription-plans-title">
      <h2 id="subscription-plans-title">Planes de acceso</h2>
      <dl>
        <div>
          <dt>Gratuito</dt>
          <dd>Obras marcadas como gratuitas. Sin vencimiento.</dd>
        </div>
        <div>
          <dt>Probacionismo</dt>
          <dd>Todo el catálogo durante 3 meses naturales desde el inicio asignado.</dd>
        </div>
        <div>
          <dt>Anual</dt>
          <dd>Todo el catálogo durante 1 año desde el inicio asignado.</dd>
        </div>
      </dl>
      <p>
        Probacionismo y Anual se asignan y renuevan con Nueva Acrópolis. No hay renovación
        automática; cualquier contratación requerirá tu aceptación.
      </p>
    </section>
  );
}
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
        <p className="lead">Consulta tu plan, su alcance y las fechas de acceso a la mediateca.</p>
      </div>
      {!user ? (
        <section className="surface">
          <h2>Una cuenta, muchas formas de aprender.</h2>
          <p>
            Crea tu cuenta y confirma tu correo. Puedes activar el plan Gratuito para las obras
            marcadas, o consultar con Nueva Acrópolis por Probacionismo y Anual.
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
      <PlanConditions />
    </div>
  );
}
function MySubscription() {
  const [params] = useSearchParams();
  const load = useCallback(() => subscriptions.me(), []);
  const state = useCatalog(load, 'subscription');
  const operation = useOperationGuard();
  const pending = useRef(false);
  const [current, setCurrent] = useState<SubscriptionMe | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [confirmCancel, setConfirmCancel] = useState(false);
  const data = current ?? state.data;
  const content = params.get('content') ?? '';
  const requestedContent = /^[a-z0-9]+(-[a-z0-9]+)*$/.test(content) ? content : '';
  async function act(cancel = false) {
    if (pending.current || !data || (cancel && !data.subscription)) return;
    const active = operation.begin();
    pending.current = true;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const subscription = cancel
        ? await subscriptions.cancel(data.subscription!.version)
        : await subscriptions.activate();
      if (!active()) return;
      setCurrent({
        subscription,
        eligibleToActivate:
          subscription.plan === 'free_beta' && subscription.status === 'cancelled',
      });
      setConfirmCancel(false);
      setNotice(
        cancel
          ? subscription.plan === 'free_beta'
            ? 'Cancelaste tu suscripción gratuita.'
            : 'Cancelaste tu suscripción.'
          : 'Tu acceso gratuito está activo.',
      );
    } catch (failure) {
      if (active()) setError(subscriptionMessage(failure));
    } finally {
      pending.current = false;
      if (active()) setBusy(false);
    }
  }
  function refresh() {
    setCurrent(null);
    setError('');
    setNotice('');
    setConfirmCancel(false);
    state.reload();
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
        <h2>{subscription ? subscriptionPlanLabels[subscription.plan] : 'Acceso gratuito'}</h2>
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
          {subscription?.plan === 'probationismo'
            ? 'Este plan incluye todo el catálogo durante 3 meses naturales.'
            : subscription?.plan === 'annual'
              ? 'Este plan incluye todo el catálogo durante 1 año.'
              : 'El plan Gratuito incluye únicamente las obras marcadas como gratuitas, sin tarjeta ni cargos.'}
        </p>
        {subscription && (
          <dl className="subscription-dates">
            <div>
              <dt>Inicio</dt>
              <dd>
                <time dateTime={subscription.startsUtc}>
                  {subscriptionInstant(subscription.startsUtc)}
                </time>
              </dd>
            </div>
            <div>
              <dt>Vencimiento</dt>
              <dd>
                {subscription.expiresUtc ? (
                  <time dateTime={subscription.expiresUtc}>
                    {subscriptionInstant(subscription.expiresUtc)}
                  </time>
                ) : (
                  'Sin vencimiento'
                )}
              </dd>
            </div>
          </dl>
        )}
        {subscription && (
          <p className="field-help">
            Fechas en hora de Lima (UTC−05:00). El acceso termina al llegar al vencimiento.
          </p>
        )}
        {data.eligibleToActivate ? (
          <div className="subscription-actions">
            <button className="button" disabled={busy} onClick={() => void act()}>
              {busy ? 'Activando…' : 'Suscribirme gratis'}
            </button>
          </div>
        ) : !subscription ? (
          <p className="form-alert">
            Confirma tu correo o contacta con Nueva Acrópolis para revisar tu acceso.
          </p>
        ) : null}
        {subscription?.effectiveState === 'active' && (
          <div className="subscription-actions">
            <Link
              className="button"
              to={requestedContent ? '/content/' + requestedContent : '/explore'}
            >
              {requestedContent ? 'Volver al contenido' : 'Explorar la mediateca'}
            </Link>
          </div>
        )}
        {subscription?.effectiveState === 'scheduled' && (
          <p className="form-notice">Tu acceso comenzará en la fecha de inicio indicada.</p>
        )}
        {subscription?.effectiveState === 'expired' && (
          <p className="form-notice">
            Tu plan venció. Contacta con Nueva Acrópolis para renovarlo o cambiar al plan Gratuito.
          </p>
        )}
        {subscription?.status === 'suspended' && (
          <p className="form-alert" role="alert">
            La suscripción está suspendida. Contacta con Nueva Acrópolis para revisar tu acceso.
          </p>
        )}
        {subscription?.status === 'cancelled' && subscription.plan !== 'free_beta' && (
          <p className="form-notice">
            La suscripción está cancelada. Contacta con Nueva Acrópolis para renovar el plan o
            asignar acceso gratuito.
          </p>
        )}
        {subscription?.status === 'active' && (
          <div className="subscription-actions">
            <button className="link-button" disabled={busy} onClick={() => setConfirmCancel(true)}>
              Cancelar suscripción
            </button>
          </div>
        )}
        {confirmCancel && (
          <div className="editor-confirmation" role="alert">
            <p>
              {subscription?.plan === 'free_beta'
                ? 'Al cancelar perderás el acceso a las obras gratuitas. Podrás activar de nuevo el plan Gratuito.'
                : 'Al cancelar perderás el acceso de este plan. Para reactivarlo tendrás que contactar con Nueva Acrópolis.'}
            </p>
            <button
              className="button button-outline"
              disabled={busy}
              onClick={() => void act(true)}
            >
              {busy ? 'Cancelando…' : 'Confirmar cancelación'}
            </button>
            <button className="link-button" disabled={busy} onClick={() => setConfirmCancel(false)}>
              Conservar suscripción
            </button>
          </div>
        )}
      </section>
      <aside className="subscription-status">
        <h2>Estado de tu acceso</h2>
        <span className={'status-badge status-' + (subscription?.effectiveState ?? 'pending')}>
          {subscription ? subscriptionStateLabels[subscription.effectiveState] : 'Sin suscripción'}
        </span>
        <p>El nivel institucional y la suscripción son independientes.</p>
        <button className="link-button" disabled={busy} onClick={refresh}>
          Actualizar estado
        </button>
        <p>
          <Link to="/profile">Volver a mi perfil</Link>
        </p>
      </aside>
    </div>
  );
}
