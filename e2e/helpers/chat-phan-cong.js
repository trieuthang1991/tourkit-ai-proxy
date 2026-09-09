// helpers/chat-phan-cong.js — cửa an toàn + dọn dẹp cho nhóm bài phân công hội thoại.
//
// ⚠️ VÌ SAO CẦN MỘT FILE RIÊNG CHO CHUYỆN NÀY. Mọi bài E2E có sẵn của repo chỉ ĐỌC. Nhóm phân
// công là nhóm ĐẦU TIÊN phải GHI: nó nhận việc, giao việc, và lưu cấu hình phân công của cả công
// ty. Ba thứ đó mà chạy nhầm chỗ thì không có "undo":
//
//   • playwright.config.js mặc định trỏ vào https://mobile-api2.tourkit.vn — BẢN CHẠY THẬT.
//     Gõ `npm test` không kèm gì là bài ghi sẽ chạy ở đó. Nên nhóm này TỰ CHẶN mình, không dựa
//     vào việc người chạy nhớ đặt biến môi trường đúng.
//   • Đường gửi tin (/send, /send-template) nối vào bot Telegram THẬT. Một tin "test" là một tin
//     tới điện thoại của một người thật. Nhóm này TUYỆT ĐỐI không gọi hai đường đó.
//
// Cách chạy (PowerShell):
//   cd e2e
//   $env:E2E_TARGET='local'
//   $env:E2E_SESSION='<phiên quản trị>'
//   $env:E2E_SESSION_NV='<phiên nhân viên thường>'
//   $env:E2E_CHO_PHEP_GHI='1'
//   npx playwright test tests/07-chat-phan-cong-api.spec.js tests/07b-chat-phan-cong-giao-dien.spec.js
//
// Lấy sessionId: đăng nhập app rồi mở DevTools Console → localStorage.getItem('tourkit_tk_session')
// KHÔNG hardcode vào file: phiên là thứ mở được dữ liệu công ty thật.

export const PHIEN_QUAN_TRI = process.env.E2E_SESSION || '';
export const PHIEN_NHAN_VIEN = process.env.E2E_SESSION_NV || '';
export const CHO_PHEP_GHI = process.env.E2E_CHO_PHEP_GHI === '1';

/** Máy chủ nào là bản CHẠY THẬT — cấm ghi lên, không có ngoại lệ nào bật được. */
const MAY_CHU_THAT = [/mobile-api2\.tourkit\.vn/i, /\/\/erp\./i, /travelai\.vn/i];

/** Tenant nào là dữ liệu thật — quy ước của repo: chỉ thử trên staging, không đụng erp. */
const TENANT_THAT = [/^erp\./i];

export const GOC = '/api/v1/chat';

/**
 * Vì sao KHÔNG dùng test.skip cho mấy cửa này: bỏ qua im lặng là kiểu hỏng mà chính nhánh chat
 * vừa trả giá — 1219 test xanh mà hai lỗi chặn vẫn lọt. Một bài "xanh" vì nó không chạy thì tệ
 * hơn không có bài. Thiếu điều kiện thì bài ĐỎ và nói rõ thiếu gì.
 */
export function batBuocDuLieuVao(baseURL) {
  const thieu = [];
  if (!PHIEN_QUAN_TRI) thieu.push('E2E_SESSION (phiên quản trị)');
  if (!PHIEN_NHAN_VIEN) thieu.push('E2E_SESSION_NV (phiên nhân viên thường)');
  if (!CHO_PHEP_GHI) thieu.push("E2E_CHO_PHEP_GHI=1 (xác nhận cho phép GHI)");
  if (thieu.length) {
    throw new Error(
      'Nhóm bài phân công cần đủ biến môi trường, còn thiếu:\n  - ' + thieu.join('\n  - ') +
      '\nXem hướng dẫn ở đầu e2e/helpers/chat-phan-cong.js.');
  }
  const url = String(baseURL || '');
  const trung = MAY_CHU_THAT.find(re => re.test(url));
  if (trung) {
    throw new Error(
      `TỪ CHỐI CHẠY: "${url}" là bản chạy thật (khớp ${trung}). Nhóm bài này GHI dữ liệu — ` +
      'nhận việc, giao việc, lưu cấu hình phân công của cả công ty. Đặt E2E_TARGET=local rồi chạy lại.');
  }
}

/** Tiêu đề mang phiên. Tách hàm để không chỗ nào lỡ tay quên header rồi nhận 401 khó hiểu. */
export const nhu = (phien, them = {}) => ({ 'X-Session-Id': phien, ...them });

/** Tenant của phiên — dùng để chặn ghi nhầm sang công ty dữ liệu thật. */
export async function layTenant(request, phien) {
  const r = await request.get('/api/v1/session', { headers: nhu(phien) });
  if (!r.ok()) throw new Error(`Phiên không dùng được (${r.status()}). Lấy lại sessionId rồi chạy lại.`);
  const d = await r.json();
  const t = String(d.tenantId || '');
  const trung = TENANT_THAT.find(re => re.test(t));
  if (trung) throw new Error(`TỪ CHỐI CHẠY: phiên thuộc tenant "${t}" — dữ liệu thật, cấm ghi.`);
  return t;
}

