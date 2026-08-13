// core/features.js — Cờ tính năng ĐANG MỞ, đọc 1 lần từ GET /api/v1/features.
//
//   window.tourkitFeatures.isOn('digest')   → true/false (đồng bộ, đọc cache)
//   window.tourkitFeatures.ready            → Promise, resolve sau khi hỏi server xong
//   window.tourkitFeatures.useFeature(name) → React hook, tự vẽ lại khi biết kết quả
//
// KHÁC phân quyền (window.tourkitAuth.hasPermission): quyền trả lời "người này được xem gì",
// cờ ở đây trả lời "tính năng đã ra mắt chưa" — tắt là tắt cho tất cả, kể cả admin.
//
// File riêng (không nhét vào app.jsx) vì có HAI entry HTML dùng: index.html và admin-trav-ai.html.
'use strict';

(function () {
  // Mặc định TẮT, và hỏi lỗi cũng giữ TẮT. Cùng nguyên tắc với backend: thà thiếu cái chuông
  // vài trăm ms lúc mạng chập chờn, còn hơn hé tính năng chưa ra mắt ra bản public.
  let flags = {};
  const listeners = new Set();

  const ready = fetch('/api/v1/features')
    .then(r => (r.ok ? r.json() : {}))
    .then(d => { flags = d || {}; return flags; })
    .catch(() => flags)
    .then(f => { listeners.forEach(fn => { try { fn(); } catch {} }); return f; });

  const isOn = (name) => flags[name] === true;

  // Hook: trả false ở lần vẽ đầu (chưa biết), rồi true nếu server nói đang bật.
  function useFeature(name) {
    const [on, setOn] = React.useState(() => isOn(name));
    React.useEffect(() => {
      let alive = true;
      const sync = () => { if (alive) setOn(isOn(name)); };
      listeners.add(sync);
      ready.then(sync);
      return () => { alive = false; listeners.delete(sync); };
    }, [name]);
    return on;
  }

  window.tourkitFeatures = { isOn, ready, useFeature };
})();
