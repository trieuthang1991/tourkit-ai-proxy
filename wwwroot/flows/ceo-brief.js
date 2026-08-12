// Sơ đồ: Bản tin điều hành cho GIÁM ĐỐC (ceo-brief).
// Vẽ theo Services/Workflows/CeoBriefWorkflow.cs + Services/Digest/CeoBriefBuilder.cs
//
// Khác bản tin của nhân viên bán hàng ở 3 điểm, thể hiện ngay trên sơ đồ:
//   1. Nhìn TOÀN CÔNG TY và so 2 kỳ để thấy xu hướng, không chỉ số hôm nay.
//      Phạm vi số do CRM tự cắt theo quyền của tài khoản — proxy không tự gác.
//   2. CÓ gọi AI để viết nhận định, nhưng AI chỉ VIẾT LỜI: số do máy chủ tính sẵn.
//   3. AI lỗi hoặc hết lượt thì rơi về bảng số — bản tin không bao giờ mất.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'ceo-brief',
    label: 'Bản tin điều hành (giám đốc)',
    note: 'Vẽ theo CeoBriefWorkflow.cs. Tốn khoảng 1 lượt AI mỗi lần gửi. Số do máy chủ tính, AI chỉ viết lời — và AI lỗi thì vẫn có bảng số.',
    nodes: [
      F.node('e1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút, tự chọn ai đến giờ',
        sub: 'Giờ nhận của từng người khai ở trang Bản tin AI', cfg: ['@interval'] }),
      F.node('e2', 'branch', F.MID, 1, { icon: 'user', title: 'Người này từng đăng nhập chưa?',
        sub: 'Chưa từng đăng nhập thì bỏ qua và ghi rõ lý do' }),
      F.node('e3', 'step', F.MID, 2, { icon: 'shield', title: 'Dùng TÀI KHOẢN CỦA HỌ',
        sub: 'CRM tự cắt phạm vi theo quyền — người quyền hẹp thấy đúng phần của mình' }),

      F.node('e4', 'step', F.LEFT, 3.2, { icon: 'dollar', title: 'Tài chính 2 kỳ',
        sub: 'Đầu tháng tới nay so CÙNG KHOẢNG NGÀY tháng trước' }),
      F.node('e5', 'step', F.MID, 3.2, { icon: 'calendar', title: 'Lịch hẹn hôm nay',
        sub: 'Kèm số tồn đọng quá hạn tích luỹ' }),
      F.node('e6', 'step', F.RIGHT, 3.2, { icon: 'users', title: 'Top nhân viên bán hàng',
        sub: 'Doanh số từ đầu tháng' }),

      F.node('e7', 'step', F.LEFT, 4.4, { icon: 'zap', title: 'Cơ hội mới hôm qua',
        sub: 'Đếm theo ngày tạo' }),
      F.node('e8', 'step', F.RIGHT, 4.4, { icon: 'warning', title: 'Cảnh báo còn treo',
        sub: 'Tour sắp đi mà khách chưa trả đủ' }),

      F.node('e9', 'step', F.MID, 5.6, { icon: 'chart', title: 'Máy chủ tính sẵn mọi con số',
        sub: 'KHÔNG để AI tự tính — sai số trong báo cáo tai hại hơn diễn đạt kém' }),
      F.node('e10', 'branch', F.MID, 6.6, { icon: 'sparkle', title: 'AI viết nhận định được không?',
        sub: 'Ai thấy CÙNG bộ số thì dùng chung 1 bản → thường 1 lượt/công ty' }),
      F.node('e11', 'step', F.LEFT, 7.8, { icon: 'edit', title: '5–8 câu nhận định',
        sub: 'Cấm bịa số ngoài bảng · tồn đọng không được gọi là việc khẩn hôm nay' }),
      F.node('e12', 'step', F.RIGHT, 7.8, { icon: 'checkCircle', title: 'Bảng số thuần',
        sub: 'AI lỗi hoặc hết lượt → bản tin KHÔNG bao giờ mất' }),

      // Hai TẦNG cấu hình (giống bản tin của nhân viên bán hàng)
      F.node('e13', 'step', F.LEFT, 9, { icon: 'shield', title: 'OA Zalo của công ty',
        sub: 'Tốn tiền + hạn mức riêng nên công ty tự khai · Telegram và email hệ thống lo' }),
      F.node('e14', 'step', F.RIGHT, 9, { icon: 'user', title: 'Nơi nhận của bạn',
        sub: 'Email · Zalo · Telegram — tự khai, muốn tắt kênh nào cũng được' }),

      F.node('e15', 'send', F.MID, 10.2, { icon: 'send', title: 'Gửi từng kênh, ghi lại kênh nào xong',
        sub: 'Bảng số luôn đính kèm dưới bài viết để đối chiếu ngay' }),
    ],
    edges: [
      F.edge('e1', 'e2'), F.edge('e2', 'e3', 'Rồi'),
      F.edge('e3', 'e4'), F.edge('e3', 'e5'), F.edge('e3', 'e6'),
      F.edge('e4', 'e7'), F.edge('e6', 'e8'),
      F.edge('e5', 'e9'), F.edge('e7', 'e9'), F.edge('e8', 'e9'),
      F.edge('e9', 'e10'),
      F.edge('e10', 'e11', 'Được'), F.edge('e10', 'e12', 'Lỗi / hết lượt'),
      F.edge('e11', 'e13'), F.edge('e12', 'e14'),
      F.edge('e13', 'e15'), F.edge('e14', 'e15'),
    ],
  });
})();
