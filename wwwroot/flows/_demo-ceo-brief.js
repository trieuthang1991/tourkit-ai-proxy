// Sơ đồ THIẾT KẾ: Bản tin sáng cho GIÁM ĐỐC (ceo-brief).
//
// CHƯA có workflow tương ứng ở backend → demo:true, mã có tiền tố '_'.
// Vẽ theo docs/superpowers/specs/2026-08-11-dot1-digest-insight-design.md §5.2
// (CeoBriefBuilder). Khi Đợt 1 code xong: đổi type thành 'ceo-brief', bỏ demo:true.
//
// Khác bản tin của sale ở 3 điểm, thể hiện ngay trên sơ đồ:
//   1. Phạm vi TOÀN CÔNG TY (sale chỉ thấy việc của mình) → có chốt kiểm quyền
//   2. So sánh 2 kỳ để thấy xu hướng, không chỉ số hôm nay
//   3. CÓ gọi AI (1 lần/công ty) để viết nhận định — nhưng AI lỗi thì vẫn ra bản số
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: '_demo-ceo-brief',
    demo: true,
    label: 'Bản tin sáng — Giám đốc',
    note: 'Thiết kế theo spec Đợt 1 §5.2. Trả lời: "công ty đang chạy thế nào so với kỳ trước, có gì cần biết ngay?"',
    nodes: [
      F.node('e1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Đến giờ đã hẹn',
        sub: 'Mỗi người tự chọn giờ nhận' }),
      F.node('e2', 'branch', F.MID, 1, { icon: 'shield', title: 'Có quyền xem toàn công ty?',
        sub: 'Kiểm lại mỗi lần gửi — mất quyền là ngừng' }),

      F.node('e3', 'step', F.LEFT, 2.3, { icon: 'dollar', title: 'Tài chính 2 kỳ',
        sub: 'Tháng này so cùng kỳ tháng trước' }),
      F.node('e4', 'step', F.MID, 2.3, { icon: 'trend', title: 'Dòng tiền & lợi nhuận',
        sub: 'Thực thu · chi · công nợ' }),
      F.node('e5', 'step', F.RIGHT, 2.3, { icon: 'users', title: 'Top nhân viên bán hàng',
        sub: 'Doanh số từ đầu tháng' }),

      F.node('e6', 'step', F.LEFT, 3.5, { icon: 'zap', title: 'Cơ hội mới hôm qua',
        sub: 'Đếm lead vào trong ngày' }),
      F.node('e7', 'step', F.RIGHT, 3.5, { icon: 'warning', title: 'Cảnh báo còn treo',
        sub: 'Tour sắp đi mà khách chưa trả đủ' }),

      F.node('e8', 'step', F.MID, 4.8, { icon: 'chart', title: 'Tính sẵn các con số',
        sub: 'Máy chủ tính, KHÔNG để AI tự tính' }),
      F.node('e9', 'branch', F.MID, 5.8, { icon: 'sparkle', title: 'AI viết nhận định được không?',
        sub: '1 lượt/công ty · cấm bịa số ngoài bảng' }),
      F.node('e10', 'step', F.LEFT, 7, { icon: 'edit', title: '5–8 câu nhận định',
        sub: 'Nêu xu hướng và việc đáng lưu ý' }),
      F.node('e11', 'step', F.RIGHT, 7, { icon: 'checkCircle', title: 'Bản số liệu thuần',
        sub: 'AI lỗi hoặc hết lượt → bản tin KHÔNG bao giờ mất' }),
      // Hai TẦNG cấu hình (giống bản tin của nhân viên bán hàng):
      //   - Công ty khai MỘT LẦN: OA Zalo (ZNS) + bot Telegram dùng để gửi đi
      //   - Từng người tự khai: địa chỉ nhận của mình
      F.node('e12', 'step', F.LEFT, 8.2, { icon: 'shield', title: 'Kênh gửi của công ty',
        sub: 'OA Zalo (ZNS) · bot Telegram — quản trị khai 1 lần' }),
      F.node('e13', 'step', F.RIGHT, 8.2, { icon: 'user', title: 'Nơi nhận của bạn',
        sub: 'Email · Zalo · Telegram — tự khai, muốn tắt kênh nào cũng được' }),
      F.node('e14', 'send', F.MID, 9.4, { icon: 'send', title: 'Gửi tới giám đốc',
        sub: 'Trong app luôn có · các kênh ngoài tuỳ bật' }),
    ],
    edges: [
      F.edge('e1', 'e2'),
      F.edge('e2', 'e3', 'Có'), F.edge('e2', 'e4'), F.edge('e2', 'e5'),
      F.edge('e3', 'e6'), F.edge('e5', 'e7'),
      F.edge('e4', 'e8'), F.edge('e6', 'e8'), F.edge('e7', 'e8'),
      F.edge('e8', 'e9'),
      F.edge('e9', 'e10', 'Được'), F.edge('e9', 'e11', 'Lỗi / hết lượt'),
      F.edge('e10', 'e12'), F.edge('e11', 'e13'),
      F.edge('e12', 'e14'), F.edge('e13', 'e14'),
    ],
  });
})();