/**
 * Đọc phản hồi một cách AN TOÀN, và trả luôn kiểu nội dung.
 *
 * ⚠️ Đây là hàm quan trọng nhất file. Lỗi chặn ngày 08/09/2026 có triệu chứng đúng như thế này:
 * máy chủ trả 404 kèm NGUYÊN MỘT TRANG HTML (trang SPA nuốt đường /api không khớp), còn bài kiểm
 * nào chỉ gọi r.json() sẽ NÉM ở chỗ khác rồi báo một lỗi chẳng liên quan. Trả cả `kieu` ra ngoài
 * để bài kiểm khẳng định được "đường API phải trả JSON", chứ không chỉ khẳng định mã trạng thái.
 */
export async function doc(r) {
  const kieu = r.headers()['content-type'] || '';
  const chu = await r.text();
  let json = null;
  if (kieu.includes('json')) { try { json = JSON.parse(chu); } catch { /* để null */ } }
  return { ma: r.status(), kieu, chu, json, laHtml: kieu.includes('html') };
}

/** Ảnh chụp trạng thái TRƯỚC khi nghịch — để trả lại nguyên vẹn sau khi chạy xong. */
export async function chupTrangThai(request, phien, maHoiThoai) {
  const ch = await doc(await request.get(`${GOC}/assign-settings`, { headers: nhu(phien) }));
  if (ch.ma !== 200 || !ch.json) {
    throw new Error(`Không đọc được cấu hình phân công (${ch.ma}) — dừng trước khi ghi bất cứ gì.`);
  }
  const hoi = await doc(await request.get(`${GOC}/conversations/${maHoiThoai}`, { headers: nhu(phien) }));
  return {
    cauHinh: {
      mode: ch.json.mode,
      scopeOwnOnly: ch.json.scopeOwnOnly,
      autoAssignOnReply: ch.json.autoAssignOnReply,
      memberIds: ch.json.memberIds || [],
    },
    nguoiPhuTrach: hoi.json?.conversation?.assignedUserId ?? null,
  };
}

/**
 * Trả lại đúng trạng thái đã chụp, và KHẲNG ĐỊNH là đã trả được.
 *
 * Dọn dẹp mà không kiểm lại thì lần chạy sau bắt đầu từ một trạng thái không ai biết — bài đỏ
 * hôm sau sẽ bị đổ cho lần sửa mới nhất chứ không ai ngờ tới lần chạy hôm trước.
 */
export async function traTrangThai(request, phien, maHoiThoai, anh) {
  await request.put(`${GOC}/assign-settings`, {
    headers: nhu(phien, { 'Content-Type': 'application/json' }),
    data: anh.cauHinh,
  });
  if (anh.nguoiPhuTrach == null) {
    await request.delete(`${GOC}/conversations/${maHoiThoai}/assign`, { headers: nhu(phien) });
  } else {
    await request.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(phien, { 'Content-Type': 'application/json' }),
      data: { userId: anh.nguoiPhuTrach },
    });
  }
  const lai = await chupTrangThai(request, phien, maHoiThoai);
  const lech = [];
  if (lai.nguoiPhuTrach !== anh.nguoiPhuTrach) lech.push('người phụ trách');
  if (lai.cauHinh.scopeOwnOnly !== anh.cauHinh.scopeOwnOnly) lech.push('scopeOwnOnly');
  if (lai.cauHinh.mode !== anh.cauHinh.mode) lech.push('chế độ');
  if (String(lai.cauHinh.memberIds) !== String(anh.cauHinh.memberIds)) lech.push('đội trực');
  if (lech.length) {
    throw new Error('DỌN DẸP HỤT — chưa trả lại được: ' + lech.join(', ') +
      '. Vào Cấu hình phân công sửa tay trước khi chạy lại.');
  }
}

/** Mã hội thoại để thử: lấy cái mới nhất mà quản trị nhìn thấy. */
export async function hoiThoaiDeThu(request, phien) {
  const r = await doc(await request.get(`${GOC}/conversations?take=1`, { headers: nhu(phien) }));
  if (r.ma !== 200 || !r.json) throw new Error(`Không đọc được danh sách hội thoại (${r.ma}).`);
  const ds = r.json.items || [];
  if (!ds.length) {
    throw new Error('Công ty này chưa có hội thoại nào — nhóm bài phân công không có gì để phân công. ' +
      'Nhắn một tin vào kênh đã nối rồi chạy lại.');
  }
  return ds[0].id;
}

/**
 * Mã người của một phiên — đọc thẳng từ `/assign-settings` (ô `meId`).
 *
 * ⚠️ TRƯỚC 09/09/2026 hàm này lấy mã bằng cách bắt phiên đó NHẬN VIỆC rồi đọc `assignedUserId`.
 * Cách ấy chết khi phạm vi xem chuyển sang theo quyền CRM: nhân viên chỉ có `CHAT_XEM` KHÔNG
 * nhìn thấy hội thoại chưa ai nhận, nên lượt nhận việc trả 404 — và cả nhóm bài đứng ở beforeAll.
 *
 * Đó không phải lỗi sản phẩm: chính bài C4 dưới đây khẳng định "hội thoại CHƯA AI NHẬN không
 * hiện với nhân viên thường". Cái sai là bộ đồ nghề của bài test đi vòng qua một thao tác GHI để
 * đọc một dữ kiện — nay đọc thẳng, không đụng gì.
 */
export async function maNguoiCuaPhien(request, phien) {
  const r = await doc(await request.get(`${GOC}/assign-settings`, { headers: nhu(phien) }));
  if (r.ma !== 200 || !r.json?.meId) {
    throw new Error(`Không xác định được mã người của phiên (${r.ma} ${r.chu.slice(0, 120)}).`);
  }
  return r.json.meId;
}
