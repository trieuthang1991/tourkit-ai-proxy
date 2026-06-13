// App shell ≤900px: sidebar ẨN, BOTTOM DOCK (5 quick + Thêm),
// click "Thêm" → drawer full menu.
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

async function login(page) {
  await page.addInitScript((sid) => { localStorage.setItem('tourkit_tk_session', sid); }, STAGING_SESSION);
}

test('Mobile (380px): sidebar ẨN, dock dưới có 5 + Thêm', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 800 });
  await login(page);
  await page.goto('/customers', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  // Sidebar trái ẨN
  const sidebarDisplay = await page.locator('.sidebar').first().evaluate(el => getComputedStyle(el).display);
  expect(sidebarDisplay).toBe('none');

  // Bottom dock visible, 6 items (5 quick + 1 Thêm)
  await expect(page.locator('.app-bottom-dock')).toBeVisible();
  const items = page.locator('.app-dock-item');
  expect(await items.count()).toBe(6);
  const labels = await items.allTextContents();
  console.log('DOCK:', labels);
  expect(labels.join('|')).toMatch(/Wizard.*Trợ lý.*Khách.*Deal.*Visa.*Thêm/i);

  // Dock ở dưới đáy (top > 700 ở viewport 800)
  const dockBox = await page.locator('.app-bottom-dock').boundingBox();
  expect(dockBox.y).toBeGreaterThan(700);

  await page.screenshot({ path: 'snap-mobile-bottom-dock.png', fullPage: false });

  // Click "Thêm" → drawer mở với full menu
  await page.locator('.app-dock-more').click();
  await page.waitForTimeout(400);
  await expect(page.locator('.app-drawer-panel')).toBeVisible();
  await expect(page.locator('.app-drawer-panel .sidebar-item', { hasText: 'Soạn Tour GIT' })).toBeVisible();
  await expect(page.locator('.app-drawer-panel .sidebar-item', { hasText: 'Import NCC' })).toBeVisible();
  await page.screenshot({ path: 'snap-mobile-drawer.png', fullPage: false });

  // Click overlay → đóng
  await page.locator('.app-drawer-overlay').click({ position: { x: 20, y: 200 } });
  await page.waitForTimeout(300);
  await expect(page.locator('.app-drawer-panel')).toHaveCount(0);
});

test('Desktop (1440px): sidebar hiện, dock ẨN', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await login(page);
  await page.goto('/customers', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  await expect(page.locator('.sidebar').first()).toBeVisible();
  const dockDisplay = await page.locator('.app-bottom-dock').evaluate(el => getComputedStyle(el).display);
  expect(dockDisplay).toBe('none');
  await page.screenshot({ path: 'snap-desktop-intact.png', clip: { x: 0, y: 0, width: 1440, height: 100 } });
});

test('Active state: ở /wizard → mục Wizard nền cam', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 800 });
  await login(page);
  await page.goto('/wizard', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);
  await expect(page.locator('.app-dock-item.active', { hasText: 'Wizard' })).toBeVisible();
});
