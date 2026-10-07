import { useCallback, useState } from 'react';
import { Link, useParams, useSearchParams, type SetURLSearchParams } from 'react-router-dom';
import {
  subscriptions,
  subscriptionMessage,
  type Subscription,
  type SubscriptionStatus,
  type SubscriptionAccount,
} from '../../api/subscriptions';
import { hasControlCharacters } from '../../validation/plainText';
import { useSubscriptionAccounts } from './useSubscriptionAccounts';
import { SubscriptionAccountPicker } from './AccountPicker';
import { SubscriptionAccountName, SubscriptionAccountsNotice } from './SubscriptionAccounts';
import { useUnsavedChanges } from '../../components/useUnsavedChanges';
import { useCatalog } from '../catalog/useCatalog';
import { CatalogState, Pagination } from '../catalog/Catalog';
const statuses: Record<SubscriptionStatus, string> = {
  active: 'Activa',
  cancelled: 'Cancelada',
  suspended: 'Suspendida',
};
export function SubscriptionAdminList() {
  const [params, setParams] = useSearchParams();
  const [accounts, setAccounts] = useState<Record<string, SubscriptionAccount>>({});
  function selectAccount(account: SubscriptionAccount, status: string) {
    setAccounts((previous) => ({ ...previous, [account.id]: account }));
    const next = new URLSearchParams({ userId: account.id });
    if (status) next.set('status', status);
    setParams(next);
  }
  return (
    <SubscriptionListView
      key={params.toString()}
      params={params}
      setParams={setParams}
      account={accounts[params.get('userId') ?? ''] ?? null}
      selectAccount={selectAccount}
    />
  );
}
function SubscriptionListView({
  params,
  setParams,
  account,
  selectAccount,
}: {
  params: URLSearchParams;
  setParams: SetURLSearchParams;
  account: SubscriptionAccount | null;
  selectAccount: (account: SubscriptionAccount, status: string) => void;
}) {
  const rawPage = /^\d+$/.test(params.get('page') ?? '') ? Number(params.get('page')) : 1;
  const page = rawPage >= 1 && rawPage <= 1000000 ? rawPage : 1;
  const appliedStatus = params.get('status') ?? '';
  const appliedUser = params.get('userId') ?? '';
  const [status, setStatus] = useState(appliedStatus);
  const load = useCallback(
    () => subscriptions.list(appliedStatus, appliedUser, page),
    [appliedStatus, appliedUser, page],
  );
  const state = useCatalog(load, params.toString());
  const names = useSubscriptionAccounts(state.data?.items.map((item) => item.userId) ?? []);
  const accountsById = new Map(names.data?.map((item) => [item.id, item]) ?? []);
  const missingNames = Boolean(state.data?.items.some((item) => !accountsById.has(item.userId)));
  function change(nextPage = 1, applied = false) {
    const next = new URLSearchParams();
    const nextStatus = applied ? appliedStatus : status;
    const nextUser = appliedUser;
    if (nextStatus) next.set('status', nextStatus);
    if (nextUser) next.set('userId', nextUser);
    if (nextPage > 1) next.set('page', String(nextPage));
    setParams(next);
  }
  return (
    <div className="workspace">
      <div className="page-heading">
        <h1>Suscripciones</h1>
        <p className="lead">Gestiona el acceso gratuito y revisa sus cambios.</p>
      </div>
      <section className="surface">
        <form
          className="catalog-filters"
          onSubmit={(event) => {
            event.preventDefault();
            change();
          }}
        >
          <div className="field">
            <span className="field-label">Cuenta</span>
            <p>
              {appliedUser ? (
                account ? (
                  <>
                    <strong>{account.displayName}</strong>
                    <span className="table-email">{account.email}</span>
                  </>
                ) : (
                  'Cuenta seleccionada desde el enlace'
                )
              ) : (
                'Todas las cuentas'
              )}
            </p>
            {appliedUser && (
              <button
                className="link-button"
                type="button"
                onClick={() => {
                  const next = new URLSearchParams();
                  if (status) next.set('status', status);
                  setParams(next);
                }}
              >
                Quitar filtro de cuenta
              </button>
            )}
          </div>
          <div className="field">
            <label htmlFor="subscription-status">Estado</label>
            <select
              id="subscription-status"
              value={status}
              onChange={(event) => setStatus(event.target.value)}
            >
              <option value="">Todos</option>
              {Object.entries(statuses).map(([id, label]) => (
                <option key={id} value={id}>
                  {label}
                </option>
              ))}
            </select>
          </div>
          <button className="button" type="submit">
            Buscar
          </button>
        </form>
        <SubscriptionAccountPicker
          select={(selectedAccount) => selectAccount(selectedAccount, status)}
        />
        <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
          {state.data && (
            <>
              <p className="results-count" role="status">
                {state.data.total} suscripciones
              </p>
              {state.data.items.length > 0 && (
                <SubscriptionAccountsNotice
                  loading={names.loading}
                  unavailable={Boolean(names.error) || missingNames}
                  retry={names.reload}
                />
              )}
              {state.data.items.length ? (
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Cuenta</th>
                        <th>Estado</th>
                        <th>Activación</th>
                        <th>Acciones</th>
                      </tr>
                    </thead>
                    <tbody>
                      {state.data.items.map((item) => (
                        <tr key={item.id}>
                          <td className="wrap-anywhere">
                            <SubscriptionAccountName
                              account={
                                accountsById.get(item.userId) ??
                                (account?.id === item.userId ? account : null)
                              }
                              loading={names.loading}
                            />
                          </td>
                          <td>
                            <span className="status-badge">{statuses[item.status]}</span>
                          </td>
                          <td>{new Date(item.activatedUtc).toLocaleDateString('es-PE')}</td>
                          <td>
                            <Link to={'/admin/subscriptions/' + item.id}>
                              Gestionar suscripción
                            </Link>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <p className="empty-state">No hay suscripciones con estos filtros.</p>
              )}
              <Pagination
                page={state.data.page}
                pageSize={state.data.pageSize}
                total={state.data.total}
                change={(next) => change(next, true)}
              />
            </>
          )}
        </CatalogState>
        <p className="subscription-audit">
          <Link to="/admin/audit?module=subscriptions">Consultar auditoría de suscripciones</Link>
        </p>
      </section>
    </div>
  );
}
export function SubscriptionAdminDetail() {
  const { id = '' } = useParams();
  const load = useCallback(() => subscriptions.detail(id), [id]);
  const state = useCatalog(load, id);
  return (
    <div className="workspace">
      <Link className="text-link back-link" to="/admin/subscriptions">
        Volver a suscripciones
      </Link>
      <h1>Gestionar suscripción</h1>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <SubscriptionEditor
            key={state.data.id + state.data.version}
            item={state.data}
            reload={state.reload}
          />
        )}
      </CatalogState>
    </div>
  );
}
function SubscriptionEditor({ item, reload }: { item: Subscription; reload: () => void }) {
  const [current, setCurrent] = useState(item);
  const names = useSubscriptionAccounts([current.userId]);
  const account = names.data?.find((entry) => entry.id === current.userId) ?? null;
  const [status, setStatus] = useState<SubscriptionStatus>(item.status);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');
  const [busy, setBusy] = useState(false);
  useUnsavedChanges(status !== current.status || Boolean(reason));
  async function save() {
    if (busy) return;
    if (reason.trim().length < 2 || reason.length > 200 || hasControlCharacters(reason)) {
      setError('Escribe un motivo de 2 a 200 caracteres en una sola línea.');
      return;
    }
    setBusy(true);
    setError('');
    setSaved('');
    try {
      const result = await subscriptions.update(current.id, current.version, status, reason.trim());
      setCurrent(result);
      setStatus(result.status);
      setReason('');
      setSaved('Guardamos el estado de la suscripción.');
    } catch (failure) {
      setError(subscriptionMessage(failure));
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="surface subscription-audit">
      <div className="wrap-anywhere">
        <span className="field-label">Cuenta</span>
        <SubscriptionAccountName account={account} loading={names.loading} detail />
      </div>
      <SubscriptionAccountsNotice
        loading={names.loading}
        unavailable={Boolean(names.error) || !account}
        retry={names.reload}
      />
      <p>Plan: acceso gratuito, sin vencimiento ni cobros.</p>
      {saved && (
        <p className="success-message" role="status">
          {saved}
        </p>
      )}
      {error && (
        <div className="form-alert" role="alert">
          {error}
          <button
            className="link-button"
            onClick={() => {
              if (window.confirm('Recargar sustituirá tus cambios sin guardar. ¿Continuar?'))
                reload();
            }}
          >
            Recargar datos
          </button>
        </div>
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
          <label htmlFor="managed-status">Estado de la suscripción</label>
          <select
            id="managed-status"
            value={status}
            disabled={busy}
            onChange={(event) => setStatus(event.target.value as SubscriptionStatus)}
          >
            {Object.entries(statuses).map(([id, label]) => (
              <option key={id} value={id}>
                {label}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="managed-reason">Motivo del cambio</label>
          <textarea
            id="managed-reason"
            rows={3}
            maxLength={200}
            value={reason}
            disabled={busy}
            onChange={(event) => setReason(event.target.value)}
          />
          <p className="field-help">
            Al cambiar el estado, el motivo quedará en la auditoría. No incluyas contraseñas,
            documentos ni datos sensibles.
          </p>
        </div>
        <button className="button" disabled={busy} type="submit">
          {busy ? 'Guardando…' : 'Guardar estado'}
        </button>
      </form>
      <p className="subscription-audit">
        <Link to={'/admin/audit?module=subscriptions&subscriptionId=' + current.id}>
          Ver historial de esta suscripción
        </Link>
      </p>
    </section>
  );
}
