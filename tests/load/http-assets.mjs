const base = process.env.BASE_URL;
if (!base?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) {
  throw new Error('Asset gate requires the isolated Production HTTPS candidate.');
}
function ensure(value, message) { if (!value) throw new Error(message); }
async function get(path) {
  return fetch(base + path, { headers: { 'Accept-Encoding': 'gzip' }, signal: AbortSignal.timeout(10000) });
}
function security(response) {
  ensure(response.headers.get('X-Content-Type-Options') === 'nosniff', 'Missing nosniff header.');
  ensure(response.headers.get('Referrer-Policy') === 'no-referrer', 'Missing private referrer policy.');
  ensure(response.headers.get('X-Frame-Options') === 'DENY', 'Missing frame protection.');
  const csp = response.headers.get('Content-Security-Policy') ?? '';
  ensure(csp.includes("default-src 'self'") && csp.includes("frame-ancestors 'none'") && csp.includes("object-src 'none'"), 'Missing candidate content security policy.');
}
const home = await get('/');
ensure(home.status === 200, 'Missing Production HTML entry.');
security(home);
ensure((home.headers.get('Cache-Control') ?? '').includes('no-cache'), 'HTML entry must revalidate its build reference.');
const html = await home.text();
const assets = [...new Set([...html.matchAll(/(?:src|href)="(\/assets\/[^"]+\.(?:js|css))"/g)].map((match) => match[1]))];
ensure(assets.length >= 2, 'Missing separate candidate script and stylesheet assets.');
for (const path of assets) {
  ensure(/-[A-Za-z0-9_-]{6,}\.(?:js|css)$/.test(path), 'Asset URL is not versioned by its build hash.');
  const response = await get(path);
  ensure(response.status === 200, 'Candidate hashed asset is missing.');
  security(response);
  ensure(response.headers.get('Content-Encoding') === 'gzip', 'Public text asset was not compressed with gzip.');
  ensure((response.headers.get('Vary') ?? '').toLowerCase().split(',').some((value) => value.trim() === 'accept-encoding'), 'Compressed asset must vary by Accept-Encoding.');
  const cache = response.headers.get('Cache-Control') ?? '';
  ensure(cache.includes('public') && cache.includes('immutable') && Number(cache.match(/max-age=(\d+)/)?.[1]) >= 31536000, 'Hashed asset must have an immutable one-year cache.');
  ensure((await response.text()).length > 0, 'Compressed asset did not decode.');
}
for (const path of ['/api/v1/identity/csrf', '/api/v1/identity/me', '/api/v1/admin/users']) {
  const response = await get(path);
  ensure(path.endsWith('/csrf') ? response.status === 200 : response.status === 401, 'Unexpected anonymous Identity HTTP boundary.');
  ensure(response.headers.get('Cache-Control') === 'no-store', 'Identity response must never be cached.');
  ensure(response.headers.get('Content-Encoding') === null, 'Identity response must not compress secrets or reflected values.');
  security(response);
}
console.log('Production assets validate gzip/Vary/immutable hashes; HTML revalidates and Identity remains private and uncompressed.');
