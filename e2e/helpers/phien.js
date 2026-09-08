// helpers/phien.js — MỘT chỗ duy nhất biết phiên đăng nhập dùng để chạy E2E.
//
// ⚠️ TRƯỚC 08/09/2026 BẢY FILE SPEC CÙNG GHIM CỨNG MỘT MÃ PHIÊN trong mã nguồn. Hai cái sai cùng
// lúc, và cái thứ hai mới là cái nguy:
//
//   1. Phiên hết hạn thì bài không đỏ mà lặng lẽ "xanh" — vài file bắt 401 rồi console.warn và
//      return, tức Playwright ghi PASSED. Bộ E2E báo xanh trong khi không bài nào thật sự chạy.
//      Đúng kiểu hỏng mà nhánh chat vừa trả giá: xanh vì không chạy, tệ hơn không có bài.
//   2. Mã phiên là KHOÁ TRUY CẬP vào dữ liệu công ty thật, và nó nằm trong repo — còn nguyên
//      trong lịch sử git kể cả sau khi xoá khỏi file. Phiên cũ đó phải coi là đã lộ và cần huỷ.
//
// Nay phiên đi qua biến môi trường, và THIẾU thì bài BỎ QUA CÓ TÊN (Playwright in "skipped",
// đếm riêng) chứ không giả vờ xanh.
//
//   $env:E2E_SESSION='<sessionId>'
//
// Lấy sessionId: đăng nhập app rồi mở DevTools Console →
//   localStorage.getItem('tourkit_tk_session')

export const PHIEN = process.env.E2E_SESSION || '';

export const THIEU_PHIEN =
  'Chưa có E2E_SESSION — bỏ qua. Đặt biến môi trường rồi chạy lại (xem e2e/README.md).';

/**
 * Bơm phiên vào localStorage TRƯỚC khi kịch bản của trang chạy.
 *
 * Sáu file spec từng chép y hệt hàm này. Chép thì mỗi bản là một chỗ phải sửa khi cách lưu phiên
 * đổi — mà cách lưu phiên NẰM Ở core/auth.jsx, không nằm ở đây, nên nó đổi được bất cứ lúc nào.
 */
export async function bomPhien(page) {
  await page.addInitScript((sid) => {
    try {
      localStorage.setItem('tourkit_tk_session', sid);
      localStorage.setItem('tourkit_skip_login_gate', '1');
    } catch { /* trình duyệt chặn lưu trữ — trang tự xử như khách */ }
  }, PHIEN);
}
