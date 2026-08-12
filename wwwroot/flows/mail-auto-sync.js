// Sơ đồ: Tự động đồng bộ Gmail. Vẽ theo Services/Workflows/MailAutoSyncWorkflow.cs
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'mail-auto-sync',
    label: 'Tự động đồng bộ Gmail',
    note: 'Vẽ theo MailAutoSyncWorkflow.cs. Mỗi lượt kéo tối đa 50 thư; tồn đọng tự trôi dần qua các chu kỳ.',
    nodes: [
      F.node('a1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Theo tần suất bạn chọn', cfg: ['@interval'] }),
      F.node('a2', 'step', F.MID, 1, { icon: 'mail', title: 'Kéo thư mới từ Gmail',
        sub: 'IMAP, tối đa 50 thư/lượt' }),
      F.node('a3', 'step', F.MID, 2, { icon: 'sparkle', title: 'AI phân loại 6 nhóm',
        sub: 'CHỈ thư mới — thư cũ bỏ qua' }),
      F.node('a4', 'branch', F.MID, 3, { icon: 'sliders', title: 'Có bật tự trả lời?',
        sub: 'Và thư thuộc nhóm đã chọn', cfg: ['autoReply', 'replyCategories'] }),
      F.node('a5', 'step', F.LEFT, 4.2, { icon: 'edit', title: 'AI soạn nháp',
        sub: 'Theo tone đã chọn → "đang xử lý"', cfg: ['replyTone'] }),
      F.node('a6', 'branch', F.LEFT, 5.2, { icon: 'sliders', title: 'Chế độ nào?',
        sub: 'Soạn sẵn hay gửi thẳng', cfg: ['replyMode'] }),
      F.node('a7', 'send', F.LEFT, 6.2, { icon: 'send', title: 'Gửi cho khách',
        sub: 'SMTP → "đã phản hồi"' }),
      F.node('a8', 'step', F.RIGHT, 4.2, { icon: 'check', title: 'Dừng ở đây',
        sub: 'Chỉ phân loại, không trả lời' }),
    ],
    edges: [
      F.edge('a1', 'a2'), F.edge('a2', 'a3'), F.edge('a3', 'a4'),
      F.edge('a4', 'a5', 'Có'), F.edge('a4', 'a8', 'Không'),
      F.edge('a5', 'a6'), F.edge('a6', 'a7', 'Gửi thẳng'),
    ],
  });
})();
