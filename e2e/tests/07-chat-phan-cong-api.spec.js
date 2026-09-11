// 07-chat-phan-cong-api.spec.js — phân công hội thoại + phân quyền xem, đo bằng REQUEST THẬT.
//
// ⚠️ VÌ SAO BÀI NÀY TỒN TẠI. Ngày 08/09/2026 nhánh phân công đã qua 13 việc, 3 vòng review và
// 1219 test xanh — mà vẫn còn HAI lỗi chặn, cả hai tìm ra trong nửa giờ gọi API thật:
//
//   1. Nút "Nhận chăm sóc" chết hoàn toàn. Tham số thân của minimal API — kể cả khai có dấu hỏi —
//      vẫn gắn AcceptsMetadata vào route, nên request thiếu Content-Type bị loại ở tầng ĐỊNH
//      TUYẾN rồi rơi xuống trang SPA: 404 kèm HTML. Bấm nút không có gì xảy ra, không lỗi nào hiện.
//   2. Bấm Lưu ở màn hình Cấu hình phân công MỘT LẦN là mất cả hộp thư. Dapper không dựng nổi
//      record vị trí khi cột là integer[]. Chưa có dòng cấu hình thì truy vấn trả null nên mọi
//      thứ chạy êm — hỏng chỉ lộ sau lần lưu đầu tiên, và cửa quyền xem gọi hàm đó ở MỌI request.
//
// Cả hai đều VÔ HÌNH với bộ test của repo: bộ đó đọc văn bản nguồn, còn hai lỗi này là hành vi
// của khung và của driver, không để lại dấu vết nào trong mã mình viết. Chỉ request thật mới thấy.
// Mỗi bài dưới đây neo vào một trong hai lỗi đó, hoặc vào một luật mà chúng làm lộ ra.
//
// Hướng dẫn chạy + các cửa an toàn: xem đầu file e2e/helpers/chat-phan-cong.js.
import { test, expect, request as moiPhienApi } from '@playwright/test';
import {
  PHIEN_QUAN_TRI, PHIEN_NHAN_VIEN, GOC,
  batBuocDuLieuVao, nhu, doc, layTenant,
  chupTrangThai, traTrangThai, hoiThoaiDeThu, maNguoiCuaPhien,
} from '../helpers/chat-phan-cong.js';

let api;            // một ngữ cảnh dùng chung — cùng máy chủ, cùng cookie, dễ suy luận
let maHoiThoai;     // hội thoại đem ra thử
let anhGoc;         // trạng thái trước khi nghịch, để trả lại
let maQuanTri;
let maNhanVien;

const datCauHinh = (cauHinh) => api.put(`${GOC}/assign-settings`, {
  headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }),
  data: cauHinh,
});

test.beforeAll(async ({ baseURL }) => {
  batBuocDuLieuVao(baseURL);
  api = await moiPhienApi.newContext({ baseURL });

  const tQuanTri = await layTenant(api, PHIEN_QUAN_TRI);
  const tNhanVien = await layTenant(api, PHIEN_NHAN_VIEN);
  // Hai phiên khác công ty thì mọi khẳng định về luật xem đều vô nghĩa (bên kia không thấy gì là
  // đương nhiên, chẳng chứng minh được luật nào chạy). Bắt lỗi ở đây, đừng để bài xanh giả.
  expect(tNhanVien, 'Hai phiên phải cùng một công ty').toBe(tQuanTri);

  maHoiThoai = await hoiThoaiDeThu(api, PHIEN_QUAN_TRI);
  anhGoc = await chupTrangThai(api, PHIEN_QUAN_TRI, maHoiThoai);

  // Đọc mã người bằng lượt ĐỌC, không bằng lượt ghi — xem chú thích ở maNguoiCuaPhien.
  // Nhờ vậy phần dựng bài không còn để lại dấu vết nào trên hội thoại đem ra thử.
  maQuanTri = await maNguoiCuaPhien(api, PHIEN_QUAN_TRI);
  maNhanVien = await maNguoiCuaPhien(api, PHIEN_NHAN_VIEN);
  expect(maNhanVien, 'Hai phiên phải là HAI người khác nhau').not.toBe(maQuanTri);
});

test.afterAll(async () => {
  if (api && anhGoc) await traTrangThai(api, PHIEN_QUAN_TRI, maHoiThoai, anhGoc);
  if (api) await api.dispose();
});

