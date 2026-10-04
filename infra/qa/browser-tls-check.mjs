import { chromium } from 'playwright';

const mode = process.argv[2] ?? 'untrusted';
if (!process.env.BASE_URL?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) {
  throw new Error('Browser TLS gate requires an isolated QA origin.');
}
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ ignoreHTTPSErrors: false });
  if (mode === 'untrusted') {
    let authorityRejected = false;
    try { await page.goto(process.env.BASE_URL + '/health', { timeout: 10000 }); }
    catch (error) { authorityRejected = String(error).includes('ERR_CERT_AUTHORITY_INVALID'); }
    if (!authorityRejected) throw new Error('Untrusted QA CA did not fail browser certificate validation.');
    console.log('Chromium rejected the private CA before NSS trust was installed.');
  } else if (mode === 'trusted') {
    const response = await page.goto(process.env.BASE_URL + '/health', { timeout: 10000 });
    if (response?.status() !== 200 || (await response.json()).status !== 'ok') {
      throw new Error('Chromium did not validate the actual candidate HTTPS liveness after NSS trust.');
    }
    console.log('Chromium validates candidate HTTPS with NSS trust and certificate errors enabled.');
  } else throw new Error('Unknown browser TLS gate.');
} finally { await browser.close(); }
