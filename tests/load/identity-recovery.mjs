import fs from 'node:fs';

const mode = process.argv[2];
const base = process.env.BASE_URL;
const mailpit = process.env.MAILPIT_URL;
const password = process.env.QA_IDENTITY_PASSWORD;
if (!base?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_') || !password) {
  throw new Error('Recovery requires isolated QA HTTPS and credentials.');
}
const email = 'qa-load-000000@example.test';
const cookies = new Map();
async function request(route, data) {
  if (data !== undefined) {
    const csrf = await request('/api/v1/identity/csrf');
    if (csrf.status !== 200) throw new Error('Recovery CSRF unavailable.');
    const token = (await csrf.json()).token;
    return send(route, data, token);
  }
  return send(route);
}
async function send(route, data, token) {
  const response = await fetch(base + route, {
    method: data === undefined ? 'GET' : 'POST',
    headers: {
      Cookie: [...cookies].map(([name, value]) => name + '=' + value).join('; '),
      ...(data === undefined ? {} : { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token, Origin: base }),
    },
    ...(data === undefined ? {} : { body: JSON.stringify(data) }),
    redirect: 'manual', signal: AbortSignal.timeout(10000),
  });
  for (const item of response.headers.getSetCookie()) {
    const pair = item.split(';')[0];
    const split = pair.indexOf('=');
    cookies.set(pair.slice(0, split), pair.slice(split + 1));
  }
  return response;
}
async function expectStatus(response, expected) {
  if (response.status !== expected) throw new Error('Recovered account HTTP boundary differs from expected status ' + expected + '.');
}
async function newLink(route) {
  const deadline = Date.now() + 60000;
  do {
    const messages = await fetch(mailpit + '/api/v1/messages?limit=200', { signal: AbortSignal.timeout(5000) });
    for (const message of (await messages.json()).messages) {
      if (!message.To.some((recipient) => recipient.Address === email)) continue;
      const detail = await fetch(mailpit + '/api/v1/message/' + message.ID, { signal: AbortSignal.timeout(5000) });
      const content = await detail.json();
      const urls = (content.Text + ' ' + content.HTML).replaceAll('&amp;', '&').match(/https:\/\/[^\s"'<>]+/g) ?? [];
      const value = urls.find((url) => new URL(url).pathname === route);
      if (value) {
        const link = new URL(value);
        if (link.origin !== base) throw new Error('Recovery message origin differs from private QA.');
        const flow = new URLSearchParams(link.hash.slice(1));
        if (!flow.get('userId') || !flow.get('token')) throw new Error('Recovery message lacks a fresh real token.');
        return { userId: flow.get('userId'), token: flow.get('token') };
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  } while (Date.now() < deadline);
  throw new Error('Recovery did not deliver the fresh authenticated TLS email.');
}
if (mode === 'blocked') {
  await expectStatus(await request('/api/v1/identity/login', { email, password }), 401);
  await expectStatus(await request('/api/v1/admin/users'), 401);
  await expectStatus(await request('/api/v1/identity/mfa'), 401);
  console.log('Restored former administrator remains quarantined and cannot authenticate.');
} else if (mode === 'revalidated') {
  await expectStatus(await request('/api/v1/identity/login', { email, password }), 401);
  await expectStatus(await request('/api/v1/identity/resend-confirmation', { email }), 202);
  await expectStatus(await request('/api/v1/identity/confirm-email', await newLink('/confirm-email')), 204);
  await expectStatus(await request('/api/v1/identity/login', { email, password }), 401);
  await expectStatus(await request('/api/v1/identity/forgot-password', { email }), 202);
  const flow = await newLink('/reset-password');
  await expectStatus(await request('/api/v1/identity/reset-password', { ...flow, newPassword: password + 'Recovery' }), 204);
  await expectStatus(await request('/api/v1/identity/login', { email, password }), 401);
  const login = await request('/api/v1/identity/login', { email, password: password + 'Recovery' });
  await expectStatus(login, 200);
  const account = await login.json();
  if (!account.emailConfirmed || account.status !== 'active' || account.permissions.length !== 0 ||
      JSON.stringify(account.levels) !== JSON.stringify(['Externo'])) {
    throw new Error('Recovery resurrected administrative or institutional privileges.');
  }
  await expectStatus(await request('/api/v1/admin/users'), 403);
  const mfa = await request('/api/v1/identity/mfa');
  await expectStatus(mfa, 200);
  const mfaState = await mfa.json();
  if (mfaState.enabled || mfaState.required || mfaState.recoveryCodesLeft !== 0) throw new Error('Recovery resurrected prior MFA material.');
  await expectStatus(await request('/api/v1/identity/reset-password', { ...flow, newPassword: password + 'Reused' }), 400);
  fs.writeFileSync('/artifacts/recovery-result.json', JSON.stringify({ status: 'passed', previousPrivilegesRestored: false }), { mode: 0o600 });
  console.log('Explicitly revalidated account requires fresh confirmation/password and receives no old privileges.');
} else throw new Error('Unknown recovery gate.');
