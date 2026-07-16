// Nhãn + định dạng số ở panel số liệu. Chốt 2 lỗi từng gặp:
//  • Nút chọn chỉ số của biểu đồ hiện tên field thô ("totalTours", "rank") thay vì tiếng Việt.
//  • Cột đếm bị format thành tiền → "Số tour" hiện "6đ".
//
// Route được MOCK: test này kiểm phần render thuần của frontend, nên KHÔNG cần SQL/CRM thật
// (khác 02b chạy end-to-end). Nhờ vậy nó chạy được cả khi hạ tầng staging sập.
import { test, expect } from '@playwright/test';

// Dòng dữ liệu kiểu /api/ai/top-customers: 1 nhãn (fullName) + 3 cột số.
const ROWS = [
  { rank: 1, fullName: 'Nguyễn Văn A', phone: '0901234567', totalTours: 6, totalRevenue: 120_000_000 },
  { rank: 2, fullName: 'Trần Thị B', phone: '0902345678', totalTours: 3, totalRevenue: 80_000_000 },
  { rank: 3, fullName: 'Lê Văn C', phone: '0903456789', totalTours: 1, totalRevenue: 20_000_000 },
];

const PANEL = {
  kind: 'topcustomers',
  title: 'TOP KHÁCH HÀNG',
  raw: { items: ROWS },
  stats: [{ label: 'Số tour', value: 10, unit: '' }],
};

function sse(...objs) {
  return objs.map(o => `data: ${JSON.stringify(o)}\n\n`).join('');
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('tourkit_tk_session', 'mock-session');
    localStorage.setItem('tourkit_skip_login_gate', '1');
  });
  await page.route('**/api/v1/session*', r =>
    r.fulfill({ json: { sessionId: 'mock-session', tenantId: 'mock.tourkit.vn', fullName: 'Test', companyName: 'Test Co' } }));
  await page.route('**/api/v1/chat/stream', r =>
    r.fulfill({
      status: 200,
      headers: { 'content-type': 'text/event-stream' },
      body: sse(
        { stage: 'analyzing', data: PANEL },
        { delta: 'Đây là top khách hàng.' },
        { done: true, reply: 'Đây là top khách hàng.', data: PANEL },
      ),
    }));
  // domcontentloaded (KHÔNG networkidle): trang còn vài request nền (quota/session) có thể
  // treo khi hạ tầng chậm → networkidle không bao giờ đạt. Các locator bên dưới tự chờ.
  await page.goto('/assistant', { waitUntil: 'domcontentloaded' });
});

async function ask(page) {
  await page.locator('.asst-input').fill('Top khách hàng');
  await page.locator('.asst-input').press('Enter');
  await expect(page.locator('.asst-table')).toBeVisible({ timeout: 20_000 });
}

test('nút chọn chỉ số của biểu đồ hiện nhãn tiếng Việt, không lộ tên field thô', async ({ page }) => {
  await ask(page);
  const chips = page.locator('.asst-mchip');
  await expect(chips.first()).toBeVisible({ timeout: 20_000 });

  const labels = await chips.allTextContents();
  expect(labels.length).toBeGreaterThan(0);

  // Không được lộ BẤT KỲ key thô nào
  for (const raw of ['totalTours', 'totalRevenue', 'rank']) {
    expect(labels.join(' | ')).not.toContain(raw);
  }
  // Và phải là nhãn tiếng Việt đúng nghĩa
  expect(labels.join(' | ')).toContain('Số tour');
});

test('cột đếm "Số tour" hiện số trần, KHÔNG gắn đơn vị tiền "đ"', async ({ page }) => {
  await ask(page);
  const table = page.locator('.asst-table');

  const headers = await table.locator('thead th').allTextContents();
  const idx = headers.findIndex(h => h.trim() === 'Số tour');
  expect(idx, `không thấy cột "Số tour" trong: ${headers.join(' | ')}`).toBeGreaterThanOrEqual(0);

  const cell = (await table.locator('tbody tr').first().locator('td').nth(idx).textContent()).trim();
  expect(cell).toBe('6');          // không phải "6đ"
  expect(cell).not.toContain('đ');
});

test('cột tiền vẫn được format là tiền (không hồi quy chiều ngược lại)', async ({ page }) => {
  await ask(page);
  const table = page.locator('.asst-table');
  const headers = await table.locator('thead th').allTextContents();
  const idx = headers.findIndex(h => /chi tiêu|doanh thu/i.test(h));
  expect(idx, `không thấy cột tiền trong: ${headers.join(' | ')}`).toBeGreaterThanOrEqual(0);

  const cell = (await table.locator('tbody tr').first().locator('td').nth(idx).textContent()).trim();
  expect(cell).toMatch(/[đ₫]|\./);   // có ký hiệu tiền hoặc dấu phân cách nghìn
});
