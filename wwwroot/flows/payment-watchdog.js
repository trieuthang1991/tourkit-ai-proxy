// Sơ đồ: Canh thanh toán trước khởi hành.
// Vẽ theo Services/Workflows/PaymentWatchdogWorkflow.cs + Services/Digest/PaymentWatchdogRule.cs
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'payment-watchdog',
    label: 'Canh thanh toán trước khởi hành',
    note: 'Vẽ theo PaymentWatchdogWorkflow.cs. Không gọi AI nên không tốn lượt. Mỗi tour chỉ nhắc 1 lần/ngày dù tác vụ chạy mỗi giờ.',
    nodes: [
      F.node('p1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn', cfg: ['@interval'] }),
      F.node('p2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('p3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Việc của cả công ty nên dùng tài khoản hệ thống' }),
      F.node('p4', 'step', F.MID, 3, { icon: 'calendar', title: 'Lấy tour khởi hành 7 ngày tới',
        sub: 'Tính theo lịch Việt Nam' }),

      F.node('p5', 'branch', F.LEFT, 4.2, { icon: 'sliders', title: 'Có số thực thu chưa?',
        sub: 'Thiếu thì BỎ QUA — thà không báo còn hơn báo sai tiền' }),
      F.node('p6', 'branch', F.RIGHT, 4.2, { icon: 'dollar', title: 'Còn thiếu tiền không?',
        sub: 'Chưa chốt giá hoặc trả đủ → không nhắc' }),

      F.node('p7', 'step', F.MID, 5.4, { icon: 'warning', title: 'Chia mức gấp',
        sub: 'Còn ≤3 ngày là gấp, còn lại là nhắc' }),
      F.node('p8', 'branch', F.MID, 6.4, { icon: 'refresh', title: 'Hôm nay đã nhắc chưa?',
        sub: 'Mỗi tour 1 lần/ngày dù chạy mỗi giờ' }),
      F.node('p9', 'send', F.MID, 7.4, { icon: 'paper', title: 'Ghi vào Bảng tin',
        sub: 'Cả công ty cùng thấy' }),
    ],
    edges: [
      F.edge('p1', 'p2'), F.edge('p2', 'p3', 'Có'), F.edge('p3', 'p4'),
      F.edge('p4', 'p5'), F.edge('p5', 'p6', 'Có'),
      F.edge('p6', 'p7', 'Còn thiếu'),
      F.edge('p7', 'p8'), F.edge('p8', 'p9', 'Chưa'),
    ],
  });
})();
