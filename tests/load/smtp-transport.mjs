import net from 'node:net';
import { pathToFileURL } from 'node:url';

const payload = [
  'EHLO qa-plaintext.acropolis.test',
  'MAIL FROM:<plaintext-probe@acropolis.test>',
  'RCPT TO:<plaintext-probe@acropolis.test>',
  'DATA',
  'Subject: Isolated plaintext rejection probe',
  '',
  'Synthetic QA transport probe.',
  '.',
  'QUIT',
  '',
].join('\r\n');

class ProbeFailure extends Error {
  constructor(reason) {
    super(reason);
    this.reason = reason;
  }
}

export function assertQaProject(project = process.env.QA_PROJECT) {
  if (typeof project !== 'string' || !/^acropolis_test_[a-z0-9_]+$/.test(project)) {
    throw new ProbeFailure('qa_project_required');
  }
}

// The connector injection is for isolated loopback tests only; the CLI has no
// endpoint override and always probes the TLS-only QA service mailpit:465.
export function probePlaintextRejection({ timeoutMs = 5000, connect = () => net.createConnection({ host: 'mailpit', port: 465 }) } = {}) {
  assertQaProject();
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) throw new ProbeFailure('invalid_timeout');
  return new Promise((resolve, reject) => {
    let socket;
    let connected = false;
    let sent = false;
    let finished = false;
    let response = '';
    const finish = (failure, result) => {
      if (finished) return;
      finished = true;
      clearTimeout(deadline);
      socket?.destroy();
      if (failure) reject(new ProbeFailure(failure));
      else resolve({ status: 'passed', mode: 'plaintext-rejected', endpoint: 'mailpit:465', result });
    };
    const deadline = setTimeout(() => finish('timeout_without_rejection'), timeoutMs);
    try {
      socket = connect();
      socket.on('connect', () => {
        connected = true;
        sent = true;
        socket.write(payload);
      });
      socket.on('data', (data) => {
        response += data.toString('latin1');
        // Keep accumulated data so a fragmented SMTP status/banner is caught.
        if (/(?:^|[\r\n])(?:220|250|354)(?=[ -]|[\r\n]|$)/.test(response)) {
          finish('plaintext_smtp_accepted');
        } else if (response.length > 8192) {
          finish('unexpected_response_size');
        }
      });
      socket.on('error', (error) => {
        if (connected && sent && (error.code === 'ECONNRESET' || error.code === 'EPIPE')) {
          finish(null, 'protocol_reset');
        } else {
          finish('connection_or_transport_failed');
        }
      });
      socket.on('end', () => {
        if (connected && sent) finish(null, 'closed_after_probe');
        else finish('closed_before_probe');
      });
      socket.on('close', () => {
        if (connected && sent) finish(null, 'closed_after_probe');
        else finish('closed_before_probe');
      });
    } catch {
      finish('connection_or_transport_failed');
    }
  });
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length !== 1 || args[0] !== 'plaintext-rejected') throw new ProbeFailure('invalid_mode');
  console.log(JSON.stringify(await probePlaintextRejection()));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(JSON.stringify({ status: 'failed', mode: 'plaintext-rejected', reason: error instanceof ProbeFailure ? error.reason : 'probe_failed' }));
    process.exitCode = 1;
  });
}
