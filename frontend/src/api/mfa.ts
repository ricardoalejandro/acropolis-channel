import { ApiError, clearCsrf, request, userFrom, type User } from './identity';
export type MfaStatus = { enabled: boolean; required: boolean; recoveryCodesLeft: number };
export type Enrollment = {
  challengeToken: string;
  sharedKey: string;
  authenticatorUri: string;
  expiresUtc: string;
};
export type RecoveryResult = { recoveryCodes: string[] };
export type EnableResult = RecoveryResult & { user: User };
export type Reauthentication = { currentPassword: string; code?: string; recoveryCode?: string };
function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
export function statusFrom(value: unknown): MfaStatus {
  if (
    !object(value) ||
    typeof value['enabled'] !== 'boolean' ||
    typeof value['required'] !== 'boolean' ||
    !Number.isInteger(value['recoveryCodesLeft']) ||
    Number(value['recoveryCodesLeft']) < 0
  )
    throw new ApiError(0, 'invalid_response');
  return value as MfaStatus;
}
export function enrollmentFrom(value: unknown): Enrollment {
  if (
    !object(value) ||
    !['challengeToken', 'sharedKey', 'authenticatorUri', 'expiresUtc'].every(
      (key) => typeof value[key] === 'string' && Boolean(value[key]),
    ) ||
    String(value['challengeToken']).length > 4096 ||
    String(value['sharedKey']).length > 256 ||
    !/^otpauth:\/\/totp\/[!-~]{1,2000}$/.test(String(value['authenticatorUri'])) ||
    !Number.isFinite(Date.parse(String(value['expiresUtc'])))
  )
    throw new ApiError(0, 'invalid_response');
  return value as Enrollment;
}
export function recoveryFrom(value: unknown): RecoveryResult {
  if (
    !object(value) ||
    !Array.isArray(value['recoveryCodes']) ||
    value['recoveryCodes'].length !== 10 ||
    !value['recoveryCodes'].every(
      (code) => typeof code === 'string' && code.length > 0 && code.length <= 100,
    ) ||
    new Set(value['recoveryCodes']).size !== 10
  )
    throw new ApiError(0, 'invalid_response');
  return { recoveryCodes: value['recoveryCodes'] as string[] };
}
export function authenticatorCode(value: string): string {
  const code = value.replace(/\s/g, '');
  if (!/^\d{6}$/.test(code)) throw new ApiError(400, 'mfa_invalid_code', { code: ['invalid'] });
  return code;
}
export const mfa = {
  status: async () => statusFrom(await request('/identity/mfa')),
  enrollment: async (body: { challengeToken: string } | { currentPassword: string }) =>
    enrollmentFrom(await request('/identity/mfa/enrollment', body)),
  enable: async (challengeToken: string, code: string): Promise<EnableResult> => {
    const data = await request('/identity/mfa/enable', {
      challengeToken,
      code: authenticatorCode(code),
    });
    const recovery = recoveryFrom(data);
    if (!object(data)) throw new ApiError(0, 'invalid_response');
    const user = userFrom(data['user']);
    clearCsrf();
    return { ...recovery, user };
  },
  verify: async (challengeToken: string, body: { code: string } | { recoveryCode: string }) => {
    const input = 'code' in body ? { code: authenticatorCode(body.code) } : body;
    const user = userFrom(await request('/identity/mfa/challenge', { challengeToken, ...input }));
    clearCsrf();
    return user;
  },
  regenerate: async (body: Reauthentication) => {
    const input = body.code !== undefined ? { ...body, code: authenticatorCode(body.code) } : body;
    const result = recoveryFrom(await request('/identity/mfa/recovery-codes', input));
    clearCsrf();
    return result;
  },
  disable: async (body: Reauthentication) => {
    const input = body.code !== undefined ? { ...body, code: authenticatorCode(body.code) } : body;
    await request('/identity/mfa/disable', input);
    clearCsrf();
  },
};
