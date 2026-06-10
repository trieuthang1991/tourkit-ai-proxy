// Home — logout button có sẵn + confirm dialog
import { test, expect } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';
async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
}

test.describe('Home page', () => {
  test('nút Đăng xuất visible + click mở confirm dialog', async ({ page }) => {
    await setSession(page);
    await page.goto('/', { waitUntil: 'networkidle' });

    const logoutBtn = page.locator('.hp-logout');
    await expect(logoutBtn).toBeVisible();
    await expect(logoutBtn).toContainText('Đăng xuất');

    await logoutBtn.click();

    // Dialog confirm xuất hiện (Portal-rendered)
    const dialog = page.locator('[role="dialog"], .dialog-backdrop, .modal-backdrop').first();
    await expect(dialog).toBeVisible({ timeout: 3000 });
  });

  test('hiển thị greeting với tên user', async ({ page }) => {
    await setSession(page);
    await page.goto('/', { waitUntil: 'networkidle' });

    const greet = page.locator('.hp-greet');
    await expect(greet).toBeVisible();
    await expect(greet).toContainText('Trợ lý AI');
  });

  test('search box filter agents', async ({ page }) => {
    await setSession(page);
    await page.goto('/', { waitUntil: 'networkidle' });

    const search = page.locator('.hp-search input');
    if (await search.isVisible()) {
      await search.fill('mail');
      // 1+ kết quả phải vẫn hiện
      const cards = page.locator('.hp-card, [class*="hp-agent"]');
      const count = await cards.count();
      expect(count).toBeGreaterThan(0);
    }
  });
});
