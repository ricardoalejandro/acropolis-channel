import { ApiError, request } from './identity';
import { subscriptionPlanLabels, subscriptionInstant, type SubscriptionPlan } from './subscriptions';
export const auditModules = [
  {
    id: 'users',
    label: 'Usuarios',
    permission: 'Users.Manage',
    objectKey: 'userId',
    actions: [
      'users.updated',
      'permissions.updated',
      'admin.bootstrap',
      'owner.bootstrap',
      'owner.recovered',
      'account.revalidated',
      'account.recovery_invalidated',
      'mfa.enabled',
      'mfa.disabled',
      'mfa.codes_rotated',
      'content.permission.granted',
      'content.permission.revoked',
    ],
  },
  {
    id: 'content',
    label: 'Contenidos',
    permission: 'Content.Manage',
    objectKey: 'contentId',
    actions: [
      'content.created',
      'content.updated',
      'content.published',
      'content.withdrawn',
      'content.archived',
      'content.restored',
    ],
  },
  {
    id: 'subscriptions',
    label: 'Suscripciones',
    permission: 'Subscriptions.Manage',
    objectKey: 'subscriptionId',
    actions: [
      'subscription.activated',
      'subscription.assigned',
      'subscription.renewed',
      'subscription.reactivated',
      'subscription.cancelled',
      'subscription.updated',
      'subscription.recovery_suspended',
    ],
  },
] as const;
export const auditActionLabels: Record<string, string> = {
  'users.updated': 'Cuenta actualizada',
  'permissions.updated': 'Permisos actualizados',
  'admin.bootstrap': 'Administrador habilitado',
  'owner.bootstrap': 'Propietario habilitado',
  'owner.recovered': 'Propietario recuperado',
  'account.revalidated': 'Cuenta revalidada',
  'account.recovery_invalidated': 'Acceso invalidado durante recuperación',
  'mfa.enabled': 'Doble factor activado',
  'mfa.disabled': 'Doble factor desactivado',
  'mfa.codes_rotated': 'Códigos de recuperación renovados',
  'content.permission.granted': 'Permiso editorial concedido',
  'content.permission.revoked': 'Permiso editorial revocado',
  'content.created': 'Borrador creado',
  'content.updated': 'Contenido actualizado',
  'content.published': 'Contenido publicado',
  'content.withdrawn': 'Publicación retirada',
  'content.archived': 'Contenido archivado',
  'content.restored': 'Contenido restaurado',
  'subscription.activated': 'Suscripción activada',
  'subscription.assigned': 'Plan asignado',
  'subscription.renewed': 'Plan renovado',
  'subscription.reactivated': 'Suscripción reactivada',
  'subscription.cancelled': 'Suscripción cancelada',
  'subscription.updated': 'Suscripción actualizada',
  'subscription.recovery_suspended': 'Suscripción suspendida durante recuperación',
};
const fieldLabels: Record<string, string> = {
  displayName: 'nombre visible',
  status: 'estado',
  levels: 'niveles institucionales',
  permissions: 'permisos',
  owner: 'propietario',
  credentials: 'credenciales',
  mfa: 'doble factor',
};
function editorialDescription(changes: string) {
  try {
    const value: unknown = JSON.parse(changes);
    if (!object(value)) return 'Cambio editorial registrado';
    const labels: string[] = [];
    const statusLabels: Record<string, string> = {
      draft: 'Borrador',
      published: 'Publicado',
      archived: 'Archivado',
    };
    if (typeof value['status'] === 'string' && statusLabels[value['status']])
      labels.push(statusLabels[value['status']]!);
    const before = value['before'];
    if (object(before)) {
      for (const [key, label] of Object.entries({
        titleChanged: 'título',
        synopsisChanged: 'sinopsis',
        workChanged: 'obra completa',
        metadataChanged: 'atribución o etiquetas',
        collectionChanged: 'colección',
      }))
        if (before[key] === true) labels.push(label);
      if (typeof before['isFree'] === 'boolean' && typeof value['isFree'] === 'boolean' && before['isFree'] !== value['isFree']) labels.push('acceso gratuito');
    } else if (typeof value['hasWork'] === 'boolean')
      labels.push(value['hasWork'] ? 'con obra completa' : 'ficha editorial');
    if (Number.isInteger(value['itemCount']) && Number(value['itemCount']) >= 0)
      labels.push(String(value['itemCount']) + ' elementos');
    return labels.join(' · ') || 'Cambio editorial registrado';
  } catch {
    return 'Cambio editorial registrado';
  }
}
export type AuditModule = (typeof auditModules)[number]['id'];
export type AuditRow = {
  id: string;
  actorId: string;
  objectId: string;
  action: string;
  createdUtc: string;
  description: string;
};
export type AuditPage = { items: AuditRow[]; total: number; page: number; pageSize: number };
function invalid(): never {
  throw new ApiError(0, 'invalid_response');
}
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
export function auditPageFrom(value: unknown, module: AuditModule): AuditPage {
  const config = auditModules.find((item) => item.id === module)!;
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
  const items = value['items'].map((item): AuditRow => {
    if (
      !object(item) ||
      !['id', 'actorId', 'action', 'createdUtc', config.objectKey].every(
        (key) => typeof item[key] === 'string',
      ) ||
      !Number.isFinite(Date.parse(item['createdUtc'] as string))
    )
      invalid();
    let description: string;
    if (module === 'users') {
      if (
        !object(item['changes']) ||
        !Array.isArray(item['changes']['fields']) ||
        !item['changes']['fields'].every((field) => typeof field === 'string')
      )
        invalid();
      description =
        item['changes']['fields']
          .map((field) => fieldLabels[field as string])
          .filter(Boolean)
          .join(', ') || 'Cambio de cuenta registrado';
    } else if (module === 'content') {
      if (typeof item['changes'] !== 'string') invalid();
      description = editorialDescription(item['changes']);
    } else {
      if (
        typeof item['reason'] !== 'string' ||
        typeof item['afterStatus'] !== 'string' ||
        !(item['beforeStatus'] === null || typeof item['beforeStatus'] === 'string')
      )
        invalid();
      const planDescription = (prefix: 'before' | 'after') => {
        const plan = item[prefix + 'Plan'];
        const starts = item[prefix + 'StartsUtc'];
        const expires = item[prefix + 'ExpiresUtc'];
        if (plan === null || plan === undefined) return '';
        if (typeof plan !== 'string' || !Object.hasOwn(subscriptionPlanLabels, plan) ||
          !(starts === null || (typeof starts === 'string' && Number.isFinite(Date.parse(starts)))) ||
          !(expires === null || (typeof expires === 'string' && Number.isFinite(Date.parse(expires))))) invalid();
        return subscriptionPlanLabels[plan as SubscriptionPlan] +
          (starts ? ', desde ' + subscriptionInstant(starts as string) : '') +
          (expires ? ', hasta ' + subscriptionInstant(expires as string) : ', sin vencimiento');
      };
      const beforePlan = planDescription('before');
      const afterPlan = planDescription('after');
      description =
        (item['beforeStatus'] ? item['beforeStatus'] + ' → ' : '') +
        item['afterStatus'] +
        (afterPlan ? ' · ' + (beforePlan ? beforePlan + ' → ' : '') + afterPlan + ' (hora de Lima)' : '') +
        (item['reason'] ? ': ' + item['reason'] : '');
    }
    return {
      id: item['id'] as string,
      actorId: item['actorId'] as string,
      objectId: item[config.objectKey] as string,
      action: item['action'] as string,
      createdUtc: item['createdUtc'] as string,
      description,
    };
  });
  return {
    items,
    total: value['total'] as number,
    page: value['page'] as number,
    pageSize: value['pageSize'] as number,
  };
}
export const audit = {
  list: async (module: AuditModule, params: URLSearchParams) => {
    const config = auditModules.find((item) => item.id === module)!;
    const query = new URLSearchParams();
    for (const key of ['fromUtc', 'toUtc', 'action', config.objectKey, 'page']) {
      const value = params.get(key);
      if (value) query.set(key, value);
    }
    query.set('pageSize', '20');
    return auditPageFrom(await request('/admin/' + module + '/audit?' + query), module);
  },
};
