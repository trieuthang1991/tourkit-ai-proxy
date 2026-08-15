// Sơ đồ: Kiểm tra sẵn sàng khởi hành (O1).
// Vẽ theo Services/Workflows/TourReadinessWorkflow.cs + Services/Digest/TourReadinessRule.cs
//
// Khác "Canh thanh toán" ở 2 điểm, thể hiện ngay trên sơ đồ:
//   1. Gom NHIỀU điều kiện vào MỘT thẻ (tiền + khách + visa), không chỉ hỏi mỗi chuyện tiền.
//   2. Chỉ nói đúng 3 lần theo mốc D-7 / D-3 / D-1, không nhắc mỗi ngày.
//   3. Phần CHỖ NGỒI có mốc RIÊNG, xa hơn (D-21/14/7): bán nốt chỗ cuối hay quyết định dồn chuyến
//      mà tới sát ngày mới nói thì đã hết đường xoay.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'tour-readiness',
    label: 'Kiểm tra sẵn sàng khởi hành',
    note: 'Vẽ theo TourReadinessWorkflow.cs. Luật thuần — không gọi AI, không tốn lượt. Mỗi tour nhắc một lần cho mỗi mốc.',
    nodes: [
      F.node('r1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn', cfg: ['@interval'] }),
      F.node('r2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('r3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Việc của cả công ty nên dùng tài khoản hệ thống' }),
      F.node('r4', 'step', F.MID, 3, { icon: 'calendar', title: 'Lấy tour sắp khởi hành',
        sub: 'Cửa sổ = mốc xa nhất của CẢ HAI nhóm · tính theo lịch Việt Nam',
        cfg: ['milestones', 'capacityMilestones'] }),

      F.node('r5', 'branch', F.MID, 4.2, { icon: 'sliders', title: 'Tour đã chạm mốc nào?',
        sub: 'Còn 5 ngày thì thuộc mốc 7 — nhắc theo mốc gần nhất ĐÃ đi qua', cfg: ['milestones'] }),

      F.node('r6', 'step', F.LEFT, 5.4, { icon: 'dollar', title: 'Thu đủ tiền chưa?',
        sub: 'Tour chưa chốt giá thì bỏ qua', cfg: ['checkPayment'] }),
      F.node('r7', 'step', F.MID, 5.4, { icon: 'users', title: 'Đủ khách tối thiểu chưa?',
        sub: 'Chưa khai ngưỡng thì KHÔNG kiểm — không đoán hộ', cfg: ['checkSeats', 'minSeats'] }),
      F.node('r8', 'step', F.RIGHT, 5.4, { icon: 'shield', title: 'Tour có cần visa?',
        sub: 'CRM không lưu trạng thái hồ sơ nên chỉ nhắc kiểm lại', cfg: ['checkVisa', 'visaTourTypes'] }),
      F.node('r8b', 'step', F.RIGHT, 6.0, { icon: 'trend', title: 'Sắp đầy chỗ chưa?',
        sub: 'Chỗ đang GIỮ cũng tính là đã chiếm · đầy hẳn thì im',
        cfg: ['checkNearlyFull', 'nearlyFullPercent'] }),

      F.node('r9', 'branch', F.MID, 7.0, { icon: 'checkCircle', title: 'Có gì đáng nói không?',
        sub: 'Không thiếu gì và cũng chưa sắp đầy thì im lặng' }),
      F.node('r10', 'step', F.MID, 8.2, { icon: 'warning', title: 'Gom thành MỘT thẻ',
        sub: 'Vấn đề và cơ hội để hai mục riêng · mức gấp CHỈ tính theo vấn đề' }),
      F.node('r11', 'send', F.MID, 9.4, { icon: 'bell', title: 'Ghi vào Bảng tin',
        sub: 'Khoá chống trùng kèm mốc — D-7 và D-3 là hai lời nhắc khác nhau' }),
    ],
    edges: [
      F.edge('r1', 'r2'), F.edge('r2', 'r3', 'Rồi'),
      F.edge('r3', 'r4'), F.edge('r4', 'r5'),
      F.edge('r5', 'r6'), F.edge('r5', 'r7'), F.edge('r5', 'r8'), F.edge('r5', 'r8b'),
      F.edge('r6', 'r9'), F.edge('r7', 'r9'), F.edge('r8', 'r9'), F.edge('r8b', 'r9'),
      F.edge('r9', 'r10', 'Có'), F.edge('r10', 'r11'),
    ],
  });
})();
