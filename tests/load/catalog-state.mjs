const base = process.env.BASE_URL;
if (!base?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) throw new Error('Isolated catalogue QA origin is required.');
for (const [slug, status] of [['qa-catalog-000000', 200], ['qa-catalog-000008', 404], ['qa-catalog-000009', 404]]) {
  const response = await fetch(base + '/api/v1/catalog/content/' + slug, { signal: AbortSignal.timeout(5000) });
  if (response.status !== status || !response.headers.get('cache-control')?.includes('no-store')) throw new Error('Catalogue publication boundary changed during persistence or recovery.');
  const body = await response.json();
  if (status === 200 && (body.slug !== slug || typeof body.body !== 'string' || 'version' in body || 'status' in body || 'mediaUrl' in body || 'workText' in body || 'youTubeId' in body)) throw new Error('Unexpected public catalogue fields.');
}
console.log('Published synopsis remains available; draft and archive stay unavailable after recovery.');
