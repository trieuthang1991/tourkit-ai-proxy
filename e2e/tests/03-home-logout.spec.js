// Home — logout button có sẵn + confirm dialog
import { test, expect } from '@playwright/test';

import { PHIEN, THIEU_PHIEN, bomPhien } from '../helpers/phien.js';

// ⚠️ CẢ FILE NÀY ĐANG KIỂM MỘT TRANG KHÔNG CÒN ĐƯỢC ĐỊNH TUYẾN. wwwroot/index.html dòng 183 đã
// chú thích lại thẻ nạp pages/home.jsx kèm ghi chú "launcher /home tạm gỡ khỏi routing — bật lại
// khi cần", nên `/` và `/home` đều không vẽ khối hp-* nữa. Lớp .hp-logout cũng đã đổi tên thành
// .hp-pill--logout từ lúc nào không rõ.
//
// Đánh dấu fixme thay vì xoá: xoá là mất luôn bộ kiểm cho trang launcher nếu sau này bật lại, mà
// để ĐỎ thường trực thì người ta quen mắt rồi bỏ qua cả những cái đỏ thật. fixme báo "biết là
// hỏng, cần xử" và Playwright đếm riêng.
//
// Hai đường ra, chủ dự án chọn: bật lại /home rồi sửa hai bộ chọn, hoặc xoá hẳn file này.
// (Ghi nhận 08/09/2026 — KHÔNG liên quan tới việc gỡ phiên ghim cứng cùng ngày; đã đo: phiên bơm
//  vào chạy đúng, localStorage có tourkit_tk_user với tên và tenant thật.)
test.describe('Home page', () => {
  test.skip(!PHIEN, THIEU_PHIEN);
  test.fixme(true, 'Trang launcher /home đang gỡ khỏi routing — xem chú thích đầu file.');

  test('nút Đăng xuất visible + click mở confirm dialog', async ({ page }) => {
    await bomPhien(page);
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
    await bomPhien(page);
    await page.goto('/', { waitUntil: 'networkidle' });

    const greet = page.locator('.hp-greet');
    await expect(greet).toBeVisible();
    await expect(greet).toContainText('Trợ lý AI');
  });

  test('search box filter agents', async ({ page }) => {
    await bomPhien(page);
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
