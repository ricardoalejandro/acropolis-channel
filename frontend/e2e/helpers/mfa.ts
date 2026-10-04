import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';

interface QaAuthenticator {
  key: string;
  used: { hash: string; at: number }[];
}
type Registry = Record<string, QaAuthenticator>;
export interface QaUser {
  id: string;
  email: string;
  displayName: string;
  permissions: string[];
  version: string;
}
const directory = process.env['QA_MFA_STATE_DIR'] ?? '/artifacts';
const statePath = path.join(directory, 'mfa-fixtures.json');
function guard() {
  if (
    !process.env['QA_PROJECT']?.startsWith('acropolis_test_') ||
    !process.env['BASE_URL']?.startsWith('https://qa-')
  )
    throw new Error('Authenticator fixtures require an isolated HTTPS QA project.');
}
function read(): Registry {
  guard();
  return fs.existsSync(statePath)
    ? (JSON.parse(fs.readFileSync(statePath, 'utf8')) as Registry)
    : {};
}
function save(registry: Registry) {
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(statePath, JSON.stringify(registry), { mode: 0o600 });
  fs.chmodSync(statePath, 0o600);
}
function identity(email: string) {
  return process.env['BASE_URL'] + ':' + email;
}
export function totpQa(key: string, offset = 0): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bytes: number[] = [];
  let bits = 0;
  let value = 0;
  for (const character of key) {
    value = (value << 5) | alphabet.indexOf(character);
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((value >> bits) & 255);
    }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000) + offset));
  const hash = crypto.createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const position = hash[19]! & 15;
  return String((hash.readUInt32BE(position) & 0x7fffffff) % 1000000).padStart(6, '0');
}
export function invalidQaCode(email: string): string {
  const fixture = read()[identity(email)];
  if (!fixture) throw new Error('QA authenticator fixture missing.');
  const valid = new Set([-3, -2, -1, 0, 1, 2, 3].map((offset) => totpQa(fixture.key, offset)));
  for (let value = 0; value < 1000000; value++) {
    const candidate = String(value).padStart(6, '0');
    if (!valid.has(candidate)) return candidate;
  }
  throw new Error('No invalid QA code available.');
}
export function saveQaAuthenticator(email: string, key: string) {
  const registry = read();
  registry[identity(email)] = { key, used: [] };
  save(registry);
}
export function freshQaCode(email: string): string {
  const registry = read();
  const fixture = registry[identity(email)];
  if (!fixture) throw new Error('QA authenticator fixture unavailable.');
  fixture.used = fixture.used.filter((item) => item.at > Date.now() - 180000);
  for (const offset of [0, 1, 2, -1, -2]) {
    const code = totpQa(fixture.key, offset);
    const hash = crypto.createHash('sha256').update(code).digest('hex');
    if (!fixture.used.some((item) => item.hash === hash)) {
      fixture.used.push({ hash, at: Date.now() });
      save(registry);
      return code;
    }
  }
  throw new Error('QA used the current real TOTP window; wait for the next code.');
}
async function nextQaCode(email: string): Promise<string> {
  const deadline = Date.now() + 90000;
  do {
    try {
      return freshQaCode(email);
    } catch (error) {
      if (!(error instanceof Error) || !error.message.includes('current real TOTP window'))
        throw error;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
  } while (Date.now() < deadline);
  throw new Error('QA could not obtain a fresh real authenticator code.');
}
export async function mfaWrite(request: APIRequestContext, route: string, body: object) {
  const csrf = await request.get('/api/v1/identity/csrf');
  expect(csrf.status()).toBe(200);
  const token = ((await csrf.json()) as { token: string }).token;
  return request.post('/api/v1/identity/' + route, {
    headers: { 'X-CSRF-TOKEN': token },
    data: body,
  });
}
export async function loginQa(
  request: APIRequestContext,
  email: string,
  password: string,
): Promise<QaUser> {
  guard();
  const response = await mfaWrite(request, 'login', { email, password });
  if (response.status() === 200) return (await response.json()) as QaUser;
  expect(response.status()).toBe(202);
  const challenge = (await response.json()) as {
    enrollmentRequired: boolean;
    challengeToken: string;
  };
  if (challenge.enrollmentRequired) {
    const start = await mfaWrite(request, 'mfa/enrollment', {
      challengeToken: challenge.challengeToken,
    });
    expect(start.status()).toBe(200);
    const enrollment = (await start.json()) as { challengeToken: string; sharedKey: string };
    saveQaAuthenticator(email, enrollment.sharedKey);
    const enabled = await mfaWrite(request, 'mfa/enable', {
      challengeToken: enrollment.challengeToken,
      code: await nextQaCode(email),
    });
    expect(enabled.status()).toBe(200);
    return ((await enabled.json()) as { user: QaUser }).user;
  }
  const verified = await mfaWrite(request, 'mfa/challenge', {
    challengeToken: challenge.challengeToken,
    code: await nextQaCode(email),
  });
  expect(verified.status()).toBe(200);
  return (await verified.json()) as QaUser;
}
