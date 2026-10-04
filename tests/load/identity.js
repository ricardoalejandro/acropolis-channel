import http from 'k6/http';
import crypto from 'k6/crypto';
import { check, sleep } from 'k6';

export const options = {
  vus: 50, duration: '60s', setupTimeout: '240s',
  thresholds: {
    'http_req_duration{phase:identity-read}': ['p(95)<500'],
    'http_req_duration{phase:admin-read}': ['p(95)<500'],
    http_req_failed: ['rate==0'], checks: ['rate==1'],
  },
};
const base = __ENV.BASE_URL;
const authenticatorRegistry = JSON.parse(open('/artifacts/mfa-fixtures.json'));
function adminCode(email) {
  const fixture = authenticatorRegistry[base + ':' + email];
  if (!fixture?.key) throw new Error('Private QA admin authenticator fixture missing.');
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; const decoded = []; let value = 0; let bits = 0;
  for (const character of fixture.key) { value = (value << 5) | alphabet.indexOf(character); bits += 5; if (bits >= 8) { bits -= 8; decoded.push((value >> bits) & 255); } }
  for (let retry = 0; retry < 90; retry++) {
    for (const delta of [0, 1, 2, -1, -2]) {
      const step = Math.floor(Date.now() / 30000) + delta; const counter = new ArrayBuffer(8); const view = new DataView(counter);
      view.setUint32(0, Math.floor(step / 4294967296)); view.setUint32(4, step >>> 0);
      const digest = crypto.hmac('sha1', new Uint8Array(decoded).buffer, counter, 'hex');
      const bytes = digest.match(/../g).map((hex) => parseInt(hex, 16)); const offset = bytes[19] & 15;
      const number = ((bytes[offset] & 127) * 16777216 + bytes[offset + 1] * 65536 + bytes[offset + 2] * 256 + bytes[offset + 3]) % 1000000;
      const code = String(number).padStart(6, '0'); const hash = crypto.sha256(code, 'hex');
      if (!fixture.used.some((item) => item.hash === hash && item.at > Date.now() - 180000)) return code;
    }
    sleep(1);
  }
  throw new Error('QA authenticator window exhausted.');
}

function parameters(session, phase) {
  return { headers: { Cookie: '__Host-acropolis-session=' + session }, tags: { phase }, timeout: '5s' };
}
function read(session, phase) {
  const me = http.get(base + '/api/v1/identity/me', parameters(session, phase));
  check(me, { 'session authorized against real PostgreSQL': (r) => r.status === 200 && r.json('emailConfirmed') === true });
  const ready = http.get(base + '/health/ready', { tags: { phase }, timeout: '5s' });
  check(ready, { 'all three module histories ready': (r) => r.status === 200 && r.json('status') === 'ok' });
}
function adminRead(session) {
  const users = http.get(base + '/api/v1/admin/users?search=qa-load-0999&status=active&level=Externo&page=1&pageSize=20', parameters(session, 'admin-read'));
  check(users, { 'admin filter reads the 100000-account fixture': (r) => r.status === 200 && r.json('total') === 100 && r.json('items').length === 20 });
}
export function setup() {
  if (!base.startsWith('https://qa-') || !base.includes('.test:8443') || !__ENV.QA_SEED_PASSWORD) {
    throw new Error('Performance fixture requires isolated QA HTTPS and a QA password.');
  }
  const sessions = [];
  for (let user = 0; user < 50; user += 1) {
    // Respect the Production 30/minute login limiter instead of weakening it.
    if (user === 25) sleep(61);
    http.cookieJar().clear(base);
    const csrf = http.get(base + '/api/v1/identity/csrf', { tags: { phase: 'setup' } });
    check(csrf, { 'CSRF generated for actual login': (r) => r.status === 200 && typeof r.json('token') === 'string' });
    let login = http.post(base + '/api/v1/identity/login', JSON.stringify({
      email: 'qa-load-' + String(user).padStart(6, '0') + '@example.test',
      password: __ENV.QA_SEED_PASSWORD,
    }), { headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.json('token'), Origin: base }, tags: { phase: 'setup' } });
    if (user === 0 && login.status === 202) {
      const challenge = login.json();
      if (challenge.enrollmentRequired) throw new Error('Admin must enroll through real E2E before load.');
      const secondCsrf = http.get(base + '/api/v1/identity/csrf', { tags: { phase: 'setup' } });
      login = http.post(base + '/api/v1/identity/mfa/challenge', JSON.stringify({ challengeToken: challenge.challengeToken, code: adminCode('qa-load-000000@example.test') }), {
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': secondCsrf.json('token'), Origin: base }, tags: { phase: 'setup' },
      });
    }
    check(login, { 'real synthetic account login' : (r) => r.status === 200 });
    const cookie = login.cookies['__Host-acropolis-session']?.[0]?.value;
    if (!cookie) throw new Error('Synthetic login did not issue its secure session cookie.');
    sessions.push(cookie);
  }
  http.cookieJar().clear(base);
  for (let iteration = 0; iteration < 15; iteration += 1) { adminRead(sessions[0]); sleep(0.1); }
  for (let iteration = 0; iteration < 5; iteration += 1) { read(sessions[iteration], 'warmup'); sleep(0.2); }
  return sessions;
}
export default function (sessions) {
  read(sessions[(__VU - 1) % sessions.length], 'identity-read');
  if (__VU === 1) adminRead(sessions[0]);
  sleep(1);
}
