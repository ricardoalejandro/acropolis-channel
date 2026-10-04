import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 20_000,
  expect: { timeout: 8_000 },
  workers: 1,
  fullyParallel: false,
  retries: 0,
  outputDir: process.env['PLAYWRIGHT_OUTPUT_DIR'] ?? '../.local/qa/playwright',
  reporter: [
    ['list'],
    [
      'html',
      {
        outputFolder: process.env['PLAYWRIGHT_HTML_REPORT'] ?? '../.local/qa/playwright-report',
        open: 'never',
      },
    ],
  ],
  use: {
    baseURL: process.env['BASE_URL'] ?? 'http://web:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ignoreHTTPSErrors: false,
  },
  projects: [
    { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile-chromium', use: { ...devices['Pixel 7'] } },
  ],
});
