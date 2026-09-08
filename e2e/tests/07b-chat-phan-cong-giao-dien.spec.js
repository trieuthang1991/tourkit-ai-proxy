// 07b-chat-phan-cong-giao-dien.spec.js — bấm NÚT THẬT trên giao diện thật.
//
// ⚠️ VÌ SAO PHẢI CÓ BÀI TRÌNH DUYỆT RIÊNG, dù bài 07 đã gọi API. Lỗi ngày 08/09/2026 nằm ĐÚNG
// giữa hai bên: máy chủ có route, giao diện có nút, mà nút gọi sai cách nên request không bao giờ
// tới route. Bài chỉ gọi API sẽ XANH (route chạy tốt); bài chỉ đọc mã nguồn cũng XANH (cả hai
// phía đều "đúng" khi đọc riêng). Chỉ có cú bấm thật mới nối được hai nửa lại.
//
// Triệu chứng người dùng gặp: bấm "Nhận chăm sóc" — không có gì xảy ra. Không quay vòng, không
// báo lỗi, không đổi nhãn. Suốt cả một nhánh.
//
// Hướng dẫn chạy + các cửa an toàn: xem đầu file e2e/helpers/chat-phan-cong.js.
import { test, expect, request as moiPhienApi } from '@playwright/test';
import {
  PHIEN_QUAN_TRI, GOC,
  batBuocDuLieuVao, nhu, doc, layTenant,
  chupTrangThai, traTrangThai, hoiThoaiDeThu,
} from '../helpers/chat-phan-cong.js';

let api;
let maHoiThoai;
let anhGoc;

test.beforeAll(async ({ baseURL }) => {
  batBuocDuLieuVao(baseURL);
  api = await moiPhienApi.newContext({ baseURL });
  await layTenant(api, PHIEN_QUAN_TRI);
  maHoiThoai = await hoiThoaiDeThu(api, PHIEN_QUAN_TRI);
  anhGoc = await chupTrangThai(api, PHIEN_QUAN_TRI, maHoiThoai);
});

test.afterAll(async () => {
  if (api && anhGoc) await traTrangThai(api, PHIEN_QUAN_TRI, maHoiThoai, anhGoc);
  if (api) await api.dispose();
});

/**
 * Bơm phiên vào localStorage TRƯỚC khi kịch bản của trang chạy, và ghi lại mọi phản hồi.
 *
 * Danh sách phản hồi là thứ bắt được lỗi B2/A2: đường /api trả HTML nghĩa là request rơi xuống
 * trang SPA thay vì tới handler — hỏng câm, không ngoại lệ nào ném ra để bài kiểm thấy.
 */
async function moTrang(page, path) {
  await page.addInitScript(sid => {
    try { localStorage.setItem('tourkit_tk_session', sid); } catch {}
  }, PHIEN_QUAN_TRI);

  const phanHoi = [];
  page.on('response', r => phanHoi.push({
    url: r.url(), ma: r.status(), kieu: r.headers()['content-type'] || '',
  }));
  const loiConsole = [];
  page.on('pageerror', e => loiConsole.push(String(e)));

  await page.goto(path);
  return { phanHoi, loiConsole };
}

const cuaApi = (ds) => ds.filter(r => r.url.includes('/api/'));

