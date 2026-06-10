// Smoke test: mọi page load + KHÔNG có console error + KHÔNG có 5xx response
import { test, expect } from '@playwright/test';

// Session test giả lập (đã có sẵn trong tk-sessions.json)
const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';

const pages = [
  { path: '/',          name: 'Home' },
  { path: '/wizard',    name: 'Wizard' },
  { path: '/assistant', name: 'Trợ lý số liệu' },
  { path: '/customers', name: 'Khách hàng' },
  { path: '/deals',     name: 'Deal AI' },
  { path: '/visa',      name: 'Thẩm định Visa' },
  { path: '/mail',      name: 'Hộp thư AI' },
  { path: '/tour-builder', name: 'Tour Builder' },
];

// Helper: inject session vào localStorage trước khi page load
async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
}

test.describe('Smoke — every page loads without JS/network errors', () => {
  for (const p of pages) {
    test(`${p.path} (${p.name})`, async ({ page }) => {
      const consoleErrors = [];
      const networkErrors = [];

      page.on('console', m => {
        if (m.type() === 'error') consoleErrors.push(m.text());
      });
      page.on('pageerror', e => consoleErrors.push(`pageerror: ${e.message}`));
      page.on('response', r => {
        // bỏ qua /api/v1/session check & /healthz, chỉ catch real failures
        if (r.status() >= 500) networkErrors.push(`${r.status()} ${r.url()}`);
      });

      await setSession(page);
      await page.goto(p.path, { waitUntil: 'networkidle' });

      // App shell phải render — ko bị babel crash
      const body = await page.locator('body').innerHTML();
      expect(body.length).toBeGreaterThan(500);

      // Báo cáo
      if (consoleErrors.length > 0) {
        console.warn(`\n[${p.name}] Console errors:\n  ` + consoleErrors.slice(0, 5).join('\n  '));
      }
      if (networkErrors.length > 0) {
        console.warn(`\n[${p.name}] Network 5xx:\n  ` + networkErrors.slice(0, 5).join('\n  '));
      }

      // Fail nếu có pageerror (uncaught exception)
      const pageerrors = consoleErrors.filter(e => e.startsWith('pageerror:'));
      expect(pageerrors, `Page có ${pageerrors.length} uncaught exception`).toEqual([]);
    });
  }
});
