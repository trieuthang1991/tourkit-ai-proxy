// Snap topbar 2 trạng thái: KHÁCH (chưa login) và USER (đã login).
// Cũng test menu mở khi click avatar.
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('topbar — KHÁCH: hiện Đăng nhập + Đăng ký tư vấn', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  await expect(page.locator('.lp-toplogin')).toBeVisible();
  await expect(page.locator('.lp-topcta')).toBeVisible();
  await expect(page.locator('.lp-user-chip')).toHaveCount(0);
  await page.screenshot({ path: 'snap-topbar-guest.png', clip: { x: 0, y: 0, width: 1440, height: 100 } });
});

test('topbar — LOGGED IN: hiện user chip (tên server-side)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  // Chỉ set session — app.jsx sẽ refresh() để lấy user thật từ /api/v1/session.
  // (Pre-set fake user sẽ bị server-refresh ghi đè — đó là behavior đúng.)
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);   // chờ /api/v1/session resolve

  await expect(page.locator('.lp-user-chip')).toBeVisible();
  // Server staging trả "Admin Thoa 123" cho session này
  await expect(page.locator('.lp-user-name')).toContainText('Admin');
  await expect(page.locator('.lp-toplogin')).toHaveCount(0);

  await page.screenshot({ path: 'snap-topbar-logged.png', clip: { x: 0, y: 0, width: 1440, height: 100 } });

  // Click chip → menu mở
  await page.locator('.lp-user-chip').click();
  await expect(page.locator('.lp-user-menu')).toBeVisible();
  await expect(page.locator('.lp-user-menu-item').first()).toContainText('Vào ứng dụng');
  await expect(page.locator('.lp-user-menu-item').last()).toContainText('Đăng xuất');
  await page.screenshot({ path: 'snap-topbar-logged-menu.png', clip: { x: 0, y: 0, width: 1440, height: 220 } });

  // Click "Đăng xuất" → chip biến mất, hiện lại login + CTA
  await page.locator('.lp-user-menu-item').last().click();
  await expect(page.locator('.lp-user-chip')).toHaveCount(0);
  await expect(page.locator('.lp-toplogin')).toBeVisible();
});

test('feature click khi đã login → navigate, KHÔNG popup', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(500);
  // Click 1 feature card → phải navigate (KHÔNG mở popup)
  await page.locator('.lp-feature-card').first().scrollIntoViewIfNeeded();
  await page.locator('.lp-feature-card').first().click();
  await page.waitForTimeout(500);
  await expect(page.locator('.lp-popup')).toHaveCount(0);   // popup KHÔNG hiện
  expect(page.url()).not.toContain('/landing');             // đã đổi route
});
