// Trợ lý số liệu — 23 gợi ý / toggle "Xem tất cả" / icon SVG đầy đủ
import { test, expect } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';
async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
}

test.describe('Assistant page — gợi ý câu hỏi', () => {
  test.beforeEach(async ({ page }) => {
    await setSession(page);
    await page.goto('/assistant', { waitUntil: 'networkidle' });
  });

  test('default state: 4 quick chips + toggle "Xem tất cả gợi ý"', async ({ page }) => {
    // Greeting state — không có message
    await expect(page.locator('.asst-greet-bubble')).toBeVisible();

    // Toggle button có sẵn
    const toggle = page.locator('.asst-suggest-toggle');
    await expect(toggle).toBeVisible();
    await expect(toggle).toContainText('Xem tất cả gợi ý');

    // 4 chip
    const chips = page.locator('.asst-quick-grid .asst-quick-chip');
    await expect(chips).toHaveCount(4);
  });

  test('expand: 7 nhóm × 23 chip, mỗi chip có SVG icon path (không trống)', async ({ page }) => {
    await page.click('.asst-suggest-toggle');

    // 7 nhóm
    const groups = page.locator('.asst-suggest-group');
    await expect(groups).toHaveCount(7);

    // Tổng 23 chip
    const allChips = page.locator('.asst-suggest-group-chips .asst-quick-chip');
    await expect(allChips).toHaveCount(23);

    // Mỗi chip phải có SVG VÀ path/circle bên trong (icon hiện được)
    const chipsHandle = await allChips.all();
    const emptySvgChips = [];
    for (let i = 0; i < chipsHandle.length; i++) {
      const svgHTML = await chipsHandle[i].locator('svg').innerHTML();
      const txt = await chipsHandle[i].innerText();
      // path/circle/rect/line đều OK
      const hasShape = /<(path|circle|rect|line|polygon|polyline|ellipse)/.test(svgHTML);
      if (!hasShape) emptySvgChips.push(txt.slice(0, 60));
    }
    expect(emptySvgChips, `Chip có SVG trống: ${emptySvgChips.join(' | ')}`).toEqual([]);
  });

  test('click chip → message gửi đi (input clear hoặc message hiện)', async ({ page }) => {
    // Click 1 chip cụ thể
    await page.locator('.asst-quick-chip', { hasText: 'Doanh thu tháng này' }).first().click();

    // Có message user hiện (text khớp) HOẶC loading dots
    const userMsg = page.locator('.asst-msg.user').first();
    await expect(userMsg).toBeVisible({ timeout: 5000 });
    await expect(userMsg).toContainText('Doanh thu tháng này');
  });

  test('toggle thu gọn lại sau khi expand', async ({ page }) => {
    await page.click('.asst-suggest-toggle');
    await expect(page.locator('.asst-suggest-all')).toBeVisible();

    await page.locator('.asst-suggest-toggle', { hasText: 'Thu gọn' }).click();
    await expect(page.locator('.asst-suggest-all')).not.toBeVisible();
    await expect(page.locator('.asst-quick-grid')).toBeVisible();
  });
});
