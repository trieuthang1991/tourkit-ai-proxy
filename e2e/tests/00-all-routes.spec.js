// 00-all-routes.spec.js — quét MỌI trang: route có mở được không, giao diện có vẽ ra không,
// console có lỗi không.
//
// VÌ SAO CẦN: hai thay đổi gần đây đều có thể làm hỏng trang mà không lộ ra ngay.
//   1. Đường dẫn lạ nay trả 404 THẬT (SeoSetup.Routes là danh sách đường hợp lệ) → khai thiếu một
//      trang là mở link trực tiếp vào trang đó ra 404, mà bấm trong app vẫn chạy vì router client
//      không hỏi server. Kiểu hỏng chỉ người dùng gặp, dev không bao giờ thấy.
//   2. Nội dung SEO dựng sẵn nhét vào #root ở "/" → nếu React không xoá sạch được thì trang chủ
//      hiện chữ thô không có định dạng.
//
// Chạy: cd e2e && E2E_TARGET=local npx playwright test tests/00-all-routes.spec.js
import { test, expect } from '@playwright/test';

// Mọi route client. `guest: true` = xem được khi CHƯA đăng nhập; còn lại sẽ bị đẩy về trang giới
// thiệu / màn đăng nhập — vẫn phải kiểm là KHÔNG lỗi, chỉ không đòi thấy nội dung riêng của trang.
const ROUTES = [
  { path: '/', guest: true, name: 'Trang giới thiệu' },
  { path: '/landing', guest: true, name: 'Trang giới thiệu (đường cũ)' },
  { path: '/travai', name: 'Trợ lý giọng nói' },
  { path: '/jarvis', name: 'Trợ lý giọng nói (tên cũ)' },
  { path: '/wizard', name: 'AI tính giá tour' },
  { path: '/tour-builder', name: 'Bóc tour bằng AI' },
  { path: '/quotes', name: 'Báo giá đã lưu' },
  { path: '/customers', name: 'Chấm điểm khách hàng' },
  { path: '/deals', name: 'Phân tích cơ hội' },
  { path: '/assistant', name: 'Trợ lý số liệu' },
  { path: '/mail', name: 'Hộp thư AI' },
  { path: '/visa', name: 'Visa AI' },
  { path: '/visa/history', name: 'Lịch sử Visa' },
  { path: '/visa-config', name: 'Cấu hình Visa' },
  { path: '/ncc-import', name: 'Import NCC' },
  { path: '/ncc-list', name: 'Nhà cung cấp' },
  { path: '/widget-admin', name: 'Widget chat khách' },
  { path: '/workflows', name: 'Tự động hoá' },
  { path: '/insights', name: 'Bảng tin' },
  { path: '/digest', name: 'Bản tin của tôi' },
  { path: '/ai-usage', name: 'Chi phí AI' },
  { path: '/help', name: 'Hướng dẫn' },
  { path: '/flow-preview', name: 'Sơ đồ luồng' },
];

// Lỗi console KHÔNG phải do trang: mạng chậm, tiện ích của trình duyệt, API cần đăng nhập trả 401.
// Bỏ qua để bài kiểm không đỏ vì chuyện ngoài code — nhưng KHÔNG bỏ qua lỗi JS thật.
const IGNORE = [
  /favicon/i,
  /401|403|Unauthorized/i,          // gọi API khi chưa đăng nhập — đúng như thiết kế
  /net::ERR_/i,                     // hạ tầng, không phải lỗi trang
  /Failed to load resource/i,
  /ResizeObserver loop/i,           // cảnh báo vô hại của Chrome
  /tinymce/i,                       // 5MB nạp lười, chỉ khi mở soạn thư
];
const isRealError = (t) => !IGNORE.some(re => re.test(t));

async function visit(page, path) {
  const errors = [];
  page.on('console', m => { if (m.type() === 'error' && isRealError(m.text())) errors.push(m.text()); });
  page.on('pageerror', e => { if (isRealError(String(e))) errors.push('pageerror: ' + String(e)); });

  const resp = await page.goto(path, { waitUntil: 'domcontentloaded' });
  // Chờ React dựng xong: splash tự xoá mình sau khi App mount.
  await page.waitForFunction(() => !document.getElementById('boot-splash'), { timeout: 25_000 })
    .catch(() => {});
  return { status: resp?.status() ?? 0, errors };
}

test.describe('Quét mọi trang', () => {
  for (const r of ROUTES) {
    test(`${r.path} — ${r.name}`, async ({ page }) => {
      const { status, errors } = await visit(page, r.path);

      // 1. Route phải mở được. 404 ở đây nghĩa là quên khai trong SeoSetup.Routes.
      expect(status, `${r.path} trả HTTP ${status} — kiểm SeoSetup.Routes`).toBe(200);

      // 2. Giao diện phải vẽ ra: #root có node con thật.
      const kids = await page.locator('#root > *').count();
      expect(kids, `${r.path}: #root rỗng — React không dựng được`).toBeGreaterThan(0);

      // 3. Không còn khối chữ SEO dựng sẵn — React phải đã thay thế nó.
      const leftover = await page.locator('#seo-prerender').count();
      expect(leftover, `${r.path}: khối chữ SEO còn nguyên — React chưa thay #root`).toBe(0);

      // 4. Không có lỗi JS thật.
      expect(errors, `${r.path} có lỗi console:\n` + errors.join('\n')).toEqual([]);

      // 5. Trang public phải hiện đúng nội dung của mình (đăng nhập rồi thì bị đẩy đi, không đòi).
      if (r.guest) {
        await expect(page.locator('h1').first()).toContainText(/gánh việc tour/i, { timeout: 15_000 });
      }
    });
  }

  test('/khong-ton-tai-abcxyz — đường lạ phải 404 nhưng vẫn vẽ được giao diện', async ({ page }) => {
    const { status, errors } = await visit(page, '/khong-ton-tai-abcxyz');
    expect(status).toBe(404);
    // Vẫn trả HTML để người dùng thấy giao diện chứ không phải trang lỗi trắng của trình duyệt.
    expect(await page.locator('#root > *').count()).toBeGreaterThan(0);
    expect(errors).toEqual([]);
  });
});