test('A1 — bấm "Nhận chăm sóc" phải THẬT SỰ nhận việc', async ({ page }) => {
  // Dựng sẵn trạng thái: chưa ai phụ trách, không kẹp quyền (để nút chắc chắn hiện).
  await api.put(`${GOC}/assign-settings`, {
    headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }),
    data: { mode: 1, scopeOwnOnly: false, autoAssignOnReply: false, memberIds: [] },
  });
  await api.delete(`${GOC}/conversations/${maHoiThoai}/assign`, { headers: nhu(PHIEN_QUAN_TRI) });

  await moTrang(page, '/chat-inbox');

  // Chọn hội thoại đầu danh sách. Neo vào .ci-muc (role=button) — lớp cấu trúc của danh sách,
  // không phải lớp trang trí, nên sửa CSS không làm bài đỏ oan.
  const muc = page.locator('.ci-muc').first();
  await expect(muc, 'không thấy hội thoại nào trong danh sách').toBeVisible({ timeout: 20_000 });
  await muc.click();

  const nut = page.getByRole('button', { name: 'Nhận chăm sóc', exact: true });
  await expect(nut, 'không thấy nút "Nhận chăm sóc" ở đầu khung chat').toBeVisible({ timeout: 15_000 });

  // Chờ ĐÚNG lượt gọi nhận việc, gắn với chính cú bấm. Không chỉ chờ nút đổi nhãn: nhãn đổi được
  // vì lý do khác (tải lại danh sách), còn ta cần bằng chứng request ĐÃ TỚI HANDLER.
  const [phanHoi] = await Promise.all([
    page.waitForResponse(r => r.url().includes('/assign/me') && r.request().method() === 'POST',
      { timeout: 15_000 }),
    nut.click(),
  ]);

  const kieu = phanHoi.headers()['content-type'] || '';
  expect(kieu, `lượt nhận việc trả "${kieu}" — HTML nghĩa là request rơi xuống trang SPA, ` +
    'không tới được handler (đúng lỗi 08/09/2026)').not.toContain('html');
  expect(phanHoi.status()).toBe(200);

  // Và giao diện phải PHẢN ÁNH kết quả. Máy chủ nhận việc xong mà nút không đổi thì với người
  // dùng vẫn là "bấm không ăn thua".
  await expect(page.getByRole('button', { name: 'Đã nhận chăm sóc', exact: true }))
    .toBeVisible({ timeout: 15_000 });

  const sau = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}`, { headers: nhu(PHIEN_QUAN_TRI) }));
  expect(sau.json.conversation.assignedUserId, 'máy chủ phải thật sự ghi người phụ trách').toBeTruthy();
});

test('A2 — mở hộp thư chat: không lượt gọi API nào rơi xuống trang SPA', async ({ page }) => {
  // Luật TỔNG QUÁT rút ra từ lỗi trên, áp cho MỌI lượt gọi mà trang tự phát ra. Một nút mới thêm
  // sau này mà gọi sai cách sẽ bị bắt ở đây, không cần ai nhớ viết bài riêng cho nó.
  const { phanHoi, loiConsole } = await moTrang(page, '/chat-inbox');
  await expect(page.locator('.ci-muc').first()).toBeVisible({ timeout: 20_000 });
  await page.locator('.ci-muc').first().click();
  await page.waitForTimeout(2000);   // để các lượt gọi chi tiết/nhãn/ghi chú kịp đi

  const html = cuaApi(phanHoi).filter(r => r.kieu.includes('html'));
  expect(html, 'đường /api trả HTML = request không tới handler:\n' +
    html.map(r => `  ${r.ma} ${r.url}`).join('\n')).toHaveLength(0);

  const nam_tram = cuaApi(phanHoi).filter(r => r.ma >= 500);
  expect(nam_tram, 'có lượt gọi 5xx:\n' + nam_tram.map(r => `  ${r.ma} ${r.url}`).join('\n'))
    .toHaveLength(0);

  expect(loiConsole, 'trang ném lỗi JavaScript:\n' + loiConsole.join('\n')).toHaveLength(0);
});

test('A3 — sau khi LƯU cấu hình phân công, hộp thư vẫn mở được', async ({ page }) => {
  // Lỗi nặng nhất của nhánh: bấm Lưu MỘT LẦN là mọi request chat trả 500 và hộp thư trắng. Bài 07
  // đo bằng API; bài này đo bằng thứ người dùng thấy — danh sách hội thoại có hiện ra không.
  await api.put(`${GOC}/assign-settings`, {
    headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }),
    data: { mode: 2, scopeOwnOnly: true, autoAssignOnReply: true, memberIds: [] },
  });

  const { phanHoi } = await moTrang(page, '/chat-inbox');
  await expect(page.locator('.ci-muc').first(),
    'hộp thư không hiện hội thoại nào sau khi lưu cấu hình — đúng triệu chứng lỗi cũ')
    .toBeVisible({ timeout: 20_000 });

  const nam_tram = cuaApi(phanHoi).filter(r => r.ma >= 500);
  expect(nam_tram, 'lưu cấu hình xong là chat 5xx:\n' +
    nam_tram.map(r => `  ${r.ma} ${r.url}`).join('\n')).toHaveLength(0);
});
