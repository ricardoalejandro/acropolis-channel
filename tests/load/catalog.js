import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 50, duration: '60s',
  thresholds: {
    'http_req_duration{phase:catalog-list}': ['p(95)<500'],
    'http_req_duration{phase:catalog-detail}': ['p(95)<500'],
    http_req_failed: ['rate==0'], checks: ['rate==1'],
  },
};
const base = __ENV.BASE_URL;
function browse() {
  const list = http.get(base + '/api/v1/catalog/content?search=000&category=videos&page=2&pageSize=20', { tags: { phase: 'catalog-list' }, timeout: '5s' });
  check(list, {
    'real catalogue filter and pagination': (r) => r.status === 200 && r.json('page') === 2 && r.json('items').length === 20 && r.json('total') > 20 && r.json('items').every((item) => item.category === 'videos' && typeof item.slug === 'string'),
    'list never exposes editorial state or private media fields': (r) => r.status === 200 && r.json('items').every((item) => !('body' in item) && !('version' in item) && !('status' in item) && !('mediaUrl' in item) && !('workText' in item) && !('youTubeId' in item)),
  });
  const detail = http.get(base + '/api/v1/catalog/content/qa-catalog-000000', { tags: { phase: 'catalog-detail' }, timeout: '5s' });
  check(detail, { 'published editorial description is available': (r) => r.status === 200 && r.json('slug') === 'qa-catalog-000000' && typeof r.json('body') === 'string' && !('workText' in r.json()) && !('youTubeId' in r.json()) });
}
export function setup() {
  if (!base?.startsWith('https://qa-') || !base.includes('.test:8443')) throw new Error('Catalogue load requires isolated QA HTTPS.');
  for (let index = 0; index < 5; index += 1) { browse(); sleep(0.2); }
}
export default function () { browse(); sleep(1); }
