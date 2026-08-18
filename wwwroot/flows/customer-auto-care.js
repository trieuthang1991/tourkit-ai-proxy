// Sơ đồ: Nhắc chăm lại khách ngủ quên (S6).
// Vẽ theo Services/Workflows/CustomerAutoCareWorkflow.cs + Services/Digest/AutoCareRule.cs
//
// Điều quan trọng nhất trên sơ đồ này là thứ KHÔNG có: không có bước nào gửi gì cho khách.
// Đo dữ liệu thật: số điện thoại có ở 100/100 khách, email chỉ 14/100 — việc đúng với dữ liệu
// đang có là nhắc nhân viên GỌI, và gọi thì phải người gọi.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'customer-auto-care',
    label: 'Nhắc chăm lại khách ngủ quên',
    note: 'Vẽ theo CustomerAutoCareWorkflow.cs. Luật thuần — không gọi AI, và KHÔNG gửi gì cho khách hàng.',
    nodes: [
      F.node('c1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn · thường đặt mỗi ngày', cfg: ['@interval'] }),
      F.node('c2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('c3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Khách của cả công ty nên dùng tài khoản hệ thống' }),
      F.node('c4', 'step', F.MID, 3, { icon: 'users', title: 'Lấy danh sách khách',
        sub: 'Kèm hạng, tổng đã mua và ngày chăm sóc gần nhất' }),

      F.node('c5', 'branch', F.LEFT, 4.2, { icon: 'calendar', title: 'Đã im đủ lâu chưa?',
        sub: 'Chưa từng được chăm lần nào thì BỎ QUA — không phải "im lâu"', cfg: ['quietDays'] }),
      F.node('c6', 'branch', F.RIGHT, 4.2, { icon: 'checkCircle', title: 'Có đáng gọi không?',
        sub: 'Đã từng mua · đúng hạng công ty chọn', cfg: ['requireBought', 'ranks'] }),

      F.node('c7', 'branch', F.MID, 5.4, { icon: 'refresh', title: 'Đã nhắc khách này chưa?',
        sub: 'Khách được chăm sóc THẬT thì bộ đếm về 0 — vòng đời mới, lại nhắc được',
        cfg: ['remindGapDays', 'maxReminders'] }),
      F.node('c8', 'step', F.MID, 6.6, { icon: 'trend', title: 'Xếp khách chi nhiều lên trước',
        sub: 'CẮT SAU khi lọc đã nhắc — cắt trước thì khách thứ 21 không bao giờ đến lượt', cfg: ['maxLeads'] }),
      F.node('c9', 'send', F.MID, 7.8, { icon: 'bell', title: 'Mỗi NHÂN VIÊN một thẻ',
        sub: 'Khách chưa gán người phụ trách thì bỏ qua, không đổ vào thẻ chung' }),
      F.node('c10', 'step', F.MID, 9, { icon: 'phone', title: 'Nhân viên gọi',
        sub: 'Hệ thống KHÔNG gửi gì cho khách — người quyết định gọi ai, nói gì' }),
    ],
    edges: [
      F.edge('c1', 'c2'), F.edge('c2', 'c3', 'Rồi'), F.edge('c3', 'c4'),
      F.edge('c4', 'c5'), F.edge('c4', 'c6'),
      F.edge('c5', 'c7', 'Rồi'), F.edge('c6', 'c7', 'Có'),
      F.edge('c7', 'c8', 'Chưa'), F.edge('c8', 'c9'), F.edge('c9', 'c10'),
    ],
  });
})();
