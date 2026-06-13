import { test } from '@playwright/test';

test('snap hero close-up', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  await page.screenshot({ path: 'snap-hero-current.png', clip: { x: 0, y: 0, width: 1440, height: 760 } });
});
