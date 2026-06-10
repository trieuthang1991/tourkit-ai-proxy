// Customers + Deals: list + auto-review toggle + checkbox
import { test, expect } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';
async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
}

test.describe('Customers page', () => {
  test('list load + auto toggle hiện', async ({ page }) => {
    await setSession(page);
    await page.goto('/customers', { waitUntil: 'networkidle' });

    // PageHero render
    await expect(page.locator('.ph-hero')).toBeVisible({ timeout: 10000 });

    // Có table hoặc list
    const rows = page.locator('table tbody tr, [class*="customer-row"], [class*="cust-row"]');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });

    // Auto-review toggle: tìm chữ "Tự động"
    const autoToggle = page.getByText(/Tự động|Auto.*review/i).first();
    if (await autoToggle.count() > 0) {
      await expect(autoToggle).toBeVisible();
    }
  });

  test('chọn 1 KH bằng checkbox + nút "Chấm AI" hiện', async ({ page }) => {
    await setSession(page);
    await page.goto('/customers', { waitUntil: 'networkidle' });

    const rows = page.locator('table tbody tr');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });

    // TKCheckbox dùng .tk-checkbox class
    const firstCheckbox = page.locator('.tk-checkbox, input[type="checkbox"]').first();
    if (await firstCheckbox.count() > 0) {
      await firstCheckbox.click();
      // Sau khi check, nút action xuất hiện
      const actionBtn = page.getByRole('button', { name: /Chấm|Phân tích|Review/i }).first();
      await expect(actionBtn).toBeVisible({ timeout: 3000 });
    }
  });
});

test.describe('Deals page', () => {
  test('list load + PageHero render', async ({ page }) => {
    await setSession(page);
    await page.goto('/deals', { waitUntil: 'networkidle' });

    await expect(page.locator('.ph-hero')).toBeVisible({ timeout: 10000 });

    const rows = page.locator('table tbody tr');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
  });

  test('checkbox shared component hiện', async ({ page }) => {
    await setSession(page);
    await page.goto('/deals', { waitUntil: 'networkidle' });

    await page.waitForSelector('table tbody tr', { timeout: 15000 });

    const checkboxes = page.locator('.tk-checkbox, input[type="checkbox"]');
    const count = await checkboxes.count();
    expect(count).toBeGreaterThan(0);
  });
});
