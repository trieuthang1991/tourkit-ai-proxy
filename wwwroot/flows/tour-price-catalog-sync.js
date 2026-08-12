// Sơ đồ: Đồng bộ bảng giá NCC. Vẽ theo Services/TourPrices/TourPriceCatalogSyncWorkflow.cs
(function () {
  const F = window.tourkitFlows.F;
  window.tourkitFlows.register({
    type: 'tour-price-catalog-sync',
    label: 'Đồng bộ bảng giá nhà cung cấp',
    note: 'Vẽ theo TourPriceCatalogSyncWorkflow.cs. Tác vụ này CHỈ ĐỌC từ TourKit, không ghi ngược, và không gọi AI (nên không tốn lượt).',
    nodes: [
      F.node('d1', 'trigger', F.MID, 0, { icon: 'clock', title: 'Mỗi N phút',
        sub: 'Mặc định 1 lần/ngày', cfg: ['@interval'] }),
      F.node('d2', 'branch', F.MID, 1, { icon: 'shield', title: 'Đã có tài khoản tự động?',
        sub: 'Chưa có là dừng ngay' }),
      F.node('d3', 'step', F.MID, 2, { icon: 'user', title: 'Đăng nhập TourKit',
        sub: 'Không cần ai online' }),
      F.node('d4', 'step', F.MID, 3, { icon: 'download', title: 'Kéo bảng giá theo trang',
        sub: 'Lặp tới hết, chặn ở 500 trang' }),
      F.node('d5', 'branch', F.LEFT, 4.2, { icon: 'sliders', title: 'Lọc dòng không dùng được',
        sub: 'Vé máy bay (có tên khách) · giá dưới 50k' }),
      F.node('d6', 'step', F.RIGHT, 4.2, { icon: 'save', title: 'Lưu vào bảng giá',
        sub: 'Có thì cập nhật, chưa có thì thêm' }),
      F.node('d7', 'step', F.MID, 5.4, { icon: 'close', title: 'Tắt dòng không còn thấy',
        sub: 'Giá NCC đã gỡ bên TourKit' }),
    ],
    edges: [
      F.edge('d1', 'd2'), F.edge('d2', 'd3', 'Có'), F.edge('d3', 'd4'),
      F.edge('d4', 'd5'), F.edge('d5', 'd6', 'Giữ lại'), F.edge('d6', 'd7'),
    ],
  });
})();