// ── Nhóm B — ngữ nghĩa request ────────────────────────────────────────────────

test.describe('Ngữ nghĩa request', () => {
  test('B1 — nhận việc KHÔNG kèm Content-Type vẫn phải tới được handler', async () => {
    // ĐÂY LÀ BÀI QUAN TRỌNG NHẤT CẢ NHÓM: nó tái hiện đúng cú bấm "Nhận chăm sóc" của giao diện.
    // Giao diện gửi POST không thân, không header. Trước 08/09/2026 request đó nhận 404 kèm HTML.
    await api.delete(`${GOC}/conversations/${maHoiThoai}/assign`, { headers: nhu(PHIEN_QUAN_TRI) });

    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign/me`,
      { headers: nhu(PHIEN_QUAN_TRI) }));

    expect(r.laHtml, `Rơi xuống trang SPA: ${r.ma} ${r.kieu}. Route đang đòi Content-Type — ` +
      'nghĩa là nó lại mọc tham số thân.').toBe(false);
    expect(r.ma).toBe(200);
    expect(r.json.assignedUserId).toBe(maQuanTri);
  });

  test('B2 — không đường chat nào được trả HTML thay cho JSON', async () => {
    // Luật TỔNG QUÁT rút ra từ lỗi B1: MapFallback nuốt mọi đường /api không khớp và trả nguyên
    // trang SPA kèm 404 (có lúc kèm 200). Client nhận HTML ở chỗ nó đợi JSON là hỏng câm — không
    // ngoại lệ nào ném, không lỗi nào hiện. Quét cả nhóm đường thay vì chỉ đường vừa sửa: lần sau
    // ai thêm route mới mà lỡ tay, bài này đỏ chứ không đợi khách phát hiện.
    const duong = [
      `${GOC}/assign-settings`,
      `${GOC}/conversations?take=1`,
      `${GOC}/conversations/${maHoiThoai}`,
      `${GOC}/conversations/${maHoiThoai}/audit`,
      `${GOC}/conversations/${maHoiThoai}/tags`,
      `${GOC}/conversations/${maHoiThoai}/notes`,
      `${GOC}/channels`,
      `${GOC}/bot-settings`,
    ];
    for (const d of duong) {
      const r = await doc(await api.get(d, { headers: nhu(PHIEN_QUAN_TRI) }));
      expect(r.laHtml, `${d} trả HTML (${r.ma}) — request không tới được handler`).toBe(false);
      expect(r.kieu, `${d} trả kiểu "${r.kieu}", không phải JSON`).toContain('json');
    }
  });

  test('B3 — giao việc thiếu mã người phải BÁO LỖI, không im lặng gán cho "người số 0"', async () => {
    // {} rơi về 0 (giá trị mặc định của int) mà không lỗi gì. Gán hội thoại cho "người số 0"
    // nghĩa là nó biến mất khỏi tầm nhìn mọi người mà không ai biết vì sao.
    //
    // LUẬT CHUNG cho MỌI thân hỏng: không bao giờ được 200. Đó là vế phải giữ bằng mọi giá.
    for (const than of [{}, { userId: 0 }, { userId: null }, { userId: 'abc' }]) {
      const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
        headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: than,
      }));
      expect(r.ma, `thân ${JSON.stringify(than)} phải bị từ chối`).toBe(400);
    }

    // Vế THỨ HAI, hẹp hơn: chỉ đòi CÂU LỖI ĐỌC ĐƯỢC ở hai ca mà chính handler xử. Đo được
    // 08/09/2026: {"userId":null} và {"userId":"abc"} trả 400 THÂN RỖNG, không cả content-type —
    // vì chúng chết ở tầng ràng buộc thân của khung, TRƯỚC khi mã ta chạy dòng nào. Đòi câu lỗi ở
    // đó là đòi thứ mình không cầm được, và giao diện đã có câu dự phòng cho ca này. Ranh giới
    // đúng nằm ở "ai là người trả lời", không nằm ở "thân trông có vẻ sai".
    for (const than of [{}, { userId: 0 }]) {
      const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
        headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: than,
      }));
      expect(r.json?.error, `thân ${JSON.stringify(than)} do handler xử — phải nói rõ thiếu gì`)
        .toBeTruthy();
    }
  });

  test('B4 — nhả việc đi đường DELETE và trả về đúng "chưa ai phụ trách"', async () => {
    await api.post(`${GOC}/conversations/${maHoiThoai}/assign/me`, { headers: nhu(PHIEN_QUAN_TRI) });
    const r = await doc(await api.delete(`${GOC}/conversations/${maHoiThoai}/assign`,
      { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.ma).toBe(200);
    expect(r.json.assignedUserId).toBeNull();
  });

  test('B5 — người đang giữ bấm nhận lại chính nó thì KHÔNG bị coi là tranh việc', async () => {
    // Khoá chống tranh việc là "assigned_user_id IS NULL OR = @userId". Thiếu vế sau thì người
    // đang giữ bấm lần hai nhận 409 "người khác đang xử lý" — trong khi người đó là chính họ.
    await api.delete(`${GOC}/conversations/${maHoiThoai}/assign`, { headers: nhu(PHIEN_QUAN_TRI) });
    const lan1 = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign/me`,
      { headers: nhu(PHIEN_QUAN_TRI) }));
    const lan2 = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign/me`,
      { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(lan1.ma).toBe(200);
    expect(lan2.ma).toBe(200);
  });
});

// ── Nhóm C — vai trò và luật xem ──────────────────────────────────────────────

test.describe('Vai trò và luật xem', () => {
  test('C1 — hai phiên phải ra hai vai khác nhau', async () => {
    const qt = await doc(await api.get(`${GOC}/assign-settings`, { headers: nhu(PHIEN_QUAN_TRI) }));
    const nv = await doc(await api.get(`${GOC}/assign-settings`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    expect(qt.json.isAdmin, 'E2E_SESSION phải là phiên QUẢN TRỊ').toBe(true);
    expect(nv.json.isAdmin, 'E2E_SESSION_NV phải là phiên NHÂN VIÊN THƯỜNG').toBe(false);
  });

  test('C2 — bật kẹp quyền thì nhân viên chỉ thấy việc của mình', async () => {
    await datCauHinh({ mode: 1, scopeOwnOnly: true, autoAssignOnReply: false,
                       memberIds: [maQuanTri, maNhanVien] });
    await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { userId: maQuanTri },
    });

    const cuaQuanTri = await doc(await api.get(`${GOC}/conversations?take=50`, { headers: nhu(PHIEN_QUAN_TRI) }));
    const cuaNhanVien = await doc(await api.get(`${GOC}/conversations?take=50`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    const co = (r) => (r.json.items || []).some(c => c.id === maHoiThoai);
    expect(co(cuaQuanTri), 'quản trị phải thấy hội thoại này').toBe(true);
    expect(co(cuaNhanVien), 'nhân viên thường KHÔNG được thấy việc của người khác').toBe(false);

    // Giao lại cho nhân viên thì họ phải thấy — vế ngược, để bài không xanh chỉ vì danh sách rỗng.
    await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { userId: maNhanVien },
    });
    const sauKhiGiao = await doc(await api.get(`${GOC}/conversations?take=50`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    expect(co(sauKhiGiao), 'giao cho họ rồi thì họ phải thấy').toBe(true);
  });

  test('C3 — từ chối xem phải là 404, và KHÔNG phân biệt được với id không tồn tại', async () => {
    // 403 nghĩa là "có hội thoại này nhưng anh không được xem" — tức xác nhận đúng cái đang giấu.
    // Dò tuần tự theo id là biết công ty có bao nhiêu khách. Hai phản hồi phải GIỐNG NHAU.
    await datCauHinh({ mode: 1, scopeOwnOnly: true, autoAssignOnReply: false,
                       memberIds: [maQuanTri, maNhanVien] });
    await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { userId: maQuanTri },
    });

    const cuaNguoiKhac = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}`,
      { headers: nhu(PHIEN_NHAN_VIEN) }));
    const khongCo = await doc(await api.get(`${GOC}/conversations/999999999`,
      { headers: nhu(PHIEN_NHAN_VIEN) }));

    expect(cuaNguoiKhac.ma).toBe(404);
    expect(khongCo.ma).toBe(404);
    expect(cuaNguoiKhac.chu, 'thân hai phản hồi phải giống nhau, không thì vẫn đoán ra được')
      .toBe(khongCo.chu);

    // Mọi đường con cũng phải theo cùng luật — nhật ký, nhãn, ghi chú đều lộ được sự tồn tại.
    for (const duoi of ['/audit', '/tags', '/notes']) {
      const r = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}${duoi}`,
        { headers: nhu(PHIEN_NHAN_VIEN) }));
      expect(r.ma, `${duoi} phải trả 404 cho người không được xem`).toBe(404);
    }
  });

  test('C6 — đường TẢI TỆP cũng phải qua luật xem, không chỉ kẹp theo công ty', async () => {
    // Lỗ thật, hoãn từ review việc 3 rồi vá 08/09/2026. Đường tải tệp đính kèm chỉ kiểm "tin có
    // thuộc công ty này không". Nhân viên từng phụ trách một hội thoại, sau khi bị chuyển giao vẫn
    // tải lại được ảnh/tệp khách đã gửi — chỉ cần còn giữ mã tin. Đóng cửa trước mà để ngỏ cửa sau
    // thì luật xem chỉ là hình thức.
    //
    // Khẳng định neo vào tính CHỐNG DÒ, không neo vào mã trạng thái trần: hội thoại của người khác
    // phải trả về ĐÚNG NHƯ một mã tin không tồn tại. Trả khác nhau là vẫn đoán ra được có gì ở đó.
    // (Cơ chế — hàm kho nhận NguoiXem và dùng đúng mệnh đề chung — do chốt canh văn bản nguồn
    //  Duong_tai_tep_cung_phai_qua_luat_xem... khoá lại; ở đây chỉ đo hành vi thật.)
    await datCauHinh({ mode: 1, scopeOwnOnly: true, autoAssignOnReply: false,
                       memberIds: [maQuanTri, maNhanVien] });
    await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { userId: maQuanTri },
    });

    // Lấy một mã tin CÓ THẬT trong hội thoại đó.
    const ct = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    const tin = (ct.json?.messages || [])[0];
    expect(tin, 'hội thoại đem ra thử phải có ít nhất một tin').toBeTruthy();

    const cuaNguoiKhac = await doc(await api.get(
      `${GOC}/messages/${tin.id}/file?fid=e2e-khong-co-that`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    const khongCo = await doc(await api.get(
      `${GOC}/messages/999999999/file?fid=e2e-khong-co-that`, { headers: nhu(PHIEN_NHAN_VIEN) }));

    expect(cuaNguoiKhac.ma, 'tệp của hội thoại người khác phải bị từ chối').toBe(404);
    expect(cuaNguoiKhac.chu, 'phải giống hệt mã tin không tồn tại, không thì vẫn dò ra được')
      .toBe(khongCo.chu);
  });

  test('C4 — hội thoại CHƯA AI NHẬN không hiện với nhân viên thường', async () => {
    // ĐÂY LÀ QUYẾT ĐỊNH, KHÔNG PHẢI SƠ SUẤT (đặc tả mục 6.3). Ai đó "sửa cho tiện" bằng cách thêm
    // vế OR assigned_user_id IS NULL sẽ mở hàng chờ cho cả công ty — bài này chặn lại.
    await datCauHinh({ mode: 1, scopeOwnOnly: true, autoAssignOnReply: false,
                       memberIds: [maQuanTri, maNhanVien] });
    await api.delete(`${GOC}/conversations/${maHoiThoai}/assign`, { headers: nhu(PHIEN_QUAN_TRI) });

    const r = await doc(await api.get(`${GOC}/conversations?take=50`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    const co = (r.json.items || []).some(c => c.id === maHoiThoai);
    expect(co, 'hội thoại chưa ai nhận không được hiện với nhân viên thường').toBe(false);
  });

  test('C5 — đội trực chỉ ràng buộc nhân viên thường, và CHỈ ở chế độ xoay vòng', async () => {
    // ⚠️ BÀI NÀY ĐÃ ĐỔI LUẬT ngày 08/09/2026. Bản trước đòi nhân viên thường bị chặn khi đội trực
    // rỗng — ở MỌI chế độ. Nhưng đội trực sinh ra cho vòng quay: chế độ thủ công không có lượt
    // nào để chia, nên đem vòng quay đi chặn việc giao tay là mượn luật của việc này áp cho việc
    // khác. Ở chế độ thủ công, đội trực rỗng là trạng thái MẶC ĐỊNH của mọi công ty.
    const giao = async (phien, choAi) => doc(await api.post(`${GOC}/conversations/${maHoiThoai}/assign`, {
      headers: nhu(phien, { 'Content-Type': 'application/json' }), data: { userId: choAi },
    }));

    // (1) THỦ CÔNG + đội trực rỗng: cả hai vai đều giao được.
    await datCauHinh({ mode: 1, scopeOwnOnly: false, autoAssignOnReply: false, memberIds: [] });
    expect((await giao(PHIEN_QUAN_TRI, maNhanVien)).ma,
      'quản trị phải giao được việc dù chưa lập đội trực').toBe(200);
    // Giao cho nhân viên TRƯỚC là có chủ đích: từ khi phạm vi xem đi theo quyền CRM, người chỉ có
    // CHAT_XEM chỉ thao tác được trên hội thoại họ NHÌN THẤY — tức việc của chính họ.
    expect((await giao(PHIEN_NHAN_VIEN, maQuanTri)).ma,
      'chế độ thủ công KHÔNG được đòi đội trực').toBe(200);

    // (2) XOAY VÒNG + người được giao NẰM NGOÀI đội trực: nhân viên bị chặn, quản trị thì không.
    await giao(PHIEN_QUAN_TRI, maNhanVien);
    await datCauHinh({ mode: 2, scopeOwnOnly: false, autoAssignOnReply: false, memberIds: [maNhanVien] });
    const bịChặn = await giao(PHIEN_NHAN_VIEN, maQuanTri);
    expect(bịChặn.ma, 'nhân viên thường không được giao cho người ngoài đội trực').toBe(400);
    expect(bịChặn.json?.error).toBeTruthy();
    expect((await giao(PHIEN_QUAN_TRI, maQuanTri)).ma,
      'quản trị vẫn giao được cho người ngoài đội trực').toBe(200);
  });
});

// ── Nhóm E — lọc theo nhãn ────────────────────────────────────────────────────

test.describe('E — Lọc theo nhãn', () => {
  // Slug cố định, dọn ở afterAll. Cố định chứ không ngẫu nhiên: chạy hỏng giữa chừng thì lần sau
  // vẫn dọn được đúng dòng đó thay vì để lại rác mang tên ngẫu nhiên không ai nhận ra.
  const NHAN = 'e2e-loc-nhan';
  const NHAN_LA = 'e2e-khong-co-nhan-nay';
  let maNhan;   // id dòng danh mục, để xoá

  test.beforeAll(async () => {
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/tags`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { tag: NHAN },
    }));
    expect(r.ma, 'gắn nhãn thử phải được').toBe(200);

    const dm = await doc(await api.get(`${GOC}/tags`, { headers: nhu(PHIEN_QUAN_TRI) }));
    maNhan = (dm.json.items || []).find(n => n.slug === NHAN)?.id;
    expect(maNhan, 'nhãn gõ tay phải tự vào danh mục — chip lọc đọc từ đó').toBeTruthy();
  });

  test.afterAll(async () => {
    if (!api) return;
    await api.delete(`${GOC}/conversations/${maHoiThoai}/tags/${NHAN}`, { headers: nhu(PHIEN_QUAN_TRI) });
    if (maNhan) await api.delete(`${GOC}/tags/${maNhan}`, { headers: nhu(PHIEN_QUAN_TRI) });
  });

  test('E1 — lọc đúng nhãn thì thấy hội thoại, và chip đếm nói về ĐÚNG danh sách đó', async () => {
    const r = await doc(await api.get(`${GOC}/conversations?tag=${NHAN}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.laHtml, 'rơi xuống trang SPA — route không nhận thêm tham số tag').toBe(false);
    expect(r.ma).toBe(200);

    const ids = (r.json.items || []).map(x => x.id);
    expect(ids, 'hội thoại vừa gắn nhãn phải nằm trong kết quả lọc').toContain(maHoiThoai);

    // Đây là vế dễ quên nhất: sửa câu liệt kê mà quên câu đếm thì danh sách hiện vài dòng còn
    // chip trạng thái ngay trên nó vẫn đếm cả công ty. Danh sách thử nhỏ hơn một trang nên
    // tổng phải bằng đúng số dòng trả về.
    expect(r.json.counts.tong, 'chip đếm không đi theo bộ lọc nhãn').toBe(ids.length);
  });

  test('E2 — nhãn không tồn tại phải ra RỖNG, không phải "không lọc"', async () => {
    // Nếu tham số bị bỏ qua ở đâu đó trên đường đi, bài E1 vẫn xanh (hội thoại nằm trong danh
    // sách đầy đủ). Chỉ bài này phân biệt được "lọc đúng" với "không lọc gì cả".
    const r = await doc(await api.get(`${GOC}/conversations?tag=${NHAN_LA}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.ma).toBe(200);
    expect(r.json.items, 'nhãn lạ mà vẫn ra danh sách nghĩa là tham số bị bỏ qua').toHaveLength(0);
    expect(r.json.counts.tong).toBe(0);
  });

  test('E3 — nhiều nhãn là HOẶC: nhãn thật cộng nhãn lạ vẫn thấy hội thoại', async () => {
    // Chốt đúng cái luật đã chọn. Đổi sang VÀ thì bài này đỏ — và nó ĐÁNG đỏ, vì đó là đổi thói
    // quen người dùng chứ không phải đổi chi tiết kỹ thuật.
    const r = await doc(await api.get(`${GOC}/conversations?tag=${NHAN},${NHAN_LA}`,
      { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.ma).toBe(200);
    expect((r.json.items || []).map(x => x.id)).toContain(maHoiThoai);
  });

  test('E4 — tham số tag rỗng KHÔNG được làm trắng danh sách', async () => {
    // Giao diện bỏ hết nhãn thì không gửi tham số nữa, nhưng URL người dùng sửa tay hoặc lịch sử
    // trình duyệt vẫn có thể còn "?tag=". Rỗng phải hiểu là KHÔNG LỌC, không phải "không nhãn nào".
    const khongLoc = await doc(await api.get(`${GOC}/conversations`, { headers: nhu(PHIEN_QUAN_TRI) }));
    for (const q of ['?tag=', '?tag=,', '?tag=%20']) {
      const r = await doc(await api.get(`${GOC}/conversations${q}`, { headers: nhu(PHIEN_QUAN_TRI) }));
      expect(r.ma, `${q} phải trả 200`).toBe(200);
      expect((r.json.items || []).length, `${q} làm trắng danh sách`).toBe((khongLoc.json.items || []).length);
    }
  });
});

// ── Nhóm D — vòng đời cấu hình ────────────────────────────────────────────────

test.describe('Vòng đời cấu hình phân công', () => {
  test('D1 — LƯU cấu hình xong thì mọi đường chat vẫn sống', async () => {
    // ĐÂY LÀ BÀI CHO LỖI NẶNG NHẤT: trước 08/09/2026, bấm Lưu MỘT LẦN là mọi request chat trả 500,
    // vì cửa quyền xem đọc dòng cấu hình ở mỗi request mà driver không dựng nổi bản ghi có cột mảng.
    // Chưa có dòng thì truy vấn trả null nên chạy êm — nên bài kiểm phải LƯU TRƯỚC rồi mới gọi.
    await datCauHinh({ mode: 2, scopeOwnOnly: true, autoAssignOnReply: true,
                       memberIds: [maQuanTri, maNhanVien] });

    const duong = [
      `${GOC}/assign-settings`,
      `${GOC}/conversations?take=1`,
      `${GOC}/conversations/${maHoiThoai}`,
      `${GOC}/conversations/${maHoiThoai}/audit`,
      `${GOC}/channels`,
    ];
    for (const d of duong) {
      const r = await doc(await api.get(d, { headers: nhu(PHIEN_QUAN_TRI) }));
      expect(r.ma, `${d} trả ${r.ma} sau khi lưu cấu hình: ${r.chu.slice(0, 200)}`)
        .toBeLessThan(500);
    }
  });

  test('D2 — lưu rồi đọc lại phải ra đúng thứ đã lưu, kể cả đội trực', async () => {
    // Đội trực là một MẢNG SỐ — đúng chỗ mà driver hụt. Đọc lại được và đúng thứ tự/nội dung là
    // bằng chứng bản ghi dựng thật, không phải "không ném là coi như xong".
    const dat = { mode: 2, scopeOwnOnly: true, autoAssignOnReply: true,
                  memberIds: [maQuanTri, maNhanVien] };
    await datCauHinh(dat);
    const r = await doc(await api.get(`${GOC}/assign-settings`, { headers: nhu(PHIEN_QUAN_TRI) }));

    expect(r.ma).toBe(200);
    expect(r.json.mode).toBe(dat.mode);
    expect(r.json.scopeOwnOnly).toBe(dat.scopeOwnOnly);
    expect(r.json.autoAssignOnReply).toBe(dat.autoAssignOnReply);
    expect([...r.json.memberIds].sort()).toEqual([...dat.memberIds].sort());
  });
});
