import { ApiError, request } from './identity';
export type SubscriptionStatus = 'active' | 'cancelled' | 'suspended';
export type Subscription = {
  id: string;
  userId: string;
  plan: 'free_beta';
  status: SubscriptionStatus;
  createdUtc: string;
  activatedUtc: string;
  updatedUtc: string;
  cancelledUtc: string | null;
  expiresUtc: null;
  version: string;
};
export type SubscriptionMe = { subscription: Subscription | null; eligibleToActivate: boolean };
export type SubscriptionAudit = {
  id: string;
  subscriptionId: string;
  actorId: string;
  userId: string;
  action: string;
  beforeStatus: string | null;
  afterStatus: string;
  reason: string;
  createdUtc: string;
};
export type SubscriptionAccount = {
  id: string;
  displayName: string;
  email: string;
  status: 'active' | 'pending' | 'disabled';
  emailConfirmed: boolean;
};
export type SubscriptionPage<T> = { items: T[]; total: number; page: number; pageSize: number };
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function date(value: unknown) {
  return typeof value === 'string' && Number.isFinite(Date.parse(value));
}
function invalid(): never {
  throw new ApiError(0, 'invalid_response');
}
export function subscriptionFrom(value: unknown): Subscription {
  if (
    !object(value) ||
    !['id', 'userId', 'version'].every((key) => typeof value[key] === 'string' && value[key]) ||
    value['plan'] !== 'free_beta' ||
    !['active', 'cancelled', 'suspended'].includes(String(value['status'])) ||
    !['createdUtc', 'activatedUtc', 'updatedUtc'].every((key) => date(value[key])) ||
    !(value['cancelledUtc'] === null || date(value['cancelledUtc'])) ||
    value['expiresUtc'] !== null
  )
    invalid();
  return value as Subscription;
}
export function subscriptionMeFrom(value: unknown): SubscriptionMe {
  if (!object(value) || typeof value['eligibleToActivate'] !== 'boolean') invalid();
  return {
    subscription: value['subscription'] === null ? null : subscriptionFrom(value['subscription']),
    eligibleToActivate: value['eligibleToActivate'],
  };
}
function auditFrom(value: unknown): SubscriptionAudit {
  if (
    !object(value) ||
    !['id', 'subscriptionId', 'userId', 'actorId', 'action', 'afterStatus', 'reason'].every(
      (key) => typeof value[key] === 'string',
    ) ||
    !(value['beforeStatus'] === null || typeof value['beforeStatus'] === 'string') ||
    !date(value['createdUtc'])
  )
    invalid();
  return value as SubscriptionAudit;
}
export function subscriptionAccountFrom(value: unknown): SubscriptionAccount {
  if (
    !object(value) ||
    !['id', 'displayName', 'email'].every((key) => typeof value[key] === 'string' && value[key]) ||
    !['active', 'pending', 'disabled'].includes(String(value['status'])) ||
    typeof value['emailConfirmed'] !== 'boolean'
  )
    invalid();
  return {
    id: value['id'] as string,
    displayName: value['displayName'] as string,
    email: value['email'] as string,
    status: value['status'] as SubscriptionAccount['status'],
    emailConfirmed: value['emailConfirmed'],
  };
}
export function subscriptionAccountsFrom(value: unknown): SubscriptionAccount[] {
  if (!Array.isArray(value) || value.length > 20) invalid();
  const accounts = value.map(subscriptionAccountFrom);
  if (new Set(accounts.map((account) => account.id)).size !== accounts.length) invalid();
  return accounts;
}
function pageFrom<T>(value: unknown, parse: (item: unknown) => T): SubscriptionPage<T> {
  if (
    !object(value) ||
    !Array.isArray(value['items']) ||
    !Number.isInteger(value['total']) ||
    Number(value['total']) < 0 ||
    !Number.isInteger(value['page']) ||
    Number(value['page']) < 1 ||
    !Number.isInteger(value['pageSize']) ||
    Number(value['pageSize']) < 1 ||
    Number(value['pageSize']) > 100
  )
    invalid();
  return {
    items: value['items'].map(parse),
    total: value['total'] as number,
    page: value['page'] as number,
    pageSize: value['pageSize'] as number,
  };
}
export function subscriptionMessage(error: unknown): string {
  if (!(error instanceof ApiError))
    return 'No pudimos consultar la suscripción. Inténtalo de nuevo.';
  const messages: Record<string, string> = {
    subscription_suspended:
      'La suscripción está suspendida. Contacta con Nueva Acrópolis para revisar tu acceso.',
    subscription_required: 'Activa tu suscripción gratuita para acceder a esta obra.',
    email_confirmation_required: 'Confirma tu correo antes de activar la suscripción gratuita.',
    concurrency_conflict: 'Esta suscripción cambió. Recarga los datos antes de continuar.',
  };
  return messages[error.code] ?? error.message;
}
function query(values: Record<string, string | number | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values))
    if (value !== undefined && value !== '') params.set(key, String(value));
  return '?' + params;
}
export const subscriptions = {
  lookupAccounts: async (userIds: string[]) => {
    if (!userIds.length) return [];
    const ids = userIds.map((id) => id.toLowerCase());
    const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
    if (
      ids.length > 20 ||
      new Set(ids).size !== ids.length ||
      ids.some((id) => !guid.test(id) || id === '00000000-0000-0000-0000-000000000000')
    )
      invalid();
    const accounts = subscriptionAccountsFrom(
      await request('/admin/subscriptions/accounts/lookup' + query({ userIds: ids.join(',') })),
    );
    if (accounts.some((account) => !ids.includes(account.id))) invalid();
    return accounts;
  },
  accounts: async (search = '', page = 1) =>
    pageFrom(
      await request('/admin/subscriptions/accounts' + query({ search, page, pageSize: 20 })),
      subscriptionAccountFrom,
    ),
  me: async () => subscriptionMeFrom(await request('/subscriptions/me')),
  activate: async () => subscriptionFrom(await request('/subscriptions/activate', {})),
  cancel: async (version: string) =>
    subscriptionFrom(await request('/subscriptions/cancel', { version })),
  list: async (status: string, userId: string, page = 1) =>
    pageFrom(
      await request('/admin/subscriptions' + query({ status, userId, page, pageSize: 20 })),
      subscriptionFrom,
    ),
  detail: async (id: string) =>
    subscriptionFrom(await request('/admin/subscriptions/' + encodeURIComponent(id))),
  update: async (id: string, version: string, status: SubscriptionStatus, reason: string) =>
    subscriptionFrom(
      await request(
        '/admin/subscriptions/' + encodeURIComponent(id),
        { version, status, reason },
        'PATCH',
      ),
    ),
  audit: async (subscriptionId = '', page = 1) =>
    pageFrom(
      await request('/admin/subscriptions/audit' + query({ subscriptionId, page, pageSize: 20 })),
      auditFrom,
    ),
};
