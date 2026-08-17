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
      F.node('p4', 'step', F.MID, 3, { icon: 'calendar', title: 'Lấy tour sắp khởi hành',
        sub: 'Mỗi loại tour một lượt đọc riêng · không chọn loại thì chỉ thấy FIT',
        cfg: ['scanTourTypes', 'windowDays'] }),

      F.node('p5', 'branch', F.LEFT, 4.2, { icon: 'sliders', title: 'Có số thực thu chưa?',
        sub: 'Thiếu thì BỎ QUA — thà không báo còn hơn báo sai tiền' }),
      F.node('p6', 'branch', F.RIGHT, 4.2, { icon: 'dollar', title: 'Còn thiếu tiền không?',
        sub: 'Theo bộ lọc "Chưa thu hết" của phần mềm · nợ nhỏ hơn ngưỡng thì bỏ qua',
        cfg: ['paymentStatus', 'minOutstanding'] }),

      F.node('p7', 'step', F.MID, 5.4, { icon: 'warning', title: 'Chia mức gấp',
        sub: 'Còn ≤3 ngày là gấp, còn lại là nhắc' }),
      F.node('p8', 'branch', F.MID, 6.4, { icon: 'refresh', title: 'Còn được nhắc nữa không?',
        sub: 'Mỗi tour 1 lần/ngày, và dừng hẳn khi đủ số lần bạn đặt',
        cfg: ['maxReminders'] }),
      F.node('p9', 'send', F.LEFT, 7.4, { icon: 'paper', title: 'Ghi vào Bảng tin',
        sub: 'Cả công ty cùng thấy · luôn có, không tắt được' }),
      F.node('p10', 'send', F.RIGHT, 7.4, { icon: 'mail', title: 'Xếp MỘT thư gộp',
        sub: 'Gửi cho ai đã khai "Nơi nhận của tôi" · proxy chỉ xếp hàng đợi, worker gửi',
        cfg: ['emailEnabled', 'alertEmails'] }),
    ],
    edges: [
      F.edge('p1', 'p2'), F.edge('p2', 'p3', 'Có'), F.edge('p3', 'p4'),
      F.edge('p4', 'p5'), F.edge('p5', 'p6', 'Có'),
      F.edge('p6', 'p7', 'Còn thiếu'),
      F.edge('p7', 'p8'), F.edge('p8', 'p9', 'Còn'), F.edge('p8', 'p10', 'Nếu bật email'),
    ],
  });
})();
