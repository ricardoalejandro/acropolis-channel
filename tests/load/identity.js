import http from 'k6/http';
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
function parameters(session, phase) {
  return { headers: { Cookie: '__Host-acropolis-session=' + session }, tags: { phase }, timeout: '5s' };
}
function read(session, phase) {
  const me = http.get(base + '/api/v1/identity/me', parameters(session, phase));
  check(me, { 'session authorized against real PostgreSQL': (r) => r.status === 200 && r.json('emailConfirmed') === true });
  const ready = http.get(base + '/health/ready', { tags: { phase }, timeout: '5s' });
  check(ready, { 'both module histories ready': (r) => r.status === 200 && r.json('status') === 'ok' });
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
    const login = http.post(base + '/api/v1/identity/login', JSON.stringify({
      email: 'qa-load-' + String(user).padStart(6, '0') + '@example.test',
      password: __ENV.QA_SEED_PASSWORD,
    }), { headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.json('token'), Origin: base }, tags: { phase: 'setup' } });
    check(login, { 'real synthetic account login': (r) => r.status === 200 });
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
