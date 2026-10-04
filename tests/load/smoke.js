import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 20,
  duration: '60s',
  thresholds: {
    'http_req_duration{phase:baseline}': ['p(95)<500'],
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
  },
};

function visit(phase) {
  const parameters = { tags: { phase }, timeout: '5s' };
  const greeting = http.get(`${__ENV.BASE_URL}/api/v1/greeting`, parameters);
  check(greeting, {
    'greeting status is 200': (response) => response.status === 200,
    'real greeting contract': (response) => {
      try { return response.json('message') === 'Hola mundo'; } catch { return false; }
    },
  });
  const ready = http.get(`${__ENV.BASE_URL}/health/ready`, parameters);
  check(ready, {
    'readiness status is 200': (response) => response.status === 200,
    'readiness contract is ok': (response) => {
      try { return response.json('status') === 'ok'; } catch { return false; }
    },
  });
}

export function setup() {
  for (let iteration = 0; iteration < 5; iteration += 1) {
    visit('warmup');
    sleep(0.2);
  }
}

export default function () {
  visit('baseline');
  sleep(1);
}
