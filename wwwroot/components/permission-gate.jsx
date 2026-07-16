// components/permission-gate.jsx — hộp "Không có quyền" cho trang yêu cầu 1 permission cụ thể.
//   window.NoPermissionBox  — màn báo thiếu quyền
(function () {
  'use strict';

  function NoPermissionBox({ feature }) {
    return (
      <main className="page" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
        <div style={{ maxWidth: 440, textAlign: 'center', padding: 36, borderRadius: 16,
          border: '1px solid var(--border)', background: 'var(--surface)' }}>
          <div style={{ fontSize: 46, marginBottom: 14 }}>🔒</div>
          <h2 style={{ margin: '0 0 10px' }}>Không có quyền truy cập</h2>
          <p style={{ color: 'var(--text-2)', lineHeight: 1.65, margin: 0 }}>
            Tính năng <b>{feature}</b> yêu cầu quyền <b>Cấu hình hệ thống</b>.
            Vui lòng liên hệ quản trị viên để được cấp quyền.
          </p>
        </div>
      </main>
    );
  }

  window.NoPermissionBox = NoPermissionBox;
})();
