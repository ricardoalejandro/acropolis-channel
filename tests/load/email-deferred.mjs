const base = process.env.BASE_URL;
if (!base?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) throw new Error('Isolated HTTPS QA configuration is required.');
const capabilities = await fetch(base + '/api/v1/identity/capabilities');
if (capabilities.status !== 200 || (await capabilities.json()).emailEnabled !== false || !capabilities.headers.get('cache-control')?.includes('no-store')) throw new Error('Explicit email deferral is not active.');
const csrf = await fetch(base + '/api/v1/identity/csrf');
if (csrf.status !== 200) throw new Error('Real anti forgery token is required.');
const token = (await csrf.json()).token;
const cookie = csrf.headers.getSetCookie().map((item) => item.split(';')[0]).join('; ');
for (const route of ['register', 'resend-confirmation', 'forgot-password']) {
  const response = await fetch(base + '/api/v1/identity/' + route, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Cookie: cookie, 'X-CSRF-TOKEN': token, Origin: base },
    body: JSON.stringify(route === 'register' ? { displayName: 'Deferred QA', email: 'deferred@acropolis.test', password: process.env.QA_IDENTITY_PASSWORD } : { email: 'deferred@acropolis.test' }),
    signal: AbortSignal.timeout(5000),
  });
  if (response.status !== 503 || (await response.json()).code !== 'email_unavailable' || !response.headers.get('cache-control')?.includes('no-store')) throw new Error('Email operation accepted while explicitly deferred.');
}
const catalogue = await fetch(base + '/api/v1/catalog/content');
if (catalogue.status !== 200 || (await catalogue.json()).total !== 0) throw new Error('Empty real catalogue must remain available without SMTP.');
console.log('Email operations remain disabled while the empty real catalogue is available.');
