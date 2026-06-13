// /home phải là alias cho / (HomePage launcher) — không 404, không redirect landing.
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('/home (logged in) → HomePage render (không 404, không landing)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/home', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  // Không phải landing
  await expect(page.locator('.lp-hero')).toHaveCount(0);
  // Không phải "Trang không tồn tại"
  await expect(page.locator('text="Trang không tồn tại"')).toHaveCount(0);
  // URL vẫn là /home
  expect(page.url()).toContain('/home');
  // HomePage launcher có element gì đó để đảm bảo render (vd .hp-* class hoặc title)
  // Lùi về kiểm tra title/text quen thuộc:
  const titleVisible = await page.locator('text=TRAV-AI').first().isVisible().catch(() => false);
  expect(titleVisible).toBeTruthy();
});

test('Landing → Vào ứng dụng → /home render HomePage', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);
  // Click chip user → menu mở → "Vào ứng dụng"
  await page.locator('.lp-user-chip').click();
  await page.locator('.lp-user-menu-item', { hasText: 'Vào ứng dụng' }).click();
  await page.waitForTimeout(800);

  expect(page.url()).toContain('/home');
  await expect(page.locator('.lp-hero')).toHaveCount(0);   // không còn landing
});
