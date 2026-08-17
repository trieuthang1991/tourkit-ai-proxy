// 00b-all-routes-authed.spec.js — quét mọi trang KHI ĐÃ ĐĂNG NHẬP.
//
// VÌ SAO CẦN BÀI RIÊNG: bài 00-all-routes chạy ở trạng thái khách, mà app đẩy MỌI trang nội bộ về
// trang giới thiệu khi chưa có phiên. Nên nó xanh không có nghĩa là giao diện từng trang chạy được —
// nó chỉ chứng minh trang giới thiệu vẽ được 21 lần. Đúng kiểu "xanh giả" đã cắn repo này trước đây.
//
// Bài này bơm sẵn phiên vào localStorage TRƯỚC khi trang chạy, rồi mới kiểm.
//
// Chạy:
//   cd e2e
//   $env:E2E_SESSION='<sessionId>'; $env:E2E_TARGET='local'
//   npx playwright test tests/00b-all-routes-authed.spec.js
//
// Lấy sessionId: đăng nhập app rồi mở DevTools Console:
//   localStorage.getItem('tourkit_tk_session')
// KHÔNG hardcode vào file: mỗi máy mỗi phiên, và phiên là thứ truy cập được dữ liệu công ty thật.
import { test, expect } from '@playwright/test';

const SESSION = process.env.E2E_SESSION || '';

// Dấu hiệu RIÊNG của từng trang — thứ chỉ trang đó có. Chọn tiêu đề/nhãn đang hiện trên giao diện
// chứ không chọn class CSS: class đổi lúc chỉnh style là bài kiểm đỏ oan, còn chữ đổi thì đúng là
// giao diện đã đổi và đáng biết.
const PAGES = [
  { path: '/travai',       expect: /Trợ lý|TRAV|nói|nghe/i },
  { path: '/wizard',       expect: /tour|báo giá|lộ trình/i },
  { path: '/tour-builder', expect: /tour|bóc|lịch trình/i },
  { path: '/quotes',       expect: /báo giá/i },
  { path: '/customers',    expect: /khách/i },
  { path: '/deals',        expect: /cơ hội|deal/i },
  { path: '/assistant',    expect: /trợ lý|số liệu|hỏi/i },
  { path: '/mail',         expect: /thư|mail|hộp/i },
  { path: '/visa',         expect: /visa|hồ sơ/i },
  { path: '/visa/history', expect: /visa|lịch sử/i },
  { path: '/visa-config',  expect: /visa|cấu hình/i },
  { path: '/ncc-import',   expect: /nhà cung cấp|NCC|import/i },
  { path: '/ncc-list',     expect: /nhà cung cấp|NCC/i },
  { path: '/widget-admin', expect: /widget|nhúng|website/i },
  { path: '/workflows',    expect: /tự động|tác vụ|bảng tin/i },
  { path: '/insights',     expect: /bảng tin|thông báo|chưa có/i },
  { path: '/ai-usage',     expect: /chi phí|lượt|token/i },
  { path: '/help',         expect: /hướng dẫn/i },
  { path: '/flow-preview', expect: /sơ đồ|luồng|tác vụ/i },
  // Đường NGƯỜI DÙNG THẬT đi vào (nút "Xem sơ đồ" ở trang Tự động hoá) — khác /flow-preview trần,
  // và là nhánh code khác (có tham số type). Phải kiểm riêng.
  { path: '/flow-preview/sale-brief', expect: /bản tin sáng|sơ đồ/i },
  // Mã tác vụ KHÔNG có sơ đồ → phải nói thẳng là chưa có, TUYỆT ĐỐI không rơi về sơ đồ tác vụ khác
  // (hiện sơ đồ sai là giao diện nói dối). Dùng mã bịa vì hiện cả 10 tác vụ đều đã vẽ sơ đồ.
  { path: '/flow-preview/khong-ton-tai-abc', expect: /chưa có sơ đồ/i },
];

const IGNORE = [
  /favicon/i, /401|403|Unauthorized/i, /net::ERR_/i, /Failed to load resource/i,
  /ResizeObserver loop/i, /tinymce/i,
  /429|quota/i,                     // hết lượt AI — chuyện vận hành, không phải lỗi trang
];
const isRealError = (t) => !IGNORE.some(re => re.test(t));

test.describe('Quét mọi trang (đã đăng nhập)', () => {
  test.skip(!SESSION, 'Chưa có E2E_SESSION — bỏ qua. Xem hướng dẫn ở đầu file.');

  test.beforeEach(async ({ page }) => {
    // addInitScript chạy TRƯỚC script của trang → app thấy phiên ngay lần đọc đầu, không bị đẩy
    // về trang giới thiệu rồi mới nhận ra là đã đăng nhập.
    await page.addInitScript((sid) => {
      localStorage.setItem('tourkit_tk_session', sid);
    }, SESSION);
  });

  for (const p of PAGES) {
    test(`${p.path}`, async ({ page }) => {
      const errors = [];
      page.on('console', m => { if (m.type() === 'error' && isRealError(m.text())) errors.push(m.text()); });
      page.on('pageerror', e => { if (isRealError(String(e))) errors.push('pageerror: ' + String(e)); });

      const resp = await page.goto(p.path, { waitUntil: 'domcontentloaded' });
      expect(resp?.status(), `${p.path} trả HTTP ${resp?.status()}`).toBe(200);

      await page.waitForFunction(() => !document.getElementById('boot-splash'), { timeout: 25_000 })
        .catch(() => {});

      // 1. Giao diện vẽ ra
      expect(await page.locator('#root > *').count(), `${p.path}: #root rỗng`).toBeGreaterThan(0);

      // 2. KHÔNG bị đẩy về trang giới thiệu — đây là chốt chặn chống "xanh giả": nếu phiên hỏng thì
      //    mọi trang đều thành trang giới thiệu và mọi assertion khác vẫn có thể qua.
      expect(await page.locator('#seo-prerender').count()).toBe(0);
      const onLanding = await page.locator('h1', { hasText: /gánh việc tour/i }).count();
      expect(onLanding, `${p.path}: bị đẩy về trang giới thiệu — phiên E2E_SESSION còn dùng được không?`).toBe(0);

      // 3. Có chữ RIÊNG của trang đó
      const body = await page.locator('body').innerText();
      expect(body, `${p.path}: không thấy chữ đặc trưng của trang`).toMatch(p.expect);

      // 4. Không lỗi JS thật
      expect(errors, `${p.path} lỗi console:\n` + errors.join('\n')).toEqual([]);
    });
  }
});
