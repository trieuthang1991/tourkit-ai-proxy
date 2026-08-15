// Sơ đồ: Canh doanh thu bất thường (C2).
// Vẽ theo Services/Workflows/AnomalyWatchdogWorkflow.cs + Services/Digest/AnomalyRule.cs
//
// Hai điều thể hiện ngay trên sơ đồ, vì đó là chỗ hay bị hiểu nhầm nhất:
//   1. KHÔNG có bảng lịch sử nào — mức "bình thường" hỏi thẳng CRM mỗi lần chạy.
//   2. Ba nhánh IM LẶNG có chủ ý. Cảnh báo tuần nào cũng có thì không còn là cảnh báo.
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'anomaly-watchdog',
    label: 'Canh doanh thu bất thường',
    note: 'Vẽ theo AnomalyWatchdogWorkflow.cs. Luật thuần — không gọi AI, không tốn lượt.',
    nodes: [
      F.node('a1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn · thường đặt mỗi ngày', cfg: ['@interval'] }),
      F.node('a2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('a3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Số của cả công ty nên dùng tài khoản hệ thống' }),

      F.node('a4', 'step', F.LEFT, 3.2, { icon: 'calendar', title: 'Doanh thu tuần vừa rồi',
        sub: 'Tuần ĐÃ TRỌN VẸN — lấy tuần đang chạy dở thì thứ Hai nào cũng thấy "sụt giảm"' }),
      F.node('a5', 'step', F.RIGHT, 3.2, { icon: 'chart', title: 'Doanh thu mấy tuần trước',
        sub: 'Hỏi thẳng CRM từng tuần — KHÔNG nuôi bảng lịch sử nào', cfg: ['baselineWeeks'] }),

      F.node('a6', 'step', F.MID, 4.4, { icon: 'trend', title: 'Mức thường = TRUNG VỊ',
        sub: 'Không dùng trung bình: một tuần có hợp đồng lớn sẽ kéo nền lên, làm mọi tuần sau trông như sụt' }),

      F.node('a7', 'branch', F.MID, 5.6, { icon: 'checkCircle', title: 'Có đáng báo không?',
        sub: 'Im nếu: thiếu tuần nền · nền bằng 0 · lệch trong ngưỡng',
        cfg: ['thresholdPercent', 'alertOnIncrease'] }),

      F.node('a8', 'step', F.MID, 6.8, { icon: 'warning', title: 'Dựng cảnh báo',
        sub: 'Giảm sâu = gấp · tăng vọt chỉ là tin vui, không tô đỏ' }),
      F.node('a9', 'send', F.MID, 8, { icon: 'bell', title: 'Ghi vào Bảng tin',
        sub: 'Khoá theo TUẦN — chạy lại trong cùng tuần không nhắc lại' }),
    ],
    edges: [
      F.edge('a1', 'a2'), F.edge('a2', 'a3', 'Rồi'),
      F.edge('a3', 'a4'), F.edge('a3', 'a5'),
      F.edge('a4', 'a6'), F.edge('a5', 'a6'),
      F.edge('a6', 'a7'), F.edge('a7', 'a8', 'Có'), F.edge('a8', 'a9'),
    ],
  });
})();
