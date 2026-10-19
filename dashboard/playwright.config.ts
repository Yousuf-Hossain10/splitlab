import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const apiUrl = 'http://localhost:5080';
const appUrl = 'http://localhost:4200';
// Absolute, because `dotnet run --project` starts the host in its own project directory, not this one.
const databaseFile = path.resolve(__dirname, 'e2e', '.data', 'splitlab.db');

/**
 * End-to-end tests drive the real dashboard in a real browser against the real API: its endpoints, authentication,
 * event pipeline, rollups and statistics. Only the database differs from production (a SQLite file instead of
 * PostgreSQL), so these run anywhere without a database server.
 *
 * Locally, set PW_CHANNEL=msedge (or chrome) to use an installed browser instead of downloading one.
 */
export default defineConfig({
  testDir: './e2e',
  // One worker: the tests share one API process and database, and each makes its own data, so order does not matter.
  workers: 1,
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: appUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], channel: process.env['PW_CHANNEL'] || undefined },
    },
  ],
  webServer: [
    {
      command: `dotnet run --project ../tests/Splitlab.E2E.Host -- --db "${databaseFile}" --urls ${apiUrl}`,
      url: apiUrl + '/swagger/v1/swagger.json',
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
    {
      // The dev server proxies /v1 and /dev to the API (proxy.conf.json), exactly as in everyday development.
      command: 'npm start -- --port 4200',
      url: appUrl,
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
  ],
});
