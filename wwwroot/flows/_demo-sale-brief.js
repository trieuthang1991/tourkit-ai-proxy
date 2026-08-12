// Sơ đồ THIẾT KẾ: Bản tin sáng cho NHÂN VIÊN BÁN HÀNG (sale-brief).
//
// CHƯA có workflow tương ứng ở backend → demo:true, mã có tiền tố '_'.
// Vẽ theo docs/superpowers/specs/2026-08-11-dot1-digest-insight-design.md §5.1
// (SaleBriefBuilder). Khi Đợt 1 code xong: đổi type thành 'sale-brief', bỏ demo:true,
// đổi tên file — bộ E2E sẽ tự bắt nếu quên (demo:true mà backend có workflow trùng mã).
//
// Điểm đáng nhớ của bản tin này: RULE THUẦN, KHÔNG gọi AI → không tốn lượt.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: '_demo-sale-brief',
    demo: true,
    label: 'Bản tin sáng — Nhân viên bán hàng',
    note: 'Thiết kế theo spec Đợt 1 §5.1. Trả lời đúng một câu: "sáng nay tôi gọi ai trước?"',
    nodes: [
      F.node('s1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Đến giờ đã hẹn',
        sub: 'Mỗi người tự chọn giờ nhận' }),
      F.node('s2', 'step', F.MID, 1, { icon: 'user', title: 'Đăng nhập bằng TÀI KHOẢN BẠN',
        sub: 'CRM tự chặn phần không thuộc bạn — không dùng tài khoản chung' }),

      F.node('s3', 'step', F.LEFT, 2.3, { icon: 'phone', title: 'Cơ hội cần gọi hôm nay',
        sub: 'Top 5 · kèm % chốt và số ngày im lặng' }),
      F.node('s4', 'step', F.MID, 2.3, { icon: 'calendar', title: 'Lịch hẹn hôm nay',
        sub: 'Giờ + tên khách' }),
      F.node('s5', 'step', F.RIGHT, 2.3, { icon: 'star', title: 'Khách hạng A/B bỏ lâu',
        sub: 'Chưa mua lại quá 60 ngày' }),

      F.node('s6', 'step', F.LEFT, 3.5, { icon: 'paper', title: 'Báo giá chưa ai động',
        sub: 'Gửi đi hơn 5 ngày, khách chưa phản hồi' }),
      F.node('s7', 'step', F.MID, 3.5, { icon: 'mail', title: 'Hộp thư công ty',
        sub: 'Còn bao nhiêu thư chưa xử lý' }),
      F.node('s8', 'step', F.RIGHT, 3.5, { icon: 'warning', title: 'Cơ hội bị bỏ dở',
        sub: 'Thiếu ngày hẹn tiếp / kẹt một trạng thái' }),

      F.node('s9', 'branch', F.MID, 4.8, { icon: 'sliders', title: 'Có việc gấp không?',
        sub: 'Gộp 6 mục trên lại' }),
      F.node('s10', 'step', F.LEFT, 6, { icon: 'edit', title: 'Xếp theo mức ưu tiên',
        sub: 'Việc gấp lên đầu' }),
      F.node('s11', 'step', F.RIGHT, 6, { icon: 'checkCircle', title: 'Bản tin ngắn',
        sub: '"Hôm nay chưa có việc gấp" — vẫn gửi để giữ thói quen' }),
      // Hai TẦNG cấu hình khác nhau, cố ý tách thành 2 node để không lẫn:
      //   - Công ty khai MỘT LẦN: CHỈ OA Zalo (tốn tiền, hạn mức theo từng OA)
      //   - Từng người tự khai: địa chỉ nhận của mình
      F.node('s12', 'step', F.LEFT, 7.2, { icon: 'shield', title: 'OA Zalo của công ty',
        sub: 'Tốn tiền + hạn mức riêng nên công ty tự khai · Telegram và email hệ thống lo' }),
      F.node('s13', 'step', F.RIGHT, 7.2, { icon: 'user', title: 'Nơi nhận của bạn',
        sub: 'Email · Zalo · Telegram — bạn tự khai, muốn tắt kênh nào cũng được' }),
      F.node('s14', 'send', F.MID, 8.4, { icon: 'send', title: 'Gửi tới bạn',
        sub: 'Trong app luôn có · các kênh ngoài tuỳ bạn bật' }),
    ],
    edges: [
      F.edge('s1', 's2'),
      F.edge('s2', 's3'), F.edge('s2', 's4'), F.edge('s2', 's5'),
      F.edge('s3', 's6'), F.edge('s4', 's7'), F.edge('s5', 's8'),
      F.edge('s6', 's9'), F.edge('s7', 's9'), F.edge('s8', 's9'),
      F.edge('s9', 's10', 'Có'), F.edge('s9', 's11', 'Không'),
      F.edge('s10', 's12'), F.edge('s11', 's13'),
      F.edge('s12', 's14'), F.edge('s13', 's14'),
    ],
  });
})();
