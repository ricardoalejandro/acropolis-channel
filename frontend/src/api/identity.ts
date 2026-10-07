export const levels = [
  'Externo',
  'Probacionista',
  'Miembro',
  'FFVV',
  'Instructor',
  'Hachado',
] as const;
export type Level = (typeof levels)[number];
export type User = {
  id: string;
  displayName: string;
  email: string;
  emailConfirmed: boolean;
  status: 'pending' | 'active' | 'disabled';
  levels: Level[];
  permissions: string[];
  version: string;
  isOwner?: boolean;
};
export type MfaChallenge = {
  mfaRequired: true;
  enrollmentRequired: boolean;
  challengeToken: string;
  expiresUtc: string;
};
export function challengeFrom(value: unknown): MfaChallenge {
  if (
    !isObject(value) ||
    value['mfaRequired'] !== true ||
    typeof value['enrollmentRequired'] !== 'boolean' ||
    typeof value['challengeToken'] !== 'string' ||
    !value['challengeToken'] ||
    value['challengeToken'].length > 4096 ||
    typeof value['expiresUtc'] !== 'string' ||
    !Number.isFinite(Date.parse(value['expiresUtc']))
  )
    throw new ApiError(0, 'invalid_response');
  return value as MfaChallenge;
}
export type UserPage = { items: User[]; page: number; pageSize: number; total: number };
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly fields: Record<string, string[]> = {},
  ) {
    super(errorMessage(code));
    this.name = 'ApiError';
  }
}
export function errorMessage(code: string): string {
  const messages: Record<string, string> = {
    invalid_credentials: 'No pudimos ingresar con esos datos. Revisa tu correo y contraseña.',
    mfa_invalid_code:
      'El código no es válido. Usa un código nuevo de tu autenticador o un código de recuperación sin utilizar.',
    challenge_invalid: 'Esta verificación ha caducado. Ingresa de nuevo para continuar.',
    mfa_required_for_admin:
      'Las cuentas administrativas deben conservar la verificación en dos pasos.',
    mfa_already_enabled: 'La verificación en dos pasos ya está activa.',
    mfa_not_enabled: 'La verificación en dos pasos todavía no está activa.',
    current_password_invalid: 'La contraseña actual no es correcta. Revísala e inténtalo de nuevo.',
    validation_error: 'Revisa los campos indicados e inténtalo de nuevo.',
    csrf_invalid: 'La sesión de seguridad ha caducado. Recarga la página e inténtalo de nuevo.',
    forbidden: 'Tu cuenta no tiene permiso para realizar esta acción.',
    not_found: 'No encontramos la información solicitada.',
    concurrency_conflict:
      'Otra persona actualizó este usuario. Recarga sus datos antes de guardar.',
    last_admin: 'Debe permanecer al menos un administrador activo.',
    owner_protected:
      'La cuenta propietaria está protegida y no se puede modificar desde esta pantalla.',
    account_not_active: 'Tu cuenta no tiene acceso activo. Contacta con Nueva Acrópolis.',
    subscription_required: 'Activa tu suscripción gratuita para acceder a esta obra.',
    subscription_suspended: 'Tu suscripción está suspendida. Contacta con Nueva Acrópolis.',
    invalid_token: 'Este enlace no es válido o ha caducado. Solicita uno nuevo.',
    timeout: 'La respuesta está tardando demasiado. Inténtalo de nuevo.',
    unavailable: 'No pudimos conectar. Comprueba tu conexión e inténtalo de nuevo.',
    invalid_response: 'No pudimos leer la respuesta. Inténtalo de nuevo.',
    email_unavailable:
      'El registro y la recuperación por correo no están disponibles temporalmente.',
    rate_limited: 'Has realizado varios intentos. Espera unos minutos y vuelve a intentarlo.',
  };
  return messages[code] ?? 'No pudimos completar la acción. Inténtalo de nuevo.';
}
let csrf: string | undefined;
let csrfPending: Promise<string> | undefined;
let csrfRevision = 0;
export function clearCsrf() {
  csrf = undefined;
  csrfPending = undefined;
  csrfRevision++;
}
function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
export function userFrom(value: unknown): User {
  if (
    !isObject(value) ||
    typeof value['id'] !== 'string' ||
    typeof value['displayName'] !== 'string' ||
    typeof value['email'] !== 'string' ||
    typeof value['emailConfirmed'] !== 'boolean' ||
    !['pending', 'active', 'disabled'].includes(String(value['status'])) ||
    !Array.isArray(value['levels']) ||
    !value['levels'].every((level) => levels.includes(level as Level)) ||
    !Array.isArray(value['permissions']) ||
    !value['permissions'].every((permission) => typeof permission === 'string') ||
    typeof value['version'] !== 'string' ||
    (value['isOwner'] !== undefined && typeof value['isOwner'] !== 'boolean')
  )
    throw new ApiError(0, 'invalid_response');
  return value as User;
}
async function csrfToken(): Promise<string> {
  if (csrf) return csrf;
  if (csrfPending) return csrfPending;
  const revision = csrfRevision;
  const pending = request('/identity/csrf').then((result) => {
    if (!isObject(result) || typeof result['token'] !== 'string' || !result['token'])
      throw new ApiError(0, 'invalid_response');
    if (revision !== csrfRevision) throw new ApiError(400, 'csrf_invalid');
    csrf = result['token'];
    return csrf;
  });
  csrfPending = pending;
  try {
    return await pending;
  } finally {
    if (csrfPending === pending) csrfPending = undefined;
  }
}
export async function request(
  path: string,
  body?: unknown,
  method = body === undefined ? 'GET' : 'POST',
  options?: { signal?: AbortSignal; cache?: RequestCache },
): Promise<unknown> {
  const token = method !== 'GET' ? await csrfToken() : undefined;
  const controller = new AbortController();
  const abort = () => controller.abort();
  options?.signal?.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(() => controller.abort(), 10000);
  try {
    if (options?.signal?.aborted) throw new ApiError(0, 'timeout');
    const response = await fetch('/api/v1' + path, {
      method,
      credentials: 'same-origin',
      signal: controller.signal,
      ...(options?.cache !== undefined ? { cache: options.cache } : {}),
      headers: {
        Accept: 'application/json',
        ...(method !== 'GET'
          ? { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token as string }
          : {}),
      },
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    });
    if (options?.signal?.aborted) throw new ApiError(0, 'timeout');
    if (response.status === 204) return undefined;
    let data: unknown;
    try {
      data = await response.json();
    } catch {
      throw new ApiError(response.status, response.ok ? 'invalid_response' : 'unavailable');
    }
    if (options?.signal?.aborted) throw new ApiError(0, 'timeout');
    if (!response.ok) {
      const code =
        isObject(data) && typeof data['code'] === 'string'
          ? data['code']
          : response.status === 429
            ? 'rate_limited'
            : 'unavailable';
      const fields: Record<string, string[]> = {};
      if (isObject(data) && isObject(data['fieldErrors']))
        for (const [name, values] of Object.entries(data['fieldErrors']))
          if (Array.isArray(values) && values.every((value) => typeof value === 'string'))
            fields[name] = values;
      if (response.status === 401 && path !== '/identity/login' && path !== '/identity/me')
        window.dispatchEvent(new Event('acropolis:session-expired'));
      if (code === 'csrf_invalid') clearCsrf();
      throw new ApiError(response.status, code, fields);
    }
    return data;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    throw new ApiError(0, controller.signal.aborted ? 'timeout' : 'unavailable');
  } finally {
    options?.signal?.removeEventListener('abort', abort);
    clearTimeout(timer);
  }
}
export const identity = {
  capabilities: async () => {
    const data = await request('/identity/capabilities');
    if (!isObject(data) || typeof data['emailEnabled'] !== 'boolean')
      throw new ApiError(0, 'invalid_response');
    return { emailEnabled: data['emailEnabled'] };
  },
  me: async () => userFrom(await request('/identity/me')),
  login: async (email: string, password: string) => {
    const data = await request('/identity/login', { email, password });
    const result =
      isObject(data) && data['mfaRequired'] === true ? challengeFrom(data) : userFrom(data);
    clearCsrf();
    return result;
  },
  logout: async () => {
    await request('/identity/logout', {});
    clearCsrf();
  },
  updateProfile: async (displayName: string) =>
    userFrom(await request('/identity/me', { displayName }, 'PATCH')),
  users: async (search: string, status: string, level: string, page: number): Promise<UserPage> => {
    const params = new URLSearchParams({ search });
    if (status) params.set('status', status);
    if (level) params.set('level', level);
    params.set('page', String(page));
    params.set('pageSize', '20');
    const result = await request('/admin/users?' + params);
    if (
      !isObject(result) ||
      !Array.isArray(result['items']) ||
      typeof result['total'] !== 'number' ||
      typeof result['page'] !== 'number' ||
      typeof result['pageSize'] !== 'number'
    )
      throw new ApiError(0, 'invalid_response');
    return {
      items: result['items'].map(userFrom),
      total: result['total'],
      page: result['page'],
      pageSize: result['pageSize'],
    };
  },
  user: async (id: string) => userFrom(await request('/admin/users/' + encodeURIComponent(id))),
  permissions: async (id: string, version: string, permissions: string[]) =>
    userFrom(
      await request(
        '/admin/users/' + encodeURIComponent(id) + '/permissions',
        { version, permissions },
        'PUT',
      ),
    ),
  updateUser: async (id: string, body: unknown) =>
    userFrom(await request('/admin/users/' + encodeURIComponent(id), body, 'PATCH')),
};
