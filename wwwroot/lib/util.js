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

  // ── Đọc JSON từ Response — KHÔNG tin là máy chủ có trả JSON ───────────────
  // Hạ tầng chen ngang GIỮA trình duyệt và ứng dụng (Cloudflare 524 quá giờ / 502 app
  // chết / 403 WAF chặn, IIS 411 thiếu Content-Length / 413 file nặng / 502.5) trả về
  // TRANG HTML. Gọi thẳng r.json() khi đó ném "Unexpected token '<', "<!DOCTYPE "…" —
  // câu ấy nuốt mất status thật, đúng vào lúc cần biết status nhất.
  //
  // Trả về object đã parse. Ném Error với câu đọc hiểu được khi body không phải JSON
  // hoặc HTTP không thành công → chỗ gọi chỉ còn việc hiện e.message.
  async function jsonHoacLoi(response) {
    const tho = await response.text();
    let j;
    try { j = tho ? JSON.parse(tho) : null; } catch { j = undefined; }
    if (j === undefined || typeof j !== 'object') throw new Error(moTaTrangLoi(response, tho));
    if (!response.ok) throw new Error((j && j.error) || ('HTTP ' + response.status));
    return j || {};
  }

  // Câu mô tả cho TRANG LỖI của hạ tầng (không phải lỗi của app): kèm status, nguyên nhân
  // hay gặp, <title> của trang (Cloudflare ghi sẵn "524: A timeout occurred") và mã CF-RAY
  // để tra trong dashboard. Cùng origin nên đọc được header.
  function moTaTrangLoi(response, tho) {
    const s = response.status;
    const viCo = {
      403: 'tường lửa Cloudflare chặn yêu cầu',
      411: 'thiếu Content-Length, http.sys chặn trước khi tới ứng dụng',
      413: 'file quá nặng, bị chặn trước khi tới ứng dụng',
      502: 'ứng dụng không trả lời (vừa khởi động lại, hoặc hết bộ nhớ giữa chừng)',
      503: 'ứng dụng đang không chạy (hoặc đang deploy)',
      504: 'xử lý quá lâu nên bị cắt',
      520: 'máy chủ trả phản hồi hỏng',
      521: 'máy chủ từ chối kết nối',
      524: 'xử lý quá 100 giây nên Cloudflare cắt kết nối',
    }[s];
    const tieuDe = (String(tho || '').match(/<title[^>]*>([^<]{0,160})<\/title>/i) || [])[1];
    const ray = (response.headers && response.headers.get) ? response.headers.get('cf-ray') : null;
    return (response.ok
      ? 'máy chủ trả dữ liệu không phải JSON (HTTP ' + s + ')'
      : 'máy chủ trả về trang lỗi HTTP ' + s + ' chứ không phải dữ liệu')
      + (viCo ? ' — ' + viCo : '')
      + (tieuDe ? ' ["' + tieuDe.trim() + '"]' : '')
      + (ray ? ' · mã tra cứu ' + ray : '');
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

  // ── Mở CRM đăng nhập SẴN (SSO) ────────────────────────────────────────────
  // Mở deep-link CRM và ĐĂNG NHẬP đúng account đang dùng ở Trav-ai (KHÔNG rơi vào trang login,
  // KHÔNG dùng lại cookie CRM cũ = account khác). Đi qua ticket SSO: POST /api/v1/crm-sso-ticket
  // (proxy ký HMAC danh tính, KHÔNG password → CRM sinh code 1-lần → set Forms-auth cookie).
  // - path: deep-link nội bộ CRM ('/customer-data', '/booking-ticket/123'…). Không có '/' đầu → tự thêm.
  // - onError(msg): callback báo lỗi (thường là pushToast). Không truyền → fallback alert.
  // Mở tab trống SYNC ngay đầu (trong click handler) để KHỎI bị popup-blocker chặn.
  async function openCrm(path, onError) {
    const win = window.open('', '_blank');
    try {
      const redirect = String(path || '/').startsWith('/') ? path : '/' + path;
      // Body '{}' tường minh: POST rỗng qua HTTP/2 có thể thiếu Content-Length → http.sys chặn 411
      // "Length Required" (trang HTML) trước cả khi tới app.
      const r = await window.tourkitAuth.authedFetch(
        '/api/v1/crm-sso-ticket?redirect=' + encodeURIComponent(redirect),
        { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      const j = await jsonHoacLoi(r);
      if (j.error) throw new Error(j.error);
      if (win) win.location = j.url; else window.location = j.url;
    } catch (e) {
      if (win) win.close();
      const msg = 'Không mở được CRM: ' + e.message;
      // Thứ tự xuống thang: toast của chỗ gọi → hộp thoại dùng chung → alert() thô.
      // alert() chỉ còn là chốt chặn cuối nếu lớp hộp thoại chưa nạp; không bao giờ nuốt lỗi.
      if (onError) onError(msg, 'error');
      else if (window.appAlert) window.appAlert(msg, { title: 'Không mở được CRM' });
      else alert(msg);
    }
  }

  window.tourkitUtil = { readSSE, fmtAgo, fmtDate, copyText, crmUrl, openCrm, jsonHoacLoi, moTaTrangLoi };
})();
