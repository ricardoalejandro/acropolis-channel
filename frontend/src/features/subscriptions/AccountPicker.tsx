import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { subscriptions, type SubscriptionAccount } from '../../api/subscriptions';
import { CatalogState, Pagination } from '../catalog/Catalog';
import { useCatalog } from '../catalog/useCatalog';
const statusLabels: Record<string, string> = {
  active: 'Activa',
  pending: 'Pendiente',
  disabled: 'Deshabilitada',
};
export function SubscriptionAccountPicker({
  select,
}: {
  select: (account: SubscriptionAccount) => void;
}) {
  const [opened, setOpened] = useState(false);
  return (
    <section className="subscription-account-picker">
      <button
        className="button button-outline button-small"
        type="button"
        aria-expanded={opened}
        aria-controls="subscription-account-directory"
        onClick={() => setOpened(!opened)}
      >
        {opened ? 'Cerrar búsqueda de cuentas' : 'Buscar una cuenta'}
      </button>
      {opened && <AccountDirectory select={select} />}
    </section>
  );
}
function AccountDirectory({ select }: { select: (account: SubscriptionAccount) => void }) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState({ search: '', page: 1 });
  const load = useCallback(() => subscriptions.accounts(query.search, query.page), [query]);
  const state = useCatalog(load, JSON.stringify(query));
  return (
    <div id="subscription-account-directory" className="account-directory">
      <form
        className="catalog-filters"
        onSubmit={(event) => {
          event.preventDefault();
          setQuery({ search: search.trim(), page: 1 });
        }}
      >
        <div className="field">
          <label htmlFor="account-search">Nombre o correo de la cuenta</label>
          <input
            id="account-search"
            type="search"
            maxLength={200}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </div>
        <button className="button button-small" type="submit">
          Buscar cuentas
        </button>
      </form>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <>
            <p className="results-count" role="status">
              {state.data.total}{' '}
              {state.data.total === 1 ? 'cuenta encontrada' : 'cuentas encontradas'}
            </p>
            {state.data.items.length ? (
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>Cuenta</th>
                      <th>Estado</th>
                      <th>Correo</th>
                      <th>Acción</th>
                    </tr>
                  </thead>
                  <tbody>
                    {state.data.items.map((account) => (
                      <tr key={account.id}>
                        <td>
                          <strong>{account.displayName}</strong>
                          <span className="table-email">{account.email}</span>
                        </td>
                        <td>
                          <span className={'status-badge status-' + account.status}>
                            {statusLabels[account.status]}
                          </span>
                        </td>
                        <td>{account.emailConfirmed ? 'Confirmado' : 'Por confirmar'}</td>
                        <td>
                          <button
                            className="link-button"
                            type="button"
                            aria-label={
                              'Ver suscripciones de ' + account.displayName + ', ' + account.email
                            }
                            onClick={() => select(account)}
                          >
                            Ver suscripciones
                          </button>
                          <Link
                            className="text-link"
                            to={'/admin/subscriptions/accounts/' + account.id}
                            aria-label={
                              'Asignar plan a ' + account.displayName + ', ' + account.email
                            }
                          >
                            Asignar plan
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="empty-state">No encontramos cuentas con esa búsqueda.</p>
            )}
            <Pagination
              page={state.data.page}
              pageSize={state.data.pageSize}
              total={state.data.total}
              change={(page) => setQuery((previous) => ({ ...previous, page }))}
            />
          </>
        )}
      </CatalogState>
    </div>
  );
}
