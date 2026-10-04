import fs from 'node:fs';
import http from 'node:http';

if (!/^acropolis_test_[a-z0-9_]+$/.test(process.env.QA_PROJECT ?? '')) {
  throw new Error('CRL service requires its own isolated QA project.');
}
const crl = fs.readFileSync('/qa-crl/root.crl');
if (crl.length === 0) throw new Error('Missing signed QA certificate revocation list.');
http.createServer((request, response) => {
  if (request.url === '/health' && request.method === 'GET') {
    response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
    response.end('ok');
  } else if (request.url === '/root.crl' && (request.method === 'GET' || request.method === 'HEAD')) {
    response.writeHead(200, {
      'Content-Type': 'application/pkix-crl', 'Content-Length': crl.length,
      'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
    });
    response.end(request.method === 'HEAD' ? undefined : crl);
  } else {
    response.writeHead(404, { 'Content-Type': 'text/plain' });
    response.end('Not Found');
  }
}).listen(8082, '0.0.0.0');
