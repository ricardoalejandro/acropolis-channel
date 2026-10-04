import fs from 'node:fs';
const mode = process.argv[2];
const base = process.env.BASE_URL;
if (!base?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) throw new Error('SMTP failure gate requires an isolated QA origin.');
const state = '/artifacts/smtp-recovery.json';
if (mode === 'queue') {
  const email = 'smtp-failure-' + Date.now() + '@acropolis.test';
  const csrf = await fetch(base + '/api/v1/identity/csrf');
  if (csrf.status !== 200) throw new Error('Missing real anti forgery token.');
  const token = (await csrf.json()).token;
  const cookie = csrf.headers.getSetCookie().map((item) => item.split(';')[0]).join('; ');
  const response = await fetch(base + '/api/v1/identity/register', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Cookie: cookie, 'X-CSRF-TOKEN': token, Origin: base },
    body: JSON.stringify({ displayName: 'SMTP failure QA', email, password: process.env.QA_IDENTITY_PASSWORD }),
    signal: AbortSignal.timeout(10000),
  });
  if (response.status !== 202) throw new Error('SMTP outage did not preserve the accepted registration boundary.');
  fs.writeFileSync(state, JSON.stringify({ email }), { mode: 0o600 });
  console.log('Real registration accepted while QA SMTP was offline.');
} else if (mode === 'delivered') {
  const { email } = JSON.parse(fs.readFileSync(state, 'utf8'));
  const deadline = Date.now() + 120000;
  let delivered = false;
  do {
    const response = await fetch(process.env.MAILPIT_URL + '/api/v1/messages?limit=200', { signal: AbortSignal.timeout(5000) });
    if (response.status === 200) delivered = (await response.json()).messages.some((message) => message.To.some((recipient) => recipient.Address === email));
    if (delivered) break;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  } while (Date.now() < deadline);
  if (!delivered) throw new Error('Queued real email was not delivered after SMTP recovery.');
  console.log('Queued registration email delivered after authenticated implicit TLS recovery.');
} else throw new Error('Unknown SMTP failure gate.');
