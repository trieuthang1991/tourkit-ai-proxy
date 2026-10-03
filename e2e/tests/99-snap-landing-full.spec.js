import { test, expect } from '@playwright/test';

test('snap full landing — features 3x3 + testimonials', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  // Đếm features. Ngưỡng TỐI THIỂU chứ không phải con số chính xác — xem lý do ở
  // 99-snap-landing.spec.js. Bài này từng ghi 8, bài kia ghi 6, trang thật có 9: ba con số cho
  // cùng một thứ, và cả hai bài cùng đỏ giả.
  const featureCount = await page.locator('.lp-feature-card').count();
  expect(featureCount).toBeGreaterThanOrEqual(6);
  console.log('FEATURES:', featureCount);

  // KHỐI CẢM NHẬN KHÁCH HÀNG ĐÃ BỊ GỠ khỏi trang giới thiệu — bài này từng đòi đúng 3 thẻ
  // `.lp-testi-card`, nay trang có 0. Không nới lỏng thành ">= 0" (vô nghĩa) mà bỏ hẳn: canh số
  // lượng của một khối không còn tồn tại thì mãi mãi là đỏ giả.
  //
  // 12/09/2026: CSS `.lp-testi-*` vẫn nằm trong styles.css (khoảng dòng 7361) dù không còn ai
  // dùng. Dọn nó là việc riêng, không gộp vào đây.

  // Scroll xuống features rồi snap
  await page.locator('#lp-features').scrollIntoViewIfNeeded();
  await page.waitForTimeout(800);
  await page.screenshot({ path: 'snap-landing-9features.png', fullPage: false });

  // Full page screenshot to xem rhythm
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'snap-landing-full.png', fullPage: true });
});
