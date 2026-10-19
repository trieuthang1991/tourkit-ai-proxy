// core/internal-chat-loader.js — Nhúng widget chat nội bộ vào CHÍNH app TRAV-AI.
//
// Widget (/internal-chat.js) viết cho CRM nhúng bằng một thẻ <script data-session="…">. Trong app
// này phiên chỉ có SAU khi đăng nhập (localStorage tourkit_tk_session), nên không gắn cứng được thẻ
// vào index.html: tệp này chèn thẻ khi cờ internalChat bật VÀ đã có phiên, gỡ khi đăng xuất hoặc
// đổi tài khoản (widget giữ phiên cũ trong mình, không gỡ là người sau nhắn dưới tên người trước).
'use strict';

(function () {
  const KEY = 'tourkit_tk_session';
  let loadedFor = null;   // phiên mà widget đang chạy

  const sid = () => { try { return localStorage.getItem(KEY); } catch { return null; } };
  const on = () => !!(window.tourkitFeatures && window.tourkitFeatures.isOn('internalChat'));

  function unload() {
    try { window.TkInternalChat && window.TkInternalChat.destroy(); } catch { /* đã gỡ */ }
    document.querySelectorAll('script[data-tk-internal-chat]').forEach((s) => s.remove());
    loadedFor = null;
  }

  function sync() {
    const s = sid();
    if (loadedFor && (loadedFor !== s || !on())) unload();
    if (!loadedFor && s && on()) {
      const el = document.createElement('script');
      el.src = '/internal-chat.js';
      el.async = true;
      el.setAttribute('data-session', s);
      el.setAttribute('data-tk-internal-chat', '');
      document.body.appendChild(el);
      loadedFor = s;
    }
  }

  // Phòng theo cơ hội có nút "Mở cơ hội": mở đúng cơ hội trên CRM, đăng nhập sẵn đúng tài khoản (SSO) —
  // cùng đường với nút "Xem cơ hội CRM" ở màn Cơ hội.
  window.addEventListener('tk-internal-chat-ready', () => {
    const u = window.tourkitUtil;
    if (!window.TkInternalChat || !u || !u.crmUrl('/')) return;
    window.TkInternalChat.setEntityLinks({ deal: (id) => u.openCrm('/booking-ticket/' + encodeURIComponent(id)) });
  });

  if (!window.tourkitFeatures) return;
  window.tourkitFeatures.ready.then(sync);
  window.addEventListener('tourkit-auth-changed', sync);
  // Đăng nhập / đăng xuất ở tab khác.
  window.addEventListener('storage', (e) => { if (e.key === KEY) sync(); });
})();
