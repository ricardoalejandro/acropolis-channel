import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';

const homePage = readFileSync(new URL('./public/index.html', import.meta.url));
const configuredPort = process.env.PORT ?? '3000';
const port = Number(configuredPort);

if (!/^\d+$/.test(configuredPort) || !Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error('PORT debe ser un puerto válido entre 1 y 65535.');
}

const server = createServer((request, response) => {
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Referrer-Policy', 'no-referrer');
  response.setHeader('Content-Security-Policy', "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD', 'Content-Type': 'text/plain; charset=utf-8' });
    response.end('Método no permitido.');
    return;
  }

  let pathname;
  try {
    pathname = new URL(request.url, 'http://localhost').pathname;
  } catch {
    response.writeHead(400, { 'Content-Type': 'text/plain; charset=utf-8' });
    response.end('Solicitud no válida.');
    return;
  }

  if (pathname === '/favicon.ico') {
    response.writeHead(204);
    response.end();
    return;
  }

  let body;
  let status;
  let contentType;

  if (pathname === '/') {
    body = homePage;
    status = 200;
    contentType = 'text/html; charset=utf-8';
  } else if (pathname === '/health') {
    body = Buffer.from(JSON.stringify({ status: 'ok', service: 'acropolis-channel' }));
    status = 200;
    contentType = 'application/json; charset=utf-8';
  } else {
    body = Buffer.from('Página no encontrada.');
    status = 404;
    contentType = 'text/plain; charset=utf-8';
  }

  response.writeHead(status, {
    'Content-Type': contentType,
    'Content-Length': body.length,
    'Cache-Control': 'no-store',
  });
  response.end(request.method === 'HEAD' ? undefined : body);
});

server.listen(port, '0.0.0.0', () => {
  console.log(`Acropolis Channel escuchando en el puerto ${port}.`);
});

for (const signal of ['SIGTERM', 'SIGINT']) {
  process.once(signal, () => {
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(1), 10_000).unref();
  });
}
