import fs from 'node:fs';
import http from 'node:http';

if (process.env.LOCAL_RUNTIME !== 'acropolis-channel-local') {
  throw new Error('CRL service requires the isolated local runtime.');
}
const crl = fs.readFileSync('/local-pki/crl/root.crl');
if (crl.length === 0) throw new Error('Missing signed local certificate revocation list.');

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
    response.writeHead(404, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
    response.end('Not Found');
  }
}).listen(8082, '0.0.0.0');
