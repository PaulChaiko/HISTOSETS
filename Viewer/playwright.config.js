import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests', workers: 1, timeout: 30_000,
  use: { baseURL: 'http://127.0.0.1:4173', viewport: { width: 1200, height: 780 }, screenshot: 'only-on-failure',
    launchOptions: process.env.HISTOSETS_CHROMIUM ? { executablePath: process.env.HISTOSETS_CHROMIUM } : {} },
  reporter: [['list'], ['html', { open: 'never' }]],
  webServer: { command: 'node tests/server.mjs', url: 'http://127.0.0.1:4173', reuseExistingServer: !process.env.CI }
});
