// wwwroot/flows/_registry.js — SỔ ĐĂNG KÝ sơ đồ luồng.
//
// Mỗi tính năng có sơ đồ riêng → mỗi sơ đồ MỘT FILE trong thư mục này, tự đăng ký vào đây.
// Trang /flow-preview chỉ còn việc VẼ, không giữ dữ liệu — thêm sơ đồ mới không phải
// đụng vào trang đó nữa.
//
// Thêm 1 sơ đồ mới:
//   1. Tạo wwwroot/flows/<ma-workflow>.js theo mẫu bên dưới
//   2. Khai vào index.html  (thẻ <script>, ĐẶT SAU _registry.js)
//   3. Khai vào bundle-entry.js (import, ĐẶT SAU _registry.js)
//   → Bước 2 và 3 rất dễ quên. scripts/e2e/features-flow-diagram.check.js kiểm cả hai,
//     thiếu chỗ nào là báo lỗi ngay.
//
// Mẫu:
//   window.tourkitFlows.register({
//     type:  'ma-workflow',                 // PHẢI khớp IScheduledWorkflow.Type bên C#
//     label: 'Tên hiển thị',
//     note:  'Vẽ theo XxxWorkflow.cs. ...', // ghi rõ vẽ theo file nào để sau còn đối chiếu
//     nodes: [ F.node('n1', 'trigger', F.MID, 0, { ... }) ],
//     edges: [ F.edge('n1', 'n2'), F.edge('n2', 'n3', 'Có') ],
//   });

(function () {
  'use strict';

  const flows = {};

  // Lưới đặt node: 3 cột × các hàng cách đều. Đặt tay toạ độ dễ lệch, đặt qua lưới thì
  // mọi sơ đồ nhìn cùng một nhịp và đổi khoảng cách chỉ sửa 1 chỗ.
  const COL = { left: 10, mid: 250, right: 490 };
  const ROW_H = 108;

  const F = {
    LEFT: 'left', MID: 'mid', RIGHT: 'right',

    /// 1 node. kind: trigger | step | branch | send
    /// data: { icon, title, sub, cfg? } — cfg là danh sách khoá cấu hình sửa được tại node này
    /// ('@interval' = tần suất chạy; còn lại phải có trong WORKFLOW_OPTIONS của workflow đó).
    node(id, kind, col, row, data) {
      return {
        id,
        type: 'fp' + kind.charAt(0).toUpperCase() + kind.slice(1),
        position: { x: COL[col], y: Math.round(row * ROW_H) },
        data,
      };
    },

    /// 1 cạnh. label để ghi nhánh ("Có" / "Không" / "Gửi thẳng"...).
    edge(source, target, label) {
      const e = { id: 'e_' + source + '_' + target, source, target };
      if (label) e.label = label;
      return e;
    },
  };

  window.tourkitFlows = {
    F,
    register(def) {
      if (!def || !def.type) { console.warn('[flows] register thiếu type', def); return; }
      if (flows[def.type]) console.warn('[flows] ghi đè sơ đồ đã đăng ký:', def.type);
      flows[def.type] = def;
    },
    get(type) { return (type && flows[type]) || null; },
    all() { return Object.values(flows); },
    types() { return Object.keys(flows); },
  };
})();
