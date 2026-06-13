// Snapshot landing page mới + verify popup luồng đăng ký tư vấn khi chưa đăng nhập.
import { test, expect } from '@playwright/test';

test('landing renders + scroll-reveal + popup mở khi click feature mà chưa login', async ({ page }) => {
  test.setTimeout(60_000);

  // KHÔNG set tk_session → user là khách (chưa login)
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });

  // Hero hiển thị
  await expect(page.locator('.lp-hero h1')).toBeVisible({ timeout: 10_000 });
  const heroText = await page.locator('.lp-hero h1').textContent();
  console.log('HERO:', heroText.replace(/\s+/g, ' ').trim());

  // Mascot hero load (robot4)
  await expect(page.locator('.lp-hero-bot')).toBeVisible();

  // Chụp viewport đầu (hero + topbar)
  await page.screenshot({ path: 'snap-landing-hero.png', fullPage: false });

  // Scroll xuống features rồi chụp
  await page.locator('#lp-features').scrollIntoViewIfNeeded();
  await page.waitForTimeout(800);   // scroll-reveal IO settle
  const featureCount = await page.locator('.lp-feature-card').count();
  console.log('FEATURE CARDS:', featureCount);
  expect(featureCount).toBe(6);
  await page.screenshot({ path: 'snap-landing-features.png', fullPage: false });

  // Scroll xuống "Cách bắt đầu"
  await page.locator('#lp-how').scrollIntoViewIfNeeded();
  await page.waitForTimeout(600);
  await page.screenshot({ path: 'snap-landing-how.png', fullPage: false });

  // Scroll xuống CTA band
  await page.locator('.lp-cta-band').scrollIntoViewIfNeeded();
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'snap-landing-cta.png', fullPage: false });

  // Click 1 feature card → vì chưa login → popup mở
  await page.locator('.lp-feature-card').first().scrollIntoViewIfNeeded();
  await page.locator('.lp-feature-card').first().click();
  await expect(page.locator('.lp-popup')).toBeVisible({ timeout: 5000 });
  const popupHead = await page.locator('.lp-popup-head h3').textContent();
  console.log('POPUP HEAD:', popupHead);
  expect(popupHead).toMatch(/Trải nghiệm/i);
  await page.screenshot({ path: 'snap-landing-popup.png', fullPage: false });

  // Điền form + gửi → success state
  await page.locator('.lp-field input').first().fill('Trần Trung');
  await page.locator('.lp-field input').nth(1).fill('0912345678');
  await page.locator('.lp-popup-btn-primary').click();
  await expect(page.locator('.lp-popup-success')).toBeVisible({ timeout: 8000 });
  console.log('SUCCESS state visible OK');
  await page.screenshot({ path: 'snap-landing-popup-success.png', fullPage: false });
});
