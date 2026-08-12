// Sơ đồ: Tự động xếp hạng khách hàng. Vẽ theo Services/Workflows/CustomerAutoReviewWorkflow.cs
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'customer-auto-review',
    label: 'Tự động xếp hạng khách hàng',
    note: 'Vẽ theo CustomerAutoReviewWorkflow.cs. Hạng A–D lưu lại, bộ đồng bộ riêng đẩy về CRM.',
    nodes: [
      F.node('c1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn', cfg: ['@interval'] }),
      F.node('c2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('c3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Không cần ai online' }),
      F.node('c4', 'step', F.LEFT, 3.2, { icon: 'users', title: 'Lượt 1 — khách chưa xếp hạng',
        sub: 'Trong số ngày bạn đặt', cfg: ['createdWithinDays'] }),
      F.node('c5', 'step', F.RIGHT, 3.2, { icon: 'refresh', title: 'Lượt 2 — khách đến hạn xem lại',
        sub: 'Quá số ngày kể từ lần trước', cfg: ['reReview', 'reReviewDays'] }),
      F.node('c6', 'step', F.MID, 4.4, { icon: 'sparkle', title: 'AI chấm hạng A–D',
        sub: 'Kèm điểm mạnh, việc nên làm' }),
      F.node('c7', 'send', F.MID, 5.4, { icon: 'save', title: 'Lưu kết quả',
        sub: 'Bộ đồng bộ đẩy hạng về CRM' }),
    ],
    edges: [
      F.edge('c1', 'c2'), F.edge('c2', 'c3', 'Có'),
      F.edge('c3', 'c4'), F.edge('c3', 'c5'),
      F.edge('c4', 'c6'), F.edge('c5', 'c6'), F.edge('c6', 'c7'),
    ],
  });
})();
