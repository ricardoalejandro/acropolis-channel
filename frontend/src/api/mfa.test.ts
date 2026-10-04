import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authenticatorCode, enrollmentFrom, mfa, recoveryFrom, statusFrom } from './mfa';
import { ApiError, challengeFrom, clearCsrf, identity } from './identity';
import { json, user } from '../test/fixtures';
import { challenge, enrollment, recoveryCodes } from '../test/mfaFixtures';
beforeEach(clearCsrf);
describe('Two-factor contracts and credential boundaries', () => {
  it('distinguishes login challenges from full sessions and validates their complete shape', async () => {
    const value = challenge();
    expect(challengeFrom(value)).toEqual(value);
    for (const invalid of [
      null,
      {},
      { ...value, mfaRequired: false },
      { ...value, enrollmentRequired: 'yes' },
      { ...value, challengeToken: '' },
      { ...value, challengeToken: 'x'.repeat(4097) },
      { ...value, expiresUtc: 'no' },
    ])
      expect(() => challengeFrom(invalid)).toThrow(ApiError);
    vi.stubGlobal(
      'fetch',
      vi.fn((path: string) =>
        Promise.resolve(path.endsWith('/csrf') ? json({ token: 'csrf' }) : json(value, 202)),
      ),
    );
    expect(await identity.login('qa@example.test', 'password')).toEqual(value);
  });
  it('validates status and ephemeral enrollment without accepting remote provisioning URLs', () => {
    expect(statusFrom({ enabled: true, required: false, recoveryCodesLeft: 10 })).toBeTruthy();
    for (const value of [
      null,
      { enabled: 'true', required: false, recoveryCodesLeft: 10 },
      { enabled: true, required: 0, recoveryCodesLeft: 10 },
      { enabled: true, required: false, recoveryCodesLeft: -1 },
      { enabled: true, required: false, recoveryCodesLeft: 1.5 },
    ])
      expect(() => statusFrom(value)).toThrow(ApiError);
    const value = enrollment();
    expect(enrollmentFrom(value)).toEqual(value);
    for (const invalid of [
      null,
      {},
      { ...value, sharedKey: '' },
      { ...value, challengeToken: 'x'.repeat(4097) },
      { ...value, sharedKey: 'x'.repeat(257) },
      { ...value, authenticatorUri: 'https://example.test/qr' },
      { ...value, expiresUtc: 'no' },
    ])
      expect(() => enrollmentFrom(invalid)).toThrow(ApiError);
  });
  it('rejects incomplete or duplicated recovery lists and malformed six-digit codes', () => {
    expect(recoveryFrom({ recoveryCodes })).toEqual({ recoveryCodes });
    for (const value of [
      null,
      {},
      { recoveryCodes: recoveryCodes.slice(1) },
      { recoveryCodes: Array(10).fill('same') },
      { recoveryCodes: [...recoveryCodes.slice(1), ''] },
      { recoveryCodes: [...recoveryCodes.slice(1), 1] },
      { recoveryCodes: [...recoveryCodes.slice(1), 'x'.repeat(101)] },
    ])
      expect(() => recoveryFrom(value)).toThrow(ApiError);
    expect(authenticatorCode('123 456')).toBe('123456');
    for (const code of ['', '12', 'abcdef', '1234567'])
      expect(() => authenticatorCode(code)).toThrow(ApiError);
  });
  it('maps enrollment, enable, challenge and reauthentication and refreshes CSRF after session changes', async () => {
    const fetch = vi.fn((path: string) =>
      Promise.resolve(
        path.endsWith('/csrf')
          ? json({ token: 'fresh' })
          : path.endsWith('/mfa')
            ? json({ enabled: true, required: false, recoveryCodesLeft: 10 })
            : path.endsWith('/enrollment')
              ? json(enrollment())
              : path.endsWith('/enable')
                ? json({ user, recoveryCodes })
                : path.endsWith('/recovery-codes')
                  ? json({ recoveryCodes })
                  : path.endsWith('/disable')
                    ? new Response(null, { status: 204 })
                    : json(user),
      ),
    );
    vi.stubGlobal('fetch', fetch);
    expect((await mfa.status()).enabled).toBe(true);
    await mfa.enrollment({ currentPassword: 'correct password' });
    await mfa.enrollment({ challengeToken: 'login-challenge' });
    expect((await mfa.enable('challenge', '123456')).recoveryCodes).toHaveLength(10);
    await mfa.verify('challenge', { code: '123456' });
    await mfa.verify('challenge', { recoveryCode: 'unused-code' });
    await mfa.regenerate({ currentPassword: 'password', code: '123456' });
    await mfa.regenerate({ currentPassword: 'password', recoveryCode: 'unused' });
    await mfa.disable({ currentPassword: 'password', code: '123456' });
    await mfa.disable({ currentPassword: 'password', recoveryCode: 'unused' });
    const calls = fetch.mock.calls as unknown as [string, RequestInit][];
    expect(calls.filter(([path]) => path.endsWith('/csrf'))).toHaveLength(7);
    const verify = calls.find(
      ([path, options]) =>
        path.endsWith('/challenge') && String(options.body).includes('unused-code'),
    );
    expect(JSON.parse(String(verify?.[1].body))).toEqual({
      challengeToken: 'challenge',
      recoveryCode: 'unused-code',
    });
    expect(
      calls
        .filter(([, options]) => options?.method === 'POST')
        .every(
          ([, options]) => (options.headers as Record<string, string>)['X-CSRF-TOKEN'] === 'fresh',
        ),
    ).toBe(true);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
  it('does not accept recovery codes as a full authenticated user or send malformed codes', async () => {
    const fetch = vi.fn((path: string) =>
      Promise.resolve(
        path.endsWith('/csrf') ? json({ token: 'csrf' }) : json({ recoveryCodes, user: null }),
      ),
    );
    vi.stubGlobal('fetch', fetch);
    await expect(mfa.enable('challenge', '123456')).rejects.toMatchObject({
      code: 'invalid_response',
    });
    await expect(mfa.verify('challenge', { code: '12' })).rejects.toMatchObject({
      code: 'mfa_invalid_code',
    });
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
