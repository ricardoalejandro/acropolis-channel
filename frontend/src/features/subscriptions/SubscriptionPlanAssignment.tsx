import { useCallback, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ApiError } from '../../api/identity';
import {
  subscriptions,
  subscriptionMessage,
  subscriptionPlanLabels,
  subscriptionInstant,
  type Subscription,
  type SubscriptionPlan,
} from '../../api/subscriptions';
import { hasControlCharacters } from '../../validation/plainText';
import { useUnsavedChanges } from '../../components/useUnsavedChanges';
import { useOperationGuard } from '../accounts/useOperationGuard';
import { CatalogState } from '../catalog/Catalog';
import { useCatalog } from '../catalog/useCatalog';
import { limaInput, limaStart, planPeriodEnd } from './subscriptionDates';
import './subscriptions.css';
export function SubscriptionAccountAssignment() {
  const { userId = '' } = useParams();
  return <AccountAssignmentPage key={userId} userId={userId} />;
}
function AccountAssignmentPage({ userId }: { userId: string }) {
  const load = useCallback(async () => {
    const [accounts, page] = await Promise.all([
      subscriptions.lookupAccounts([userId]),
      subscriptions.list('', userId),
    ]);
    const account = accounts.find((item) => item.id === userId);
    if (!account) throw new ApiError(404, 'not_found');
    if (page.total > 1 || page.items.some((item) => item.userId !== userId))
      throw new ApiError(0, 'invalid_response');
    return { account, subscription: page.items[0] ?? null };
  }, [userId]);
  const state = useCatalog(load, userId);
  const [dirty, setDirty] = useState(false);
  useUnsavedChanges(dirty);
  return (
    <div className="workspace">
      <Link className="text-link back-link" to="/admin/subscriptions">
        Volver a suscripciones
      </Link>
      <h1>Asignar plan a una cuenta</h1>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <AssignmentView
            key={userId + (state.data.subscription?.version ?? 'new')}
            userId={userId}
            current={state.data.subscription}
            onDirty={setDirty}
            reload={state.reload}
            accountName={state.data.account.displayName}
            email={state.data.account.email}
          />
        )}
      </CatalogState>
    </div>
  );
}
function AssignmentView({
  userId,
  current: initial,
  onDirty,
  reload,
  accountName,
  email,
}: {
  userId: string;
  current: Subscription | null;
  onDirty: (value: boolean) => void;
  reload: () => void;
  accountName: string;
  email: string;
}) {
  const [current, setCurrent] = useState(initial);
  return (
    <section className="surface subscription-assignment">
      <p>
        <strong>{accountName}</strong>
        <span className="table-email">{email}</span>
      </p>
      <PlanAssignmentForm
        userId={userId}
        current={current}
        onSaved={setCurrent}
        onDirty={onDirty}
        reload={reload}
      />
    </section>
  );
}
export function PlanAssignmentForm({
  userId,
  current,
  onSaved,
  onDirty,
  onBusy,
  reload,
  disabled = false,
}: {
  userId: string;
  current: Subscription | null;
  onSaved: (value: Subscription) => void;
  onDirty: (value: boolean) => void;
  onBusy?: (value: boolean) => void;
  reload: () => void;
  disabled?: boolean;
}) {
  const canRenew = Boolean(
    current?.expiresUtc && current.plan !== 'free_beta' && current.status !== 'suspended',
  );
  const [renew, setRenew] = useState(canRenew);
  const [plan, setPlan] = useState<SubscriptionPlan | ''>(current?.plan ?? '');
  const [start, setStart] = useState(canRenew ? limaInput(current!.expiresUtc!) : '');
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [fieldError, setFieldError] = useState('');
  const [conflict, setConflict] = useState(false);
  const [saved, setSaved] = useState<Subscription | null>(null);
  const operation = useOperationGuard();
  const pending = useRef(false);
  const planField = useRef<HTMLSelectElement>(null);
  const startField = useRef<HTMLInputElement>(null);
  const reasonField = useRef<HTMLInputElement>(null);
  const startsUtc =
    plan === 'free_beta'
      ? null
      : renew && current?.expiresUtc
        ? current.expiresUtc
        : limaStart(start);
  const end = plan && startsUtc ? planPeriodEnd(plan, startsUtc) : null;
  function change(
    nextPlan: SubscriptionPlan | '',
    nextStart: string,
    nextReason: string,
    nextRenew = renew,
  ) {
    setPlan(nextPlan);
    setStart(nextStart);
    setReason(nextReason);
    setConfirmed(false);
    setSaved(null);
    setError('');
    setFieldError('');
    const unchangedRenewalStart =
      nextRenew && current?.expiresUtc && nextStart === limaInput(current.expiresUtc);
    onDirty(
      nextPlan !== (current?.plan ?? '') ||
        Boolean(nextReason) ||
        (nextStart !== '' && !unchangedRenewalStart),
    );
  }
  function chooseRenew(value: boolean) {
    setRenew(value);
    change(
      value ? current!.plan : (current?.plan ?? ''),
      value ? limaInput(current!.expiresUtc!) : '',
      reason,
      value,
    );
  }
  function prepare() {
    if (pending.current || disabled || conflict) return;
    if (!plan || (plan !== 'free_beta' && (!startsUtc || !end))) {
      setError('Selecciona un plan y una fecha de inicio válida.');
      setFieldError(!plan ? 'plan' : 'start');
      (!plan ? planField.current : startField.current)?.focus();
      return;
    }
    if (reason.trim().length < 2 || reason.length > 200 || hasControlCharacters(reason)) {
      setError('Escribe un motivo de 2 a 200 caracteres en una sola línea.');
      setFieldError('reason');
      reasonField.current?.focus();
      return;
    }
    setError('');
    setFieldError('');
    setConfirmed(true);
  }
  async function save() {
    if (
      pending.current ||
      disabled ||
      conflict ||
      !confirmed ||
      !plan ||
      (plan !== 'free_beta' && !startsUtc)
    )
      return;
    const active = operation.begin();
    pending.current = true;
    setBusy(true);
    onBusy?.(true);
    setError('');
    try {
      const result = await subscriptions.assign(
        userId,
        plan,
        startsUtc,
        current?.version ?? null,
        reason.trim(),
      );
      if (!active()) return;
      onSaved(result);
      onDirty(false);
      setSaved(result);
      setReason('');
      setStart('');
      setRenew(false);
      setPlan(result.plan);
      setConfirmed(false);
    } catch (failure) {
      if (active()) {
        setError(subscriptionMessage(failure));
        setConflict(failure instanceof ApiError && failure.code === 'concurrency_conflict');
      }
    } finally {
      pending.current = false;
      if (active()) {
        setBusy(false);
        onBusy?.(false);
      }
    }
  }
  return (
    <div className="subscription-plan-editor">
      <h2>Asignar o renovar el plan</h2>
      <p>
        El cambio es manual y queda en la auditoría. No registra un cobro ni una renovación
        automática.
      </p>
      {current && (
        <p className="field-help">
          Plan actual: {subscriptionPlanLabels[current.plan]}.{' '}
          {current.expiresUtc
            ? 'Vence el ' + subscriptionInstant(current.expiresUtc) + ' (hora de Lima).'
            : 'Sin vencimiento.'}
        </p>
      )}
      {saved && (
        <div className="success-message" role="status">
          <p>Guardamos el plan {subscriptionPlanLabels[saved.plan]}.</p>
          <p>
            Acceso desde {subscriptionInstant(saved.startsUtc)}
            {saved.expiresUtc
              ? ' hasta ' + subscriptionInstant(saved.expiresUtc)
              : ', sin vencimiento'}{' '}
            (hora de Lima).
          </p>
          <Link to={'/admin/audit?module=subscriptions&subscriptionId=' + saved.id}>
            Revisar el cambio en la auditoría
          </Link>
        </div>
      )}
      {canRenew && (
        <div className="subscription-actions">
          <button
            className="button button-outline button-small"
            disabled={busy || disabled}
            type="button"
            aria-pressed={renew}
            onClick={() => chooseRenew(true)}
          >
            Renovar plan actual
          </button>
          <button
            className="link-button"
            disabled={busy || disabled}
            type="button"
            aria-pressed={!renew}
            onClick={() => chooseRenew(false)}
          >
            Asignar otro período o plan
          </button>
        </div>
      )}
      {error && (
        <div className="form-alert" role="alert">
          <p>{error}</p>
          {conflict && (
            <button
              className="link-button"
              type="button"
              onClick={() => {
                if (
                  window.confirm('Recargar sustituirá los cambios de este formulario. ¿Continuar?')
                ) {
                  onDirty(false);
                  reload();
                }
              }}
            >
              Recargar suscripción
            </button>
          )}
        </div>
      )}
      <form
        className="account-form"
        aria-label="Asignación manual de plan"
        aria-busy={busy}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          prepare();
        }}
      >
        <div className="field">
          <label htmlFor="assigned-plan">Plan</label>
          <select
            ref={planField}
            id="assigned-plan"
            value={plan}
            disabled={busy || disabled || renew}
            aria-invalid={fieldError === 'plan'}
            onChange={(event) => change(event.target.value as SubscriptionPlan | '', start, reason)}
          >
            <option value="" disabled>
              Seleccionar plan
            </option>
            {Object.entries(subscriptionPlanLabels).map(([id, label]) => (
              <option key={id} value={id}>
                {label}
              </option>
            ))}
          </select>
        </div>
        {plan !== 'free_beta' && (
          <div className="field">
            <label htmlFor="assigned-start">Inicio del período (hora de Lima)</label>
            <input
              ref={startField}
              id="assigned-start"
              type="datetime-local"
              step="1"
              value={start}
              readOnly={renew}
              disabled={busy || disabled}
              aria-invalid={fieldError === 'start'}
              aria-describedby="assigned-date-help"
              onChange={(event) => change(plan, event.target.value, reason)}
            />
            <p id="assigned-date-help" className="field-help">
              UTC−05:00.{' '}
              {renew
                ? 'La nueva etapa comienza al vencimiento actual y conserva el acceso del período en curso.'
                : 'Indica el inicio autorizado; el servidor calcula el vencimiento del plan.'}
            </p>
          </div>
        )}
        <div className="field">
          <label htmlFor="assigned-reason">Motivo de la asignación</label>
          <input
            ref={reasonField}
            id="assigned-reason"
            maxLength={200}
            value={reason}
            disabled={busy || disabled}
            aria-invalid={fieldError === 'reason'}
            aria-describedby="assigned-reason-help"
            onChange={(event) => change(plan, start, event.target.value)}
          />
          <p id="assigned-reason-help" className="field-help">
            De 2 a 200 caracteres, sin contraseñas ni datos sensibles.
          </p>
        </div>
        {plan === 'free_beta' && (
          <p className="subscription-period-preview">
            Gratuito: acceso inmediato sólo a las obras marcadas, sin vencimiento. Sustituirá el
            alcance del plan actual si lo hay.
          </p>
        )}
        {startsUtc && plan && (
          <p className="subscription-period-preview">
            Nueva etapa: {subscriptionPlanLabels[plan]}, desde {subscriptionInstant(startsUtc)}
            {end ? ' hasta ' + subscriptionInstant(end) : ', sin vencimiento'} (hora de Lima).
          </p>
        )}
        {!confirmed && (
          <button className="button" type="submit" disabled={busy || disabled || conflict}>
            {renew ? 'Revisar renovación' : 'Revisar asignación'}
          </button>
        )}
        {confirmed && (
          <div className="editor-confirmation">
            <p>
              Confirma la cuenta, el plan y la fecha antes de registrar el cambio.{' '}
              {renew && 'El inicio del acceso actual se conservará.'}
            </p>
            <button
              className="button"
              disabled={busy || disabled || conflict}
              type="button"
              onClick={() => void save()}
            >
              {busy ? 'Guardando…' : 'Confirmar asignación'}
            </button>
            <button
              className="link-button"
              disabled={busy || disabled}
              type="button"
              onClick={() => setConfirmed(false)}
            >
              Seguir editando
            </button>
          </div>
        )}
      </form>
    </div>
  );
}
