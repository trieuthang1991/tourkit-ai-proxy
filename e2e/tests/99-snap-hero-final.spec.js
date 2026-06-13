import { test } from '@playwright/test';

test('snap hero full quality', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  // Đợi entrance animation xong + underline draw (1.1s + 1.2s)
  await page.waitForTimeout(2500);
  await page.screenshot({ path: 'snap-hero-final.png', fullPage: false });
});
