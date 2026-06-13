// Chat bot end-to-end: gửi câu hỏi thật → AI plan + CRM fetch + phân tích stream
// → kiểm panel số liệu (stats, title VN) và bubble assistant.
import { test, expect } from '@playwright/test';

// Session staging tourkit còn sống (đã verify qua /api/v1/session trước khi chạy test này)
const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, STAGING_SESSION);
}

test.describe('Assistant — chat bot end-to-end', () => {
  test.beforeEach(async ({ page }) => {
    await setSession(page);
    await page.goto('/assistant', { waitUntil: 'networkidle' });
  });

  test('"Doanh thu tháng này" → bot hiểu, panel có thẻ Doanh thu (đ), title TV, có bubble assistant', async ({ page }) => {
    test.setTimeout(90_000);  // AI cold ~25s, warm cache <1s

    await page.locator('.asst-input').fill('Doanh thu tháng này');
    await page.locator('.asst-input').press('Enter');

    // Bubble user
    await expect(page.locator('.asst-msg.user').last()).toContainText('Doanh thu tháng này');

    // Panel có stats (tool nào cũng được — financial_summary 12 KPI / cashflow 4 thẻ đều OK)
    const stats = page.locator('.asst-stat');
    await expect(stats.first()).toBeVisible({ timeout: 60_000 });
    expect(await stats.count()).toBeGreaterThanOrEqual(2);

    // Có nhãn "Doanh thu" → bot hiểu đúng câu hỏi
    await expect(page.locator('.asst-stat-label', { hasText: /Doanh thu/i }).first()).toBeVisible();

    // Title panel bằng tiếng Việt (selector hiện tại: .asst-slate-title)
    const panelTitle = page.locator('.asst-slate-title').first();
    await expect(panelTitle).toBeVisible();
    const title = await panelTitle.textContent();
    expect(title.length).toBeGreaterThan(0);
    // Phải có chữ liên quan tài chính / dòng tiền / tổng quan
    expect(title).toMatch(/T[ÀA]I|D[ÒO]NG|DOANH|tổng|quan|tài|dòng|doanh/i);

    // Bubble assistant phản hồi (phân tích đã stream)
    const replyBubble = page.locator('.asst-msg.assistant .asst-bubble').last();
    await expect(replyBubble).toBeVisible({ timeout: 60_000 });
    const replyText = await replyBubble.textContent();
    expect(replyText.length).toBeGreaterThan(20);  // không phải rỗng / 1 chữ
  });

  test('list/tours: gửi "Danh sách tour" → bảng dữ liệu hiện, cột curate (không có Khách/SĐT/Seller)', async ({ page }) => {
    test.setTimeout(90_000);

    await page.locator('.asst-input').fill('Danh sách tour sắp khởi hành');
    await page.locator('.asst-input').press('Enter');

    await expect(page.locator('.asst-msg.user').last()).toContainText('Danh sách tour');

    // Đợi bảng dữ liệu
    const table = page.locator('.asst-table');
    await expect(table).toBeVisible({ timeout: 60_000 });

    // Header KHÔNG được chứa "Khách hàng", "SĐT", "Seller" (rules curate _KIND_COLS.tours)
    const headers = await table.locator('thead th').allTextContents();
    const txt = headers.join(' | ').toLowerCase();
    expect(txt).not.toContain('khách hàng');
    expect(txt).not.toContain('sđt');
    expect(txt).not.toContain('seller');

    // Phải có ít nhất 1 dòng dữ liệu
    const rows = table.locator('tbody tr');
    expect(await rows.count()).toBeGreaterThan(0);
  });
});
