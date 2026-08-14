// Sơ đồ: Bản tin sáng cho NHÂN VIÊN BÁN HÀNG (sale-brief).
// Vẽ theo Services/Workflows/SaleBriefWorkflow.cs + Services/Digest/SaleBriefBuilder.cs
//
// Hai điểm đáng nhớ nhất của bản tin này:
//   1. RULE THUẦN, KHÔNG gọi AI → không tốn lượt.
//   2. Lấy dữ liệu bằng TÀI KHOẢN CỦA CHÍNH NGƯỜI NHẬN, không dùng tài khoản hệ thống —
//      CRM tự cắt phần không thuộc họ, nên lọc sai cũng chỉ thiếu chứ không lộ việc người khác.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'sale-brief',
    label: 'Bản tin sáng cho nhân viên bán hàng',
    note: 'Vẽ theo SaleBriefWorkflow.cs. Trả lời đúng một câu: "sáng nay tôi gọi ai trước?". Tác vụ chạy mỗi N phút rồi tự chọn ai đến giờ — giờ nhận của từng người khai ở trang Bản tin AI.',
    nodes: [
      F.node('s1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Trước giờ nhận một chút, chuẩn bị bản tin',
        sub: 'Chuẩn bị sớm rồi hẹn giờ gửi — đến giờ khỏi phụ thuộc lúc đó máy có rảnh không',
        cfg: ['@interval'] }),
      F.node('s2', 'branch', F.MID, 1, { icon: 'user', title: 'Người này từng đăng nhập chưa?',
        sub: 'Chưa từng đăng nhập thì bỏ qua và ghi rõ lý do — không làm hỏng cả lượt' }),
      F.node('s3', 'step', F.MID, 2, { icon: 'shield', title: 'Dùng TÀI KHOẢN CỦA HỌ',
        sub: 'CRM tự chặn phần không thuộc họ · hết hạn thì tự đăng nhập lại, không bắt làm gì' }),

      F.node('s4', 'step', F.LEFT, 3.2, { icon: 'phone', title: 'Cơ hội cần gọi hôm nay',
        sub: 'Im lặng quá ngưỡng · kèm % khả năng chốt · bỏ đơn đã đóng',
        cfg: ['secCooling', 'callStatuses', 'silentDaysMin'] }),
      F.node('s5', 'step', F.MID, 3.2, { icon: 'calendar', title: 'Lịch hẹn hôm nay + quá hạn',
        sub: 'Giờ và tên khách', cfg: ['secAppointments'] }),
      F.node('s6', 'step', F.RIGHT, 3.2, { icon: 'checkCircle', title: 'Việc cần làm hôm nay',
        sub: 'Việc trễ hạn chèn lên đầu', cfg: ['secTasks'] }),

      F.node('s7', 'step', F.LEFT, 4.4, { icon: 'dollar', title: 'Tour của mình còn thiếu tiền',
        sub: 'Sắp khởi hành mà khách chưa trả đủ', cfg: ['secPayments'] }),
      F.node('s8', 'step', F.MID, 4.4, { icon: 'star', title: 'Khách hạng A/B bỏ lâu',
        sub: 'Chưa mua lại quá 60 ngày', cfg: ['secVips'] }),
      F.node('s9', 'step', F.RIGHT, 4.4, { icon: 'paper', title: 'Báo giá chưa ai động',
        sub: 'Quá ngưỡng ngày không cập nhật', cfg: ['secQuotes', 'staleQuoteDays'] }),

      F.node('s10', 'step', F.LEFT, 5.6, { icon: 'mail', title: 'Hộp thư công ty',
        sub: 'Còn bao nhiêu thư chưa xử lý · lỗi thì bỏ mục này, không bỏ bản tin',
        cfg: ['secMailbox'] }),
      F.node('s11', 'step', F.RIGHT, 5.6, { icon: 'warning', title: 'Cơ hội bị bỏ dở',
        sub: 'Kẹt một trạng thái quá ngưỡng ngày', cfg: ['secHygiene', 'hygieneStuckDays'] }),

      F.node('s12', 'branch', F.MID, 6.8, { icon: 'sliders', title: 'Có việc gấp không?',
        sub: 'Gộp các mục trên lại' }),
      F.node('s13', 'step', F.LEFT, 8, { icon: 'edit', title: 'AI xếp theo mức ưu tiên',
        sub: 'Chọn ra việc đáng làm nhất · AI lỗi thì in đủ bản rule',
        cfg: ['useAi', 'maxItems'] }),
      F.node('s14', 'step', F.RIGHT, 8, { icon: 'checkCircle', title: 'Bản tin ngắn',
        sub: '"Hôm nay chưa có việc gấp" — vẫn gửi để giữ thói quen mở bản tin' }),

      // Hai TẦNG cấu hình khác nhau, cố ý tách 2 node để không lẫn:
      //   - Công ty khai MỘT LẦN: CHỈ OA Zalo (tốn tiền, hạn mức theo từng OA)
      //   - Từng người tự khai: nơi nhận của mình
      F.node('s15', 'step', F.LEFT, 9.2, { icon: 'shield', title: 'OA Zalo của công ty',
        sub: 'Tốn tiền + hạn mức riêng nên công ty tự khai · Telegram và email hệ thống lo' }),
      F.node('s16', 'step', F.RIGHT, 9.2, { icon: 'user', title: 'Nơi nhận của bạn',
        sub: 'Email · Zalo · Telegram — bạn tự khai, muốn tắt kênh nào cũng được' }),

      F.node('s17', 'step', F.MID, 10.4, { icon: 'bell', title: 'Lưu vào Bảng tin (luôn luôn)',
        sub: 'Kho lưu để xem/nghe lại · mọi kênh ngoài hỏng hết thì vẫn còn chỗ này' }),
      F.node('s18', 'send', F.MID, 11.6, { icon: 'send', title: 'Xếp hàng đợi — đến giờ mới gửi',
        sub: 'Mỗi kênh một dòng riêng · máy bận hay khởi động lại thì gửi bù, không mất bản tin' }),
    ],
    edges: [
      F.edge('s1', 's2'), F.edge('s2', 's3', 'Rồi'),
      F.edge('s3', 's4'), F.edge('s3', 's5'), F.edge('s3', 's6'),
      F.edge('s4', 's7'), F.edge('s5', 's8'), F.edge('s6', 's9'),
      F.edge('s7', 's10'), F.edge('s9', 's11'),
      F.edge('s8', 's12'), F.edge('s10', 's12'), F.edge('s11', 's12'),
      F.edge('s12', 's13', 'Có'), F.edge('s12', 's14', 'Không'),
      F.edge('s13', 's15'), F.edge('s14', 's16'),
      F.edge('s15', 's17'), F.edge('s16', 's17'), F.edge('s17', 's18'),
    ],
  });
})();
