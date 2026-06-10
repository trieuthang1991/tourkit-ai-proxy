// Playwright config — E2E test cho TourKit AI Proxy frontend
// Proxy chạy http://localhost:5080 (Properties/launchSettings.json)
import { defineConfig, devices } from '@playwright/test';

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
    baseURL: 'http://localhost:5080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    locale: 'vi-VN',
    timezoneId: 'Asia/Ho_Chi_Minh',
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],

  // Server tự start nếu chưa chạy
  webServer: {
    command: 'dotnet run --project ../TourkitAiProxy.csproj --no-build',
    url: 'http://localhost:5080/healthz',
    reuseExistingServer: true,
    timeout: 60_000,
  },
});
