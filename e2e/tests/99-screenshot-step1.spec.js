// Snap mobile topbar after search hidden
import { test } from '@playwright/test';
import { PHIEN, THIEU_PHIEN, bomPhien } from '../helpers/phien.js';

test('mobile-topbar-no-search', async ({ page }) => {
  test.skip(!PHIEN, THIEU_PHIEN);
  await bomPhien(page);
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/deals');
  await page.waitForTimeout(2500);
  await page.locator('.topbar').screenshot({ path: 'snap-mobile-topbar-clean.png' });
});
