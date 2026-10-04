import fs from 'node:fs';
const mode = process.argv[2];
const base = new URL(process.env.BASE_URL);
if (base.protocol !== 'https:' || !base.hostname.startsWith('qa-') || !base.hostname.endsWith('.test') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) throw new Error('Identity state gate requires private QA HTTPS.');
async function verify(name, expected) {
  const state = JSON.parse(fs.readFileSync('/artifacts/session-state/' + name + '.json', 'utf8'));
  const cookies = state.cookies.filter((cookie) => cookie.domain === base.hostname && cookie.secure);
  if (!cookies.some((cookie) => cookie.name === '__Host-acropolis-session')) throw new Error('Missing real secure browser session.');
  const response = await fetch(new URL('/api/v1/identity/me', base), {
    headers: { Cookie: cookies.map((cookie) => cookie.name + '=' + cookie.value).join('; ') },
    redirect: 'manual', signal: AbortSignal.timeout(5000),
  });
  if (response.status !== expected) throw new Error('Session state does not match its expected authorization boundary.');
}
if (mode === 'persistence') {
  await verify('active', 200);
  await verify('revoked', 401);
  console.log('Recreated app preserves its valid session and rejects the revoked one.');
} else if (mode === 'database-down') {
  await verify('active', 503);
  console.log('A valid session reports database unavailability without being treated as revoked.');
} else if (mode === 'recovery') {
  await verify('active', 401);
  await verify('revoked', 401);
  console.log('Restored app rejects all cookies captured before recovery.');
} else throw new Error('Unknown Identity state gate.');
