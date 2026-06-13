import { test, expect } from '@playwright/test';

test('snap full landing — features 3x3 + testimonials', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  // Đếm features (8 sau khi bỏ "Báo giá đã lưu")
  const featureCount = await page.locator('.lp-feature-card').count();
  expect(featureCount).toBe(8);
  console.log('FEATURES:', featureCount);

  // Đếm testimonials
  const testiCount = await page.locator('.lp-testi-card').count();
  expect(testiCount).toBe(3);

  // Scroll xuống features rồi snap
  await page.locator('#lp-features').scrollIntoViewIfNeeded();
  await page.waitForTimeout(800);
  await page.screenshot({ path: 'snap-landing-9features.png', fullPage: false });

  // Scroll xuống testimonials
  await page.locator('.lp-testimonials').scrollIntoViewIfNeeded();
  await page.waitForTimeout(600);
  await page.screenshot({ path: 'snap-landing-testimonials.png', fullPage: false });

  // Full page screenshot to xem rhythm
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'snap-landing-full.png', fullPage: true });
});
