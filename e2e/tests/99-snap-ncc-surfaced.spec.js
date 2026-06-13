// Verify NCC Import xuất hiện ở: sidebar nav · /home launcher · /landing
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

async function login(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);
}

test('Sidebar — Import NCC (AI) xuất hiện trong nhóm "Tích hợp"', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await login(page);
  // Vào page bất kỳ có sidebar (KHÔNG / vì / là HomePage launcher không có sidebar)
  await page.goto('/customers', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1000);
  const nav = page.locator('.sidebar-item', { hasText: 'Import NCC' });
  await expect(nav).toBeVisible();
  await page.screenshot({ path: 'snap-sidebar-with-ncc.png', clip: { x: 0, y: 0, width: 280, height: 900 } });
});

test('/home — agent card "AI Import NCC" hiện', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await login(page);
  await page.goto('/home', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  const card = page.locator('.hp-card', { hasText: 'AI Import NCC' });
  await expect(card).toBeVisible();
  await page.screenshot({ path: 'snap-home-with-ncc.png', fullPage: false });
});

test('/landing — feature card "Import NCC bằng AI" hiện', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  const card = page.locator('.lp-feature-card', { hasText: 'Import NCC' });
  await expect(card).toBeVisible();
});
