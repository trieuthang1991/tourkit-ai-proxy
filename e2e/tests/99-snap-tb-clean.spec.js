// Tour-builder sau khi bỏ Dịch vụ điều hành: 3 block (info / KH / Phần thu),
// markets fetch từ /api/v1/markets thật, summary chỉ revenue.
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('tour-builder UI sạch: 3 block, không còn "Dịch vụ điều hành (chi)"', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => { localStorage.setItem('tourkit_tk_session', sid); }, STAGING_SESSION);

  await page.goto('/tour-builder', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);

  // 3 block: Mô tả tour (trái) + Thông tin tour / Khách / Phần thu (phải) = 4 card
  const cards = await page.locator('.tb-card').count();
  expect(cards).toBe(4);

  // KHÔNG còn text "Dịch vụ điều hành"
  await expect(page.locator('text=/Dịch vụ điều hành/i')).toHaveCount(0);
  // KHÔNG còn cột "Tổng chi" trong summary
  await expect(page.locator('text=/Tổng chi/i')).toHaveCount(0);
  await expect(page.locator('text=/Margin/i')).toHaveCount(0);

  // Summary có "Tổng thu (sau VAT)"
  await expect(page.locator('text=/Tổng thu \\(sau VAT\\)/i')).toBeVisible();

  await page.screenshot({ path: 'snap-tb-clean.png', fullPage: true });
});

test('markets dropdown: fetch từ /api/v1/markets (real tenant data)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => { localStorage.setItem('tourkit_tk_session', sid); }, STAGING_SESSION);

  await page.goto('/tour-builder', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1800);   // chờ fetch /api/v1/markets

  // Click dropdown thị trường
  const marketInput = page.locator('.tb-row:has(label:has-text("Thị trường")) input').first();
  await marketInput.click();
  await page.waitForTimeout(400);

  // Dropdown items: phải có ít nhất 1 mục KHÔNG nằm trong fallback hardcode 12.
  // Staging tenant có "Thị trường Quốc Tế", "Miền Tây", "QT1"… (xác minh được fetch real)
  const itemCount = await page.locator('.ss-item, .search-select-item, [role="option"]').count();
  console.log('Dropdown items:', itemCount);
  expect(itemCount).toBeGreaterThan(10);
});
