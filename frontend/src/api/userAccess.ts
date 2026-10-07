import { ApiError, request } from './identity';
export type UserAccess = { lastSignInUtc: string | null };
export function userAccessFrom(value: unknown): UserAccess {
  if (
    typeof value !== 'object' ||
    value === null ||
    Array.isArray(value) ||
    !('lastSignInUtc' in value)
  )
    throw new ApiError(0, 'invalid_response');
  const timestamp = (value as Record<string, unknown>)['lastSignInUtc'];
  if (timestamp === null) return { lastSignInUtc: null };
  if (
    typeof timestamp !== 'string' ||
    !/^(?!0000)\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(timestamp) ||
    !Number.isFinite(Date.parse(timestamp)) ||
    new Date(timestamp).toISOString().slice(0, 19) !== timestamp.slice(0, 19)
  )
    throw new ApiError(0, 'invalid_response');
  return { lastSignInUtc: timestamp };
}
export async function userAccess(id: string, signal?: AbortSignal): Promise<UserAccess> {
  return userAccessFrom(
    await request('/admin/users/' + encodeURIComponent(id) + '/access', undefined, 'GET', {
      ...(signal ? { signal } : {}),
      cache: 'no-store',
    }),
  );
}
export function userAccessMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'mfa_required_for_admin')
      return 'Completa la verificación en dos pasos para consultar este registro.';
    if (error.status === 403) return 'Tu cuenta no tiene permiso para consultar este registro.';
    if (error.status === 401) return 'Ingresa de nuevo para consultar este registro.';
    if (error.status === 404) return 'No encontramos el registro de esta cuenta.';
    if (error.code === 'invalid_response')
      return 'No pudimos leer el registro del último ingreso. Inténtalo de nuevo.';
    if (error.status === 429)
      return 'Espera unos minutos antes de volver a consultar este registro.';
  }
  return 'No pudimos consultar el último ingreso. Inténtalo de nuevo.';
}
