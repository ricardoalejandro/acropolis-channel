import { chromium } from 'playwright';
if (!process.env.BASE_URL?.startsWith('https://qa-') || !process.env.QA_PROJECT?.startsWith('acropolis_test_')) throw new Error('Browser TLS gate requires an isolated QA origin.');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ ignoreHTTPSErrors: false });
  let authorityRejected = false;
  try { await page.goto(process.env.BASE_URL + '/health', { timeout: 10000 }); } catch (error) { authorityRejected = String(error).includes('ERR_CERT_AUTHORITY_INVALID'); }
  if (!authorityRejected) throw new Error('Untrusted QA CA did not fail browser certificate validation.');
  console.log('Chromium rejected the private CA before NSS trust was installed.');
} finally { await browser.close(); }
