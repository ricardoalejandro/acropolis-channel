import { type SubscriptionAccount } from '../../api/subscriptions';
export function SubscriptionAccountName({
  account,
  loading = false,
  detail = false,
}: {
  account: SubscriptionAccount | null;
  loading?: boolean;
  detail?: boolean;
}) {
  if (!account)
    return (
      <span>{loading ? 'Cargando datos de la cuenta…' : 'Datos de la cuenta no disponibles.'}</span>
    );
  const labels = { active: 'Activa', pending: 'Pendiente', disabled: 'Deshabilitada' };
  return (
    <>
      <strong>{account.displayName}</strong>
      <span className="table-email">{account.email}</span>
      {detail && (
        <p>
          Estado de la cuenta: {labels[account.status]}. Correo{' '}
          {account.emailConfirmed ? 'confirmado' : 'por confirmar'}.
        </p>
      )}
    </>
  );
}
export function SubscriptionAccountsNotice({
  loading,
  unavailable,
  retry,
}: {
  loading: boolean;
  unavailable: boolean;
  retry: () => void;
}) {
  if (loading) return <p role="status">Cargando datos de las cuentas…</p>;
  if (!unavailable) return null;
  return (
    <p className="form-alert" role="alert">
      No pudimos obtener los datos de algunas cuentas.
      <button className="link-button" type="button" onClick={retry}>
        Reintentar datos de cuentas
      </button>
    </p>
  );
}
