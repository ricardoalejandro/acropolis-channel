import { ApiError, request } from './identity';
export type SubscriptionStatus = 'active' | 'cancelled' | 'suspended';
export type SubscriptionPlan = 'free_beta' | 'probationismo' | 'annual';
export type SubscriptionEffectiveState = SubscriptionStatus | 'scheduled' | 'expired';
export const subscriptionPlanLabels: Record<SubscriptionPlan, string> = {
  free_beta: 'Gratuito',
  probationismo: 'Probacionismo',
  annual: 'Anual',
};
export const subscriptionStateLabels: Record<SubscriptionEffectiveState, string> = {
  active: 'Activa',
  scheduled: 'Programada',
  expired: 'Vencida',
  cancelled: 'Cancelada',
  suspended: 'Suspendida',
};
const limaInstant = new Intl.DateTimeFormat('es-PE', {
  dateStyle: 'medium',
  timeStyle: 'short',
  timeZone: 'America/Lima',
});
export function subscriptionInstant(value: string) {
  return limaInstant.format(new Date(value));
}
export type Subscription = {
  id: string;
  userId: string;
  plan: SubscriptionPlan;
  status: SubscriptionStatus;
  createdUtc: string;
  activatedUtc: string;
  startsUtc: string;
  effectiveState: SubscriptionEffectiveState;
  updatedUtc: string;
  cancelledUtc: string | null;
  expiresUtc: string | null;
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
  beforePlan: SubscriptionPlan | null;
  afterPlan: SubscriptionPlan | null;
  beforeStartsUtc: string | null;
  afterStartsUtc: string | null;
  beforeExpiresUtc: string | null;
  afterExpiresUtc: string | null;
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
    !Object.hasOwn(subscriptionPlanLabels, String(value['plan'])) ||
    !['active', 'cancelled', 'suspended'].includes(String(value['status'])) ||
    !['createdUtc', 'activatedUtc', 'startsUtc', 'updatedUtc'].every((key) => date(value[key])) ||
    !Object.hasOwn(subscriptionStateLabels, String(value['effectiveState'])) ||
    (value['status'] !== 'active' && value['effectiveState'] !== value['status']) ||
    (value['status'] === 'active' &&
      !['active', 'scheduled', 'expired'].includes(String(value['effectiveState']))) ||
    !(value['cancelledUtc'] === null || date(value['cancelledUtc'])) ||
    !(value['plan'] === 'free_beta'
      ? value['expiresUtc'] === null
      : date(value['expiresUtc']) &&
        Date.parse(value['expiresUtc'] as string) > Date.parse(value['startsUtc'] as string))
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
    !date(value['createdUtc']) ||
    !['beforePlan', 'afterPlan'].every(
      (key) => value[key] === null || Object.hasOwn(subscriptionPlanLabels, String(value[key])),
    ) ||
    !['beforeStartsUtc', 'afterStartsUtc', 'beforeExpiresUtc', 'afterExpiresUtc'].every(
      (key) => value[key] === null || date(value[key]),
    )
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
    subscription_required: 'Necesitas una suscripción vigente para acceder a esta obra. Revisa tu plan y sus fechas de acceso.',
    plan_change_requires_manager: 'Para cambiar o renovar este plan, contacta con Nueva Acrópolis.',
    content_requires_plan:
      'Esta obra requiere Probacionismo o Anual. El plan Gratuito incluye las obras marcadas como gratuitas.',
    active_period_would_be_replaced:
      'Ese período futuro sustituiría el acceso actual. Usa Renovar plan actual para conservar su continuidad, o revisa la fecha de inicio.',
    account_not_active: 'La cuenta debe estar activa y tener el correo confirmado.',
    owner_protected: 'El acceso del propietario requiere su autorización.',
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
  assign: async (
    userId: string,
    plan: SubscriptionPlan,
    startsUtc: string | null,
    version: string | null,
    reason: string,
  ) =>
    subscriptionFrom(
      await request(
        '/admin/subscriptions/accounts/' + encodeURIComponent(userId) + '/assign',
        { plan, startsUtc, version, reason },
      ),
    ),
  audit: async (subscriptionId = '', page = 1) =>
    pageFrom(
      await request('/admin/subscriptions/audit' + query({ subscriptionId, page, pageSize: 20 })),
      auditFrom,
    ),
};
