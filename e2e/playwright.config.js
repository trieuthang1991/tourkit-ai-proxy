// Playwright config — E2E test cho TourKit AI Proxy frontend
// Default: deployed proxy (https://mobile-api2.tourkit.vn).
// Local dev: chạy `E2E_TARGET=local npx playwright test` để dùng http://localhost:5080
// (sẽ tự `dotnet run --no-build`).
import { defineConfig, devices } from '@playwright/test';

const TARGET = process.env.E2E_TARGET === 'local' ? 'local' : 'deployed';
const BASE_URL = TARGET === 'local'
  ? 'http://localhost:5080'
  : 'https://mobile-api2.tourkit.vn';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,            // tránh race với SSE/session shared
  forbidOnly: false,
  retries: 0,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'report' }]],

  use: {
    baseURL: BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    locale: 'vi-VN',
    timezoneId: 'Asia/Ho_Chi_Minh',
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],

  // Auto-start dotnet CHỈ khi chạy local. Default (deployed) → skip.
  ...(TARGET === 'local' ? {
    webServer: {
      command: 'dotnet run --project ../TourkitAiProxy.csproj --no-build',
      url: 'http://localhost:5080/healthz',
      reuseExistingServer: true,
      timeout: 60_000,
    },
  } : {}),
});
