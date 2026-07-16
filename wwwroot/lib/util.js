// lib/util.js — tiện ích THUẦN (không React) dùng chung toàn frontend.
// Gom các helper trước đây bị copy-paste ở nhiều page. Expose qua window.tourkitUtil.
// (React hooks dùng chung nằm ở lib/hooks.jsx → window.tourkitHooks)
(function () {

  // ── SSE reader ────────────────────────────────────────────────────────────
  // Đọc Server-Sent Events từ 1 Response ĐÃ fetch sẵn. Gọi onEvent(obj) cho MỖI
  // `data: {json}` parse được (bỏ qua dòng lỗi/rỗng). onEvent trả về false → dừng
  // đọc + cancel reader (dùng cho unmount guard). KHÔNG tự fetch / accumulate /
  // xử lý error — caller giữ phần đó vì mỗi nơi khác nhau (chat inline switch /
  // accumulate full text / capture-then-throw). Đây là phần plumbing trước đây
  // viết lại y hệt ở assistant/home/customers/deals/mail + core/ai-provider.
  async function readSSE(response, onEvent) {
    if (!response || !response.body) throw new Error('SSE: response không có body');
    const reader = response.body.getReader();
    const dec = new TextDecoder('utf-8');
    let buf = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const evt = buf.slice(0, i); buf = buf.slice(i + 2);
        const line = evt.split('\n').find(l => l.startsWith('data:'));
        if (!line) continue;
        const payload = line.slice(5).trim();
        if (!payload) continue;
        let o; try { o = JSON.parse(payload); } catch { continue; }
        if (onEvent(o) === false) { try { reader.cancel(); } catch {} return; }
      }
    }
  }

  // ── Time ago ──────────────────────────────────────────────────────────────
  // "5 phút trước". opts.seconds=true → hiện cả mốc "giây trước". null/invalid →
  // opts.empty (mặc định ''). >30 ngày → ngày tuyệt đối dd/MM/yyyy.
  function fmtAgo(iso, opts = {}) {
    const empty = opts.empty != null ? opts.empty : '';
    if (!iso) return empty;
    const t = new Date(iso).getTime();
    if (Number.isNaN(t)) return empty;
    const sec = (Date.now() - t) / 1000;
    if (opts.seconds && sec < 60) return `${Math.round(sec)} giây trước`;
    const m = Math.floor(sec / 60);
    if (m < 1) return 'vừa xong';
    if (m < 60) return `${m} phút trước`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h} giờ trước`;
    const d = Math.floor(h / 24);
    if (d < 30) return `${d} ngày trước`;
    return new Date(iso).toLocaleDateString('vi-VN');
  }

  // ── Date format ───────────────────────────────────────────────────────────
  // Mặc định dd/MM/yyyy. opts.time=true → kèm giờ phút (dateStyle+timeStyle short).
  function fmtDate(iso, opts = {}) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    if (opts.time) return d.toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'short' });
    return d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit', year: 'numeric' });
  }

  // ── Clipboard ─────────────────────────────────────────────────────────────
  // Copy text → Promise<boolean> (false nếu trình duyệt không hỗ trợ / lỗi).
  // Caller tự pushToast / setCopied tuỳ UI.
  async function copyText(text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch { /* ignore */ }
    return false;
  }

  // ── CRM URL builder ───────────────────────────────────────────────────────
  // Build URL sang CRM web (tourkit) từ tenantId user đang đăng nhập.
  // - tenantId có dấu '.' → treat as full host (vd 'demo2.tourkit.vn')
  // - không có '.' → append '.tourkit.vn' (vd 'demo2' → 'demo2.tourkit.vn')
  // Trả null nếu chưa đăng nhập (không có tenantId). Dùng cho nút "Xem trên CRM".
  function crmUrl(path) {
    try {
      const u = window.tourkitAuth && window.tourkitAuth.getUser && window.tourkitAuth.getUser();
      const t = (u && u.tenantId ? String(u.tenantId) : '').trim();
      if (!t) return null;
      const host = t.includes('.') ? t : (t + '.tourkit.vn');
      const p = String(path || '').startsWith('/') ? path : '/' + (path || '');
      return `https://${host}${p}`;
    } catch { return null; }
  }

  window.tourkitUtil = { readSSE, fmtAgo, fmtDate, copyText, crmUrl };
})();
