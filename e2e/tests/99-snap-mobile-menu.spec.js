// Mobile menu: hamburger replace topnav, click → drawer hiện full menu.
import { test, expect } from '@playwright/test';

test('Mobile (380px): hamburger hiện, topnav ẩn', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 800 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);

  const burger = page.locator('.lp-burger');
  await expect(burger).toBeVisible();
  // topnav phải ẩn (display:none)
  const navDisplay = await page.locator('.lp-topnav').evaluate(el => getComputedStyle(el).display);
  expect(navDisplay).toBe('none');
  await page.screenshot({ path: 'snap-mobile-closed.png', clip: { x: 0, y: 0, width: 380, height: 100 } });

  // Click burger → drawer mở
  await burger.click();
  await page.waitForTimeout(400);
  await expect(page.locator('.lp-mobile-panel')).toBeVisible();
  // Tính năng nhanh: 4 mục
  const quick = page.locator('.lp-mobile-features button');
  expect(await quick.count()).toBe(4);
  await page.screenshot({ path: 'snap-mobile-drawer.png', fullPage: false });

  // Click overlay → drawer đóng
  await page.locator('.lp-mobile-overlay').click({ position: { x: 30, y: 200 } });
  await page.waitForTimeout(300);
  await expect(page.locator('.lp-mobile-panel')).toHaveCount(0);
});

test('Desktop (1440px): hamburger ẩn, topnav hiện', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(400);
  const burgerDisplay = await page.locator('.lp-burger').evaluate(el => getComputedStyle(el).display);
  expect(burgerDisplay).toBe('none');
  await expect(page.locator('.lp-topnav')).toBeVisible();
});
