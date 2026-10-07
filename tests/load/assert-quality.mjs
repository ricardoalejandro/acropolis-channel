import fs from 'node:fs';
import path from 'node:path';

const [mode, input, value, expectedAssembly] = process.argv.slice(2);
function fail(message) { console.error(message); process.exit(1); }
function files(directory, suffix) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((item) => {
    const target = path.join(directory, item.name);
    return item.isDirectory() ? files(target, suffix) : item.name.endsWith(suffix) ? [target] : [];
  });
}
if (mode === 'coverage') {
  const reports = files(input, 'coverage.cobertura.xml');
  if (reports.length === 0) fail('Missing scoped backend coverage report.');
  const threshold = Number(value);
  if (!Number.isFinite(threshold) || threshold < 0 || threshold > 100) fail('Invalid coverage threshold.');
  const observed = new Set();
  for (const report of reports) {
    const packages = [...fs.readFileSync(report, 'utf8').matchAll(/<package\s[^>]*name="([^"]+)"/g)].map((match) => match[1]);
    for (const name of packages) observed.add(name);
    const xml = fs.readFileSync(report, 'utf8');
    const root = xml.match(/<coverage\s[^>]*>/)?.[0];
    const rate = Number(root?.match(/line-rate="([\d.]+)"/)?.[1]);
    const valid = Number(root?.match(/lines-valid="(\d+)"/)?.[1]);
    const branchRate = Number(root?.match(/branch-rate="([\d.]+)"/)?.[1]);
    const branchesValid = Number(root?.match(/branches-valid="(\d+)"/)?.[1]);
    if (![rate, valid, branchRate, branchesValid].every(Number.isFinite) || valid < 1 || branchesValid < 0 || rate < 0 || rate > 1 || branchRate < 0 || branchRate > 1) {
      fail('Coverage report is missing valid line/branch metrics.');
    }
    if (rate * 100 < threshold) fail(`Scoped backend line coverage must be >= ${threshold}%; got ${rate * 100}%.`);
    if (branchesValid > 0 && branchRate * 100 < threshold) {
      fail(`Scoped backend branch coverage must be >= ${threshold}%; got ${branchRate * 100}%.`);
    }
    if (branchesValid === 0) console.log('Branch coverage not applicable: the report has no branches.');
  }
  if (expectedAssembly && !observed.has(expectedAssembly)) fail(`Missing required coverage assembly: ${expectedAssembly}.`);
  console.log(`Scoped backend coverage passed >= ${threshold}% in lines and applicable branches.`);
} else if (mode === 'migration-history') {
  const observed = JSON.parse(fs.readFileSync(input, 'utf8'));
  const expected = JSON.parse(fs.readFileSync(value, 'utf8'));
  for (const module of ['platform', 'identity', 'catalog', 'subscriptions']) {
    if (!Array.isArray(expected[module]) || expected[module].length === 0) fail(`Missing expected ${module} migrations.`);
    if (!Array.isArray(observed[module])) fail(`Missing ${module} migration history.`);
    const ids = observed[module].map((row) => row.id).sort();
    if (new Set(ids).size !== ids.length || JSON.stringify(ids) !== JSON.stringify([...expected[module]].sort())) fail(`Migration history differs from the candidate assembly for ${module}.`);
  }
  console.log('All four module histories match the candidate migration manifest.');
} else if (mode === 'dotnet-audit') {
  const report = JSON.parse(fs.readFileSync(input, 'utf8'));
  if (!Array.isArray(report.projects) || report.projects.length === 0) fail('Missing NuGet audit projects.');
  if (Array.isArray(report.errors) && report.errors.length > 0) fail('NuGet audit returned errors.');
  let unsafe = false;
  function inspect(value) {
    if (!value || typeof value !== 'object') return;
    if (['high', 'critical'].includes(String(value.severity).toLowerCase())) unsafe = true;
    for (const child of Object.values(value)) inspect(child);
  }
  inspect(report);
  if (unsafe) fail('NuGet audit contains High/Critical vulnerabilities.');
  console.log('NuGet audit passed: no High/Critical vulnerabilities.');
} else if (mode === 'http' || mode === 'wait-http') {
  const [url, expectedCode, expectedStatus] = [input, Number(value), process.argv[5]];
  let response;
  let data;
  const deadline = Date.now() + (mode === 'wait-http' ? 60000 : 5000);
  do {
    try {
      response = await fetch(url, { signal: AbortSignal.timeout(5000) });
      data = await response.json();
      if (response.status === expectedCode && data.status === expectedStatus) break;
    } catch { /* A starting service may not accept connections yet. */ }
    if (mode === 'http') break;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  } while (Date.now() < deadline);
  if (!response || !data) fail('Health endpoint did not become available.');
  if (response.status !== expectedCode || data.status !== expectedStatus) {
    fail(`HTTP health contract failed: expected ${expectedCode}/${expectedStatus}, got ${response.status}/${data.status}.`);
  }
  console.log(`HTTP health contract passed: ${expectedCode}/${expectedStatus}.`);
} else {
  fail('Usage: assert-quality.mjs coverage DIRECTORY MINIMUM | http URL STATUS BODY_STATUS');
}
