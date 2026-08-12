// Sơ đồ: Tự động review & cảnh báo deal. Vẽ theo Services/Workflows/DealAutoReviewWorkflow.cs
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'deal-auto-review',
    label: 'Tự động review & cảnh báo deal',
    note: 'Vẽ theo DealAutoReviewWorkflow.cs. Hết 5 phút hoặc hết lượt AI thì dừng êm, giữ phần đã làm, chu kỳ sau chạy tiếp.',
    nodes: [
      F.node('b1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn', cfg: ['@interval'] }),
      F.node('b2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('b3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Không cần ai online' }),
      F.node('b4', 'step', F.LEFT, 3.2, { icon: 'star', title: 'Lượt 1 — chấm deal mới',
        sub: 'Bỏ deal đã chốt / đã huỷ',
        cfg: ['statuses', 'createdWithinDays', 'autoReview', 'reviewMax'] }),
      F.node('b5', 'step', F.LEFT, 4.2, { icon: 'refresh', title: 'Lượt 2 — chấm lại deal cũ',
        sub: 'Chỉ khi nội dung đã đổi',
        cfg: ['reReview', 'reReviewDays', 'maxAutoReviews'] }),
      F.node('b6', 'step', F.LEFT, 5.2, { icon: 'check', title: 'Chốt sổ deal hết hạn',
        sub: 'Đổi trạng thái / quá cũ → ngừng chấm' }),
      F.node('b7', 'branch', F.RIGHT, 3.2, { icon: 'warning', title: 'Deal nào đang nguội?',
        sub: 'Quá số ngày bạn đặt',
        cfg: ['alertCooling', 'coolingStatuses', 'coolingDays'] }),
      F.node('b8', 'step', F.RIGHT, 4.2, { icon: 'sliders', title: 'Lọc trước khi báo',
        sub: 'Có người phụ trách · tỉ lệ thắng · không báo dồn',
        cfg: ['minWinRateToNotify', 'maxNotifications', 'notifyMinGapHours'] }),
      F.node('b9', 'send', F.RIGHT, 5.2, { icon: 'mail', title: 'Xếp mail vào hàng đợi',
        sub: 'Bộ phận gửi riêng lo gửi đi' }),
    ],
    edges: [
      F.edge('b1', 'b2'), F.edge('b2', 'b3', 'Có'),
      F.edge('b3', 'b4'), F.edge('b3', 'b7'),
      F.edge('b4', 'b5'), F.edge('b5', 'b6'),
      F.edge('b7', 'b8', 'Có'), F.edge('b8', 'b9'),
    ],
  });
})();
