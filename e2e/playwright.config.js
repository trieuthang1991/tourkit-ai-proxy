// Playwright config — E2E test cho TourKit AI Proxy frontend
// Default: deployed proxy (https://mobile-api2.tourkit.vn).
// Local dev: chạy `E2E_TARGET=local npx playwright test` để dùng http://localhost:5080
// (sẽ tự `dotnet run --no-build`).
import { defineConfig, devices } from '@playwright/test';

const TARGET = process.env.E2E_TARGET === 'local' ? 'local' : 'deployed';
// E2E_BASE_URL đè lên cả hai — dùng khi chạy một bản dựng riêng ở cổng khác (5080 thường là bản
// của người đang ngồi máy, chiếm mất là họ đang thử dở dang thì đứt).
const BASE_URL = process.env.E2E_BASE_URL
  || (TARGET === 'local' ? 'http://localhost:5080' : 'https://mobile-api2.tourkit.vn');

// ⚠️ MẶC ĐỊNH LÀ BẢN CHẠY THẬT. Bài nào chỉ ĐỌC thì không sao; bài GHI phải tự chặn mình — xem
// e2e/helpers/chat-phan-cong.js. Đừng thêm bài ghi mà không đi qua cửa đó.

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

  // Auto-start dotnet CHỈ khi chạy local VÀ không tự khai máy chủ. Default (deployed) → skip.
  ...(TARGET === 'local' && !process.env.E2E_BASE_URL ? {
    webServer: {
      command: 'dotnet run --project ../TourkitAiProxy.csproj --no-build --no-launch-profile',
      url: 'http://localhost:5080/healthz',
      reuseExistingServer: true,
      timeout: 60_000,
      // ⚠️ TẮT WORKER LÀ BẮT BUỘC, không phải cho nhẹ máy. ChatOutboxWorker GỬI TIN CHO KHÁCH và
      // mặc định BẬT: dựng app để chạy test mà quên tắt là hàng đợi tin thật được gửi đi từ máy
      // của người đang chạy test. Đã suýt xảy ra ngày 08/09/2026.
      //
      // --no-launch-profile cũng bắt buộc: launchSettings.json ĐÈ lên --urls ở môi trường
      // Development, nên thiếu nó là app chiếm cổng khác cổng mình tưởng.
      env: {
        Workflows__RunChatWorkers: 'false',
        Workflows__RunChatMediaWorker: 'false',
        Workflows__RunScheduler: 'false',
      },
    },
  } : {}),
});
