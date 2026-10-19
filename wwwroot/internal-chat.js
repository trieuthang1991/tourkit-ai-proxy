/*!
 * TourKit — Chat nội bộ (widget nhúng CRM)
 *   <script async src="https://<proxy>/internal-chat.js" data-session="{X-Session-Id}"></script>
 * Tuỳ chọn data-*: position (br|bl), z-index, order-url (mẫu link mở đơn, "{id}" thay bằng mã đơn).
 * Mở phòng theo ngữ cảnh từ trang CRM:
 *   window.TkInternalChat.openEntity('order', 'DH-0142', 'Nhật Bản mùa lá đỏ')
 *
 * JS thuần, Shadow DOM tách CSS khỏi trang CRM, không phụ thuộc gì. Mọi chữ do người dùng
 * nhập đi vào DOM bằng textContent — không bao giờ innerHTML.
 */
(function () {
  'use strict';

  const scriptEl = document.currentScript || document.querySelector('script[src*="internal-chat.js"][data-session]');
  if (!scriptEl) { console.warn('[tk-chat] không thấy thẻ <script>'); return; }
  const session = scriptEl.getAttribute('data-session');
  if (!session) { console.warn('[tk-chat] thiếu data-session'); return; }
  if (window.TkInternalChat) return;   // nhúng hai lần thì chỉ chạy một

  const BASE = (() => { try { return new URL(scriptEl.src).origin; } catch { return location.origin; } })();
  const API = BASE + '/api/v1/chat/internal';
  const POS = scriptEl.getAttribute('data-position') === 'bl' ? 'left' : 'right';
  const Z = parseInt(scriptEl.getAttribute('data-z-index') || '2147483000', 10);
  const ORDER_URL = scriptEl.getAttribute('data-order-url') || '';

  // ── Trạng thái ──────────────────────────────────────────────────────────
  const st = {
    open: false, view: 'list', filter: 'all', search: '',
    me: null, users: new Map(), rooms: [], room: null, msgs: [], hasOlder: false,
    newType: 'group', picked: new Set(), userSearch: '', newTitle: '', busy: false,
    drafts: {},   // nháp theo từng phòng — sống qua mỗi lần vẽ lại và qua việc chuyển phòng
    pending: {},  // tệp chờ gửi theo phòng: [{ key, name, size, kind, status: up|ok|err, att, preview, error }]
    dragOver: false,
    // Cỡ khung: 'compact' (một cột) | 'large' (hai cột). Nhớ theo trình duyệt — chỉ là tiện ích cá nhân.
    size: (() => { try { return localStorage.getItem('tk-chat-size') === 'large' ? 'large' : 'compact'; } catch { return 'compact'; } })(),
  };
  // Màn hẹp không đủ chỗ cho hai cột: phóng to tự lùi về một cột (vẫn nhớ lựa chọn cho màn rộng).
  const isLarge = () => st.size === 'large' && window.innerWidth >= 760;
  // Cùng danh sách trắng với máy chủ (InternalAttachmentRules) — chặn sớm cho đỡ một lượt tải hỏng.
  const ALLOWED = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.txt', '.csv', '.zip', '.rar'];
  const IMAGES = ['.png', '.jpg', '.jpeg', '.gif', '.webp'];
  const MAX_BYTES = 15 * 1024 * 1024;
  const extOf = (n) => { const i = (n || '').lastIndexOf('.'); return i < 0 ? '' : n.slice(i).toLowerCase(); };
  const fmtSize = (b) => b >= 1048576 ? (b / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  const safeUrl = (u) => typeof u === 'string' && /^https?:\/\//i.test(u) ? u : null;

  // ── Gọi API ─────────────────────────────────────────────────────────────
  let dead = false;
  async function api(method, path, body) {
    const opt = { method, headers: { 'X-Session-Id': session } };
    if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    const r = await fetch(API + path, opt);
    if (r.status === 401) { dead = true; host.remove(); console.warn('[tk-chat] phiên hết hạn'); throw new Error('401'); }
    if (r.status === 204) return null;
    const data = await r.json().catch(() => null);
    if (!r.ok) throw new Error((data && data.error) || ('HTTP ' + r.status));
    return data;
  }

  // ── Dựng DOM ────────────────────────────────────────────────────────────
  const host = document.createElement('div');
  host.style.cssText = `position:fixed;${POS}:24px;bottom:24px;z-index:${Z};`;
  const root = host.attachShadow({ mode: 'open' });
  root.innerHTML = `<style>
    :host{all:initial}
    *{box-sizing:border-box}
    .w{font-family:"Be Vietnam Pro",system-ui,-apple-system,"Segoe UI",sans-serif;color:#11151c;font-size:14px;line-height:1.5}
    button{font:inherit;cursor:pointer}
    button:focus-visible,a:focus-visible,input:focus-visible,textarea:focus-visible{outline:3px solid #6f86e6;outline-offset:2px}
    .fab{position:relative;width:56px;height:56px;border:0;border-radius:6px;background:#1f3fbf;color:#fff;display:grid;place-items:center;box-shadow:0 6px 16px rgba(31,63,191,.28)}
    .fab:hover{background:#18329a}
    .fab .n{position:absolute;top:-6px;${POS === 'right' ? 'right' : 'left'}:-6px;min-width:22px;height:22px;padding:0 6px;border-radius:6px;background:#fff;color:#1f3fbf;border:2px solid #1f3fbf;font-size:12px;font-weight:700;display:grid;place-items:center}
    .p{position:absolute;bottom:72px;${POS}:0;width:min(380px,calc(100vw - 32px));height:min(560px,calc(100vh - 120px));background:#fff;border:1px solid #e3e6ec;border-radius:6px;box-shadow:0 12px 32px rgba(17,21,28,.14);display:flex;flex-direction:column;overflow:hidden}
    .hd{display:flex;align-items:center;gap:8px;padding:10px 8px 10px 12px;border-bottom:1px solid #e3e6ec;min-height:57px}
    .hd h2{margin:0;font-size:15px;font-weight:600;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .hd small{display:block;font-size:12px;color:#5b6270;font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .ib{width:36px;height:36px;border:0;border-radius:6px;background:transparent;color:#5b6270;display:grid;place-items:center;flex-shrink:0}
    .ib:hover{background:#eef1fc;color:#1f3fbf}
    .pri{height:36px;padding:0 12px;border:0;border-radius:6px;background:#1f3fbf;color:#fff;font-size:13px;font-weight:600;display:flex;align-items:center;gap:6px;flex-shrink:0}
    .pri:hover{background:#18329a}.pri:disabled{opacity:.55;cursor:default}
    .sec{height:30px;padding:0 10px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;color:#11151c;font-size:12px;text-decoration:none;display:inline-flex;align-items:center}
    .sec:hover{background:#eef1fc}
    .tools{padding:12px 16px 8px;display:flex;flex-direction:column;gap:10px}
    .in{width:100%;height:38px;border:1px solid #d5d9e0;border-radius:6px;padding:0 10px;font:inherit;font-size:14px;color:#11151c;background:#fff}
    .chips{display:flex;gap:6px;flex-wrap:wrap}
    .chip{height:30px;padding:0 12px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;color:#11151c;font-size:13px}
    .chip[aria-pressed="true"]{border-color:#1f3fbf;background:#eef1fc;color:#1f3fbf;font-weight:600}
    ul{list-style:none;margin:0;padding:0 8px 8px;overflow:auto;flex:1}
    .it{width:100%;display:flex;gap:10px;align-items:center;padding:10px 8px;border:0;border-radius:6px;background:transparent;color:#11151c;text-align:left}
    .it:hover{background:#f5f6f8}.it.u{background:#eef1fc}
    .av{width:40px;height:40px;flex-shrink:0;border-radius:6px;background:#e3e6ec;color:#11151c;display:grid;place-items:center;font-weight:600;font-size:13px}
    .av.c{background:#eef1fc;color:#1f3fbf}.av.s{width:32px;height:32px;font-size:12px}
    .tx{flex:1;min-width:0;display:flex;flex-direction:column;gap:2px}
    .r1,.r2{display:flex;gap:8px;align-items:center}
    .nm{font-weight:600;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .tm{font-size:12px;color:#5b6270;flex-shrink:0}.u .tm{color:#1f3fbf;font-weight:600}
    .pv{font-size:13px;color:#5b6270;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.u .pv{color:#11151c;font-weight:500}
    .bd{min-width:20px;height:20px;padding:0 6px;border-radius:6px;background:#1f3fbf;color:#fff;font-size:12px;font-weight:600;display:grid;place-items:center}
    .empty{padding:32px 16px;text-align:center;color:#5b6270;font-size:13px}
    .ms{flex:1;overflow:auto;padding:12px 14px;display:flex;flex-direction:column;gap:10px;background:#f5f6f8}
    .day{align-self:center;font-size:12px;color:#5b6270}
    .m{display:flex;flex-direction:column;align-items:flex-start;gap:2px;max-width:82%}
    .m.me{align-self:flex-end;align-items:flex-end}
    .m .who{font-size:12px;font-weight:600}
    .m .row{display:flex;align-items:center;gap:6px}
    .bb{padding:8px 12px;border-radius:6px;background:#fff;border:1px solid #e3e6ec;white-space:pre-wrap;word-break:break-word}
    .me .bb{background:#1f3fbf;border-color:#1f3fbf;color:#fff}
    .bb.del{background:#fff;border:1px dashed #d5d9e0;color:#5b6270;font-style:italic;font-size:13px}
    .m .t{font-size:11px;color:#5b6270}
    .rc{height:26px;padding:0 8px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;color:#11151c;font-size:12px;visibility:hidden}
    .m:hover .rc,.rc:focus-visible{visibility:visible}
    .cp{border-top:1px solid #e3e6ec;padding:10px 12px;display:flex;flex-direction:column;gap:6px}
    .cp .row{display:flex;gap:8px;align-items:flex-end}
    textarea{flex:1;resize:none;border:1px solid #d5d9e0;border-radius:6px;padding:8px 10px;font:inherit;font-size:14px;color:#11151c;min-height:44px;max-height:120px}
    .send{width:44px;height:44px;flex-shrink:0;border:0;border-radius:6px;background:#1f3fbf;color:#fff;display:grid;place-items:center}
    .send:hover{background:#18329a}
    .hint{font-size:11px;color:#5b6270}
    .err{margin:0 12px 8px;padding:8px 10px;border-radius:6px;background:#fdecea;color:#8a1c12;font-size:13px}
    .seg{display:grid;grid-template-columns:1fr 1fr;border:1px solid #d5d9e0;border-radius:6px;overflow:hidden}
    .seg button{height:36px;border:0;background:#fff;color:#11151c;font-size:13px}
    .seg button[aria-pressed="true"]{background:#eef1fc;color:#1f3fbf;font-weight:600}
    .lb{font-size:13px;font-weight:500;display:block;margin-bottom:4px}
    .pk{display:flex;flex-wrap:wrap;gap:6px}
    .pk span{height:28px;padding:0 4px 0 10px;border-radius:6px;background:#eef1fc;color:#1f3fbf;font-size:12px;font-weight:600;display:flex;align-items:center;gap:2px}
    .pk button{width:22px;height:22px;border:0;background:transparent;color:#1f3fbf;display:grid;place-items:center}
    .opt{display:flex;align-items:center;gap:10px;padding:8px;border-radius:6px;cursor:pointer}
    .opt:hover{background:#f5f6f8}
    .opt input{width:18px;height:18px;accent-color:#1f3fbf}
    .ft{border-top:1px solid #e3e6ec;padding:12px 16px;display:flex;flex-direction:column;gap:8px}
    .ft .pri{height:44px;justify-content:center;font-size:14px}
    .sr{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
    .img{display:block;border-radius:6px;overflow:hidden;border:1px solid #e3e6ec;background:#fff;line-height:0}
    .img img{display:block;max-width:220px;max-height:220px;object-fit:cover}
    .fa{display:flex;align-items:center;gap:8px;padding:8px 10px;border-radius:6px;background:#fff;border:1px solid #e3e6ec;color:#11151c;text-decoration:none;max-width:240px}
    .fa:hover{border-color:#1f3fbf}
    .fa .fn{font-size:13px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .fa .fs{font-size:11px;color:#5b6270}
    .fa svg{color:#1f3fbf;flex-shrink:0}
    .pend{display:flex;gap:6px;flex-wrap:wrap}
    .pc{position:relative;display:flex;align-items:center;gap:6px;height:44px;padding:0 28px 0 6px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;max-width:200px;font-size:12px}
    .pc img{width:32px;height:32px;object-fit:cover;border-radius:4px}
    .pc .fn{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .pc.err{border-color:#c0392b;color:#8a1c12}
    .pc .x{position:absolute;top:2px;right:2px;width:22px;height:22px;border:0;background:transparent;color:#5b6270;display:grid;place-items:center}
    .pc .st{font-size:11px;color:#5b6270}
    .p.lg{width:min(920px,calc(100vw - 32px));height:min(720px,calc(100vh - 120px));flex-direction:row}
    .pane{position:relative;display:flex;flex-direction:column;flex:1;min-width:0;min-height:0}
    .lg .pane.list{flex:0 0 300px;border-right:1px solid #e3e6ec}
    .lg .ms{padding:16px 20px}.lg .m{max-width:70%}.lg .img img{max-width:320px;max-height:320px}
    .blank{flex:1;display:grid;place-items:center;color:#5b6270;font-size:13px;background:#f5f6f8;text-align:center;padding:24px}
    .drop{position:absolute;inset:0;background:rgba(238,241,252,.92);border:2px dashed #1f3fbf;border-radius:6px;display:grid;place-items:center;color:#1f3fbf;font-weight:600;pointer-events:none}
    @media (prefers-reduced-motion:reduce){*{transition:none!important;scroll-behavior:auto!important}}
  </style><div class="w"></div>`;
  const W = root.querySelector('.w');

  // ── Tiện ích dựng phần tử ───────────────────────────────────────────────
  function el(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'on') for (const [ev, fn] of Object.entries(v)) e.addEventListener(ev, fn);
      else if (k === 'text') e.textContent = v;
      else e.setAttribute(k, v === true ? '' : String(v));
    }
    for (const k of kids.flat()) if (k != null && k !== false) e.append(k.nodeType ? k : document.createTextNode(String(k)));
    return e;
  }
  const ICONS = {
    chat: 'M4 5h16v11H9l-5 4z', plus: 'M12 5v14M5 12h14', x: 'M6 6l12 12M18 6L6 18', back: 'M15 6l-6 6 6 6',
    send: 'M4 12l16-8-6 16-2-7z', order: 'M7 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM9 8h6M9 12h6M9 16h4',
    company: 'M4 21V7l8-4 8 4v14M9 21v-6h6v6',
    clip: 'M21 12.5l-8.5 8.5a5 5 0 0 1-7-7l9-9a3.5 3.5 0 0 1 5 5l-9 9a2 2 0 0 1-3-3l8-8',
    file: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5',
    max: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5', min: 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5', group: 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM3 20c0-3 3-5 6-5s6 2 6 5M17 11.5a2.5 2.5 0 1 0 0-5M15 15c3 0 6 1.5 6 4.5',
  };
  function icon(name, size) {
    const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('width', size || 18); s.setAttribute('height', size || 18); s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('fill', 'none'); s.setAttribute('stroke', 'currentColor'); s.setAttribute('stroke-width', '2');
    s.setAttribute('stroke-linecap', 'round'); s.setAttribute('stroke-linejoin', 'round'); s.setAttribute('aria-hidden', 'true');
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path'); p.setAttribute('d', ICONS[name]); s.append(p);
    return s;
  }
  const initials = (n) => (n || '?').trim().split(/\s+/).filter(Boolean).slice(-2).map((w) => w[0]).join('').toUpperCase() || '?';
  const userName = (id, fallback) => st.users.get(id) || fallback || ('#' + id);
  const pad = (n) => String(n).padStart(2, '0');
  const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  function shortTime(iso) {
    const d = new Date(iso), now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
    if (sameDay(d, now)) return pad(d.getHours()) + ':' + pad(d.getMinutes());
    if (sameDay(d, y)) return 'Hôm qua';
    return pad(d.getDate()) + '/' + pad(d.getMonth() + 1);
  }
  function dayLabel(d) {
    const now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
    if (sameDay(d, now)) return 'Hôm nay';
    if (sameDay(d, y)) return 'Hôm qua';
    return pad(d.getDate()) + '/' + pad(d.getMonth() + 1) + '/' + d.getFullYear();
  }
  const hm = (iso) => { const d = new Date(iso); return pad(d.getHours()) + ':' + pad(d.getMinutes()); };

  function roomName(r) {
    if (r.entityType === 'direct') {
      const other = (r.members || []).find((m) => m.userId !== st.me);
      return other ? userName(other.userId) : 'Phòng 1-1';
    }
    if (r.entityType === 'order') return 'Đơn ' + r.entityId + (r.title ? ' · ' + r.title : '');
    if (r.entityType === 'company') return r.title || ('Công ty ' + r.entityId);
    return (r.title || 'Nhóm') + ' · ' + (r.members || []).length + ' người';
  }
  function roomAvatar(r, small) {
    const cls = 'av' + (small ? ' s' : '');
    if (r.entityType === 'direct') return el('span', { class: cls, 'aria-hidden': 'true' }, initials(roomName(r)));
    const ic = r.entityType === 'order' ? 'order' : r.entityType === 'company' ? 'company' : 'group';
    return el('span', { class: cls + (r.entityType === 'group' ? '' : ' c'), 'aria-hidden': 'true' }, icon(ic, 20));
  }
  const totalUnread = () => st.rooms.reduce((s, r) => s + (r.unreadCount || 0), 0);

  // ── Vẽ ──────────────────────────────────────────────────────────────────
  let errMsg = '';
  function render() {
    if (dead) return;
    const keepScroll = W.querySelector('.ms');
    const atBottom = keepScroll ? keepScroll.scrollHeight - keepScroll.scrollTop - keepScroll.clientHeight < 40 : true;
    // Vẽ lại cả khung mỗi lần có sự kiện — phải trả lại focus + con trỏ, không thì người đang gõ
    // bị văng khỏi ô ngay khi có tin mới tới.
    const act = root.activeElement;
    const actId = act && act.id;
    const sel = act && typeof act.selectionStart === 'number' ? [act.selectionStart, act.selectionEnd] : null;
    W.replaceChildren();
    const n = totalUnread();
    const fab = el('button', {
      class: 'fab', type: 'button', 'aria-expanded': String(st.open),
      'aria-label': st.open ? 'Thu nhỏ chat nội bộ' : ('Mở chat nội bộ' + (n ? ', ' + n + ' tin chưa đọc' : '')),
      on: { click: () => { st.open = !st.open; if (st.open) loadRooms(); render(); } },
    }, icon(st.open ? 'x' : 'chat', 26), n ? el('span', { class: 'n', 'aria-hidden': 'true' }, n > 99 ? '99+' : n) : null);
    if (st.open) {
      if (isLarge()) {
        const right = st.view === 'room' && st.room ? viewRoom() : st.view === 'new' ? viewNew()
          : el('div', { class: 'blank' }, 'Chọn một cuộc trò chuyện bên trái, hoặc bấm "Trò chuyện mới".');
        W.append(el('section', { class: 'p lg', 'aria-label': 'Chat nội bộ' }, viewList(), right));
      } else {
        const one = st.view === 'room' && st.room ? viewRoom() : st.view === 'new' ? viewNew() : viewList();
        W.append(el('section', { class: 'p', 'aria-label': 'Chat nội bộ' }, one));
      }
    }
    W.append(fab);
    const ms = W.querySelector('.ms');
    if (ms && atBottom) ms.scrollTop = ms.scrollHeight;
    if (actId) {
      const n = root.getElementById(actId);
      if (n) { n.focus(); if (sel) { try { n.setSelectionRange(sel[0], sel[1]); } catch { /* ô không có con trỏ */ } } }
    }
  }

  /// Ô nhập lọc theo từng phím. Bỏ qua lúc bộ gõ đang ghép chữ (composition) — vẽ lại giữa chừng là
  /// phá dấu tiếng Việt; compositionend vẽ một lần khi chữ đã ghép xong.
  function liveInput(set) {
    return {
      input: (e) => { set(e.target.value); if (!e.isComposing) render(); },
      compositionend: (e) => { set(e.target.value); render(); },
    };
  }

  function errBox() { return errMsg ? el('div', { class: 'err', role: 'alert' }, errMsg) : null; }
  function sizeBtn() {
    const lg = st.size === 'large';
    return el('button', { class: 'ib', type: 'button', 'aria-label': lg ? 'Thu về khung nhỏ' : 'Phóng to', title: lg ? 'Thu về khung nhỏ' : 'Phóng to',
      on: { click: () => { st.size = lg ? 'compact' : 'large'; try { localStorage.setItem('tk-chat-size', st.size); } catch { /* chế độ riêng tư */ } render(); } } },
      icon(lg ? 'min' : 'max'));
  }
  function closeBtn() { return el('button', { class: 'ib', type: 'button', 'aria-label': 'Thu nhỏ', on: { click: () => { st.open = false; render(); } } }, icon('x')); }
  function backBtn() { return el('button', { class: 'ib', type: 'button', 'aria-label': 'Về danh sách', on: { click: () => { st.view = 'list'; st.room = null; errMsg = ''; loadRooms(); render(); } } }, icon('back')); }

  function viewList() {
    const q = st.search.trim().toLowerCase();
    const rooms = st.rooms.filter((r) => {
      if (st.filter === 'unread' && !r.unreadCount) return false;
      if (st.filter === 'entity' && !(r.entityType === 'order' || r.entityType === 'company')) return false;
      return !q || roomName(r).toLowerCase().includes(q);
    });
    const unreadRooms = st.rooms.filter((r) => r.unreadCount).length;
    const chip = (key, label) => el('button', { class: 'chip', type: 'button', 'aria-pressed': String(st.filter === key), on: { click: () => { st.filter = key; render(); } } }, label);
    const search = el('input', { class: 'in', type: 'search', id: 'tk-tim', placeholder: 'Tìm người hoặc phòng', value: st.search,
      on: liveInput((v) => { st.search = v; }) });
    const lg = isLarge();
    return el('div', { class: 'pane list' },
      el('div', { class: 'hd' },
        el('h2', {}, 'Chat nội bộ'),
        el('button', { class: 'pri', type: 'button', 'aria-label': 'Trò chuyện mới', on: { click: () => openNew() } }, icon('plus', 16), lg ? null : 'Trò chuyện mới'),
        window.innerWidth >= 760 ? sizeBtn() : null, closeBtn()),
      el('div', { class: 'tools' },
        el('label', { for: 'tk-tim', class: 'sr' }, 'Tìm người hoặc phòng'), search,
        el('div', { class: 'chips' }, chip('all', 'Tất cả'), chip('unread', 'Chưa đọc' + (unreadRooms ? ' · ' + unreadRooms : '')), chip('entity', 'Theo đơn'))),
      errBox(),
      rooms.length
        ? el('ul', {}, rooms.map((r) => el('li', {}, el('button', { class: 'it' + (r.unreadCount ? ' u' : ''), type: 'button',
            'aria-current': st.room && st.room.id === r.id && st.view === 'room' ? 'true' : null,
            style: st.room && st.room.id === r.id && st.view === 'room' && lg ? 'outline:2px solid #1f3fbf;outline-offset:-2px' : null,
            on: { click: () => openRoom(r.id) } },
            roomAvatar(r),
            el('span', { class: 'tx' },
              el('span', { class: 'r1' }, el('span', { class: 'nm' }, roomName(r)), el('span', { class: 'tm' }, shortTime(r.lastActivityAt))),
              el('span', { class: 'r2' }, el('span', { class: 'pv' }, r.lastPreview || 'Chưa có tin nhắn'),
                r.unreadCount ? el('span', { class: 'bd' }, r.unreadCount >= 100 ? '99+' : r.unreadCount) : null))))))
        : el('div', { class: 'empty' }, st.rooms.length ? 'Không có phòng nào khớp.' : 'Chưa có cuộc trò chuyện nào. Bấm "Trò chuyện mới" để bắt đầu.'));
  }

  function viewRoom() {
    const r = st.room;
    const sub = r.entityType === 'direct' ? null : (r.members || []).length + ' thành viên';
    const orderLink = r.entityType === 'order' && ORDER_URL
      ? el('a', { class: 'sec', href: ORDER_URL.replace('{id}', encodeURIComponent(r.entityId)), target: '_top' }, 'Mở đơn') : null;
    const list = el('div', { class: 'ms', 'aria-live': 'polite' });
    if (st.hasOlder) list.append(el('button', { class: 'sec', type: 'button', style: 'align-self:center', on: { click: loadOlder } }, 'Xem tin cũ hơn'));
    if (!st.msgs.length) list.append(el('div', { class: 'empty' }, 'Chưa có tin nhắn. Gửi lời chào đầu tiên.'));
    let prevDay = null;
    for (const m of st.msgs) {
      const d = new Date(m.createdUtc);
      if (!prevDay || !sameDay(prevDay, d)) { list.append(el('div', { class: 'day' }, dayLabel(d))); prevDay = d; }
      const mine = m.senderUserId === st.me;
      const deleted = !!m.deletedUtc;
      const a = !deleted && m.attachment && safeUrl(m.attachment.url) ? m.attachment : null;
      const attEl = !a ? null : a.kind === 'anh'
        ? el('a', { class: 'img', href: a.url, target: '_blank', rel: 'noopener noreferrer', 'aria-label': 'Mở ảnh ' + a.name },
            el('img', { src: a.url, alt: a.name, loading: 'lazy' }))
        : el('a', { class: 'fa', href: a.url, target: '_blank', rel: 'noopener noreferrer' }, icon('file', 22),
            el('span', { class: 'tx' }, el('span', { class: 'fn' }, a.name), el('span', { class: 'fs' }, a.size ? fmtSize(a.size) : 'Tệp')));
      const bubble = deleted
        ? el('div', { class: 'bb del' }, mine ? 'Bạn đã thu hồi một tin nhắn' : 'Tin nhắn đã được thu hồi')
        : m.body ? el('div', { class: 'bb' }, m.body) : null;
      const recall = mine && !deleted ? el('button', { class: 'rc', type: 'button', on: { click: () => recallMsg(m) } }, 'Thu hồi') : null;
      list.append(el('div', { class: 'm' + (mine ? ' me' : '') },
        !mine && r.entityType !== 'direct' ? el('span', { class: 'who' }, userName(m.senderUserId, m.senderUsername)) : null,
        attEl ? el('div', { class: 'row' }, bubble ? null : recall, attEl) : null,
        bubble ? el('div', { class: 'row' }, recall, bubble) : null,
        el('span', { class: 't' }, hm(m.createdUtc))));
    }
    const ta = el('textarea', { id: 'tk-soan', rows: '2', maxlength: '4000', placeholder: r.entityType === 'direct' ? 'Nhắn cho ' + roomName(r) : 'Nhắn cho cả phòng',
      on: {
        input: (e) => { st.drafts[r.id] = e.target.value; },
        keydown: (e) => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(ta); } },
        // Dán ảnh chụp màn hình hoặc tệp đã copy trong Explorer/Finder. Dán chữ thì để mặc định.
        paste: (e) => {
          const files = [...(e.clipboardData?.files || [])];
          if (files.length) { e.preventDefault(); addFiles(r.id, files); }
        },
      } });
    ta.value = st.drafts[r.id] || '';
    const picker = el('input', { type: 'file', multiple: true, class: 'sr', id: 'tk-tep', tabindex: '-1', 'aria-hidden': 'true',
      accept: ALLOWED.join(','), on: { change: (e) => { addFiles(r.id, [...e.target.files]); e.target.value = ''; } } });
    const pend = st.pending[r.id] || [];
    const uploading = pend.some((p) => p.status === 'up');
    const pendEl = pend.length ? el('div', { class: 'pend' }, pend.map((p) => el('div', { class: 'pc' + (p.status === 'err' ? ' err' : '') },
      p.preview ? el('img', { src: p.preview, alt: '' }) : icon('file', 20),
      el('span', { class: 'tx' }, el('span', { class: 'fn' }, p.name),
        el('span', { class: 'st' }, p.status === 'up' ? 'Đang tải lên…' : p.status === 'err' ? p.error : fmtSize(p.size))),
      el('button', { class: 'x', type: 'button', 'aria-label': 'Bỏ ' + p.name, on: { click: () => removePending(r.id, p.key) } }, icon('x', 12))))) : null;
    const lg = isLarge();
    const panel = el('div', { class: 'pane', role: 'region', 'aria-label': 'Trò chuyện: ' + roomName(r),
      on: {
        dragover: (e) => { if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); if (!st.dragOver) { st.dragOver = true; render(); } } },
        dragleave: (e) => { if (e.target === panel || !panel.contains(e.relatedTarget)) { st.dragOver = false; render(); } },
        drop: (e) => { e.preventDefault(); st.dragOver = false; addFiles(r.id, [...(e.dataTransfer?.files || [])]); },
      } },
      el('div', { class: 'hd' }, lg ? null : backBtn(), roomAvatar(r, true),
        el('h2', {}, roomName(r), sub ? el('small', {}, sub) : null), orderLink,
        lg || window.innerWidth < 760 ? null : sizeBtn(), lg ? null : closeBtn()),
      list, errBox(),
      el('div', { class: 'cp' },
        pendEl,
        el('div', { class: 'row' },
          el('button', { class: 'ib', type: 'button', 'aria-label': 'Đính kèm tệp', on: { click: () => picker.click() } }, icon('clip')),
          picker,
          el('label', { for: 'tk-soan', class: 'sr' }, 'Soạn tin nhắn'), ta,
          el('button', { class: 'send', type: 'button', 'aria-label': uploading ? 'Đang tải tệp lên' : 'Gửi', disabled: uploading, on: { click: () => send(ta) } }, icon('send'))),
        el('span', { class: 'hint' }, 'Enter để gửi · Shift + Enter xuống dòng · Dán hoặc kéo thả tệp vào đây')),
      st.dragOver ? el('div', { class: 'drop' }, 'Thả tệp để gửi vào phòng') : null);
    return panel;
  }

  function viewNew() {
    const q = st.userSearch.trim().toLowerCase();
    const people = [...st.users.entries()].filter(([id, n]) => id !== st.me && (!q || n.toLowerCase().includes(q)));
    const direct = st.newType === 'direct';
    const pickToggle = (id) => {
      if (direct) { st.picked = new Set([id]); }
      else if (st.picked.has(id)) st.picked.delete(id); else st.picked.add(id);
      render();
    };
    const title = el('input', { class: 'in', id: 'tk-ten', type: 'text', maxlength: '120', value: st.newTitle || '', placeholder: 'Ví dụ: Đoàn Nhật tháng 11',
      on: { input: (e) => { st.newTitle = e.target.value; } } });
    const us = el('input', { class: 'in', id: 'tk-tim-nv', type: 'search', placeholder: 'Tìm theo tên nhân viên', value: st.userSearch,
      on: liveInput((v) => { st.userSearch = v; }) });
    const seg = (key, label) => el('button', { type: 'button', 'aria-pressed': String(st.newType === key), on: { click: () => { st.newType = key; if (key === 'direct' && st.picked.size > 1) st.picked = new Set([...st.picked].slice(0, 1)); render(); } } }, label);
    const n = st.picked.size;
    const can = direct ? n === 1 : n >= 1;
    return el('div', { class: 'pane', role: 'region', 'aria-label': 'Tạo cuộc trò chuyện' },
      el('div', { class: 'hd' }, backBtn(), el('h2', {}, 'Trò chuyện mới'), isLarge() ? null : closeBtn()),
      el('div', { class: 'tools' },
        el('div', { class: 'seg' }, seg('direct', 'Nhắn một người'), seg('group', 'Tạo nhóm')),
        direct ? null : el('div', {}, el('label', { class: 'lb', for: 'tk-ten' }, 'Tên nhóm'), title),
        el('div', {}, el('label', { class: 'lb', for: 'tk-tim-nv' }, direct ? 'Nhắn cho' : 'Thêm người'), us),
        n && !direct ? el('div', { class: 'pk' }, [...st.picked].map((id) => el('span', {}, userName(id),
          el('button', { type: 'button', 'aria-label': 'Bỏ ' + userName(id), on: { click: () => pickToggle(id) } }, icon('x', 12))))) : null),
      errBox(),
      people.length
        ? el('ul', { style: 'border-top:1px solid #e3e6ec' }, people.map(([id, name]) => el('li', {},
            el('label', { class: 'opt' },
              el('input', { type: direct ? 'radio' : 'checkbox', name: 'tk-nv', checked: st.picked.has(id), on: { change: () => pickToggle(id) } }),
              el('span', { class: 'av s', 'aria-hidden': 'true' }, initials(name)), el('span', {}, name)))))
        : el('div', { class: 'empty' }, st.users.size ? 'Không thấy nhân viên nào khớp.' : 'Đang tải danh sách nhân viên…'),
      el('div', { class: 'ft' },
        el('span', { class: 'hint' }, (n ? 'Đã chọn ' + n + ' người · ' : '') + 'Muốn bàn về một đơn hay một công ty, mở phòng ngay trên trang đơn hoặc trang công ty đó'),
        el('button', { class: 'pri', type: 'button', disabled: !can || st.busy, on: { click: createRoom } }, 'Bắt đầu trò chuyện')));
  }

  // ── Hành động ───────────────────────────────────────────────────────────
  async function loadUsers() {
    try {
      const r = await api('GET', '/users');
      st.me = r.meId;
      st.users = new Map((r.users || []).filter((u) => u && u.id).map((u) => [u.id, u.name]));
    } catch (e) { if (!dead) console.warn('[tk-chat] không tải được nhân viên:', e.message); }
  }
  let roomsSeq = 0;
  async function loadRooms() {
    const seq = ++roomsSeq;
    try {
      const rooms = await api('GET', '/rooms?limit=100');
      if (seq !== roomsSeq) return;   // bỏ kết quả về muộn
      st.rooms = rooms || [];
      if (st.room) st.room = st.rooms.find((x) => x.id === st.room.id) || st.room;
      render();
    } catch (e) { if (!dead) { errMsg = 'Không tải được danh sách phòng.'; render(); } }
  }
  let refreshTimer = null;
  const refreshRoomsSoon = () => { clearTimeout(refreshTimer); refreshTimer = setTimeout(loadRooms, 800); };

  async function openRoom(id) {
    errMsg = '';
    st.room = st.rooms.find((x) => x.id === id) || { id, entityType: 'group', members: [] };
    st.view = 'room'; st.msgs = []; st.hasOlder = false; render();
    try {
      const page = await api('GET', `/rooms/${id}/messages?limit=50`);
      st.msgs = (page || []).slice().reverse();
      st.hasOlder = (page || []).length === 50;
      render(); jumpBottom();
      markRead(id);
      if (!st.rooms.some((x) => x.id === id)) loadRooms();
    } catch (e) { if (!dead) { errMsg = e.message; render(); } }
  }
  async function loadOlder() {
    const first = st.msgs[0]; if (!first || !st.room) return;
    const ms = W.querySelector('.ms'); const before = ms ? ms.scrollHeight - ms.scrollTop : 0;
    try {
      const page = await api('GET', `/rooms/${st.room.id}/messages?limit=50&beforeUtc=${encodeURIComponent(first.createdUtc)}&beforeId=${first.id}`);
      st.msgs = (page || []).slice().reverse().concat(st.msgs);
      st.hasOlder = (page || []).length === 50;
      render();
      const ms2 = W.querySelector('.ms'); if (ms2) ms2.scrollTop = ms2.scrollHeight - before;
    } catch (e) { if (!dead) { errMsg = e.message; render(); } }
  }
  function mergeMsgs(list) {
    const seen = new Set(st.msgs.map((m) => m.id));
    for (const m of list) if (!seen.has(m.id)) { st.msgs.push(m); seen.add(m.id); }
    st.msgs.sort((a, b) => (a.createdUtc < b.createdUtc ? -1 : a.createdUtc > b.createdUtc ? 1 : a.id - b.id));
  }
  async function fetchNewer() {
    if (!st.room) return;
    const roomId = st.room.id;
    const last = st.msgs.length ? st.msgs[st.msgs.length - 1].id : 0;
    try {
      const page = await api('GET', `/rooms/${roomId}/messages?afterId=${last}&limit=100`);
      if (!st.room || st.room.id !== roomId) return;
      if (page && page.length) { mergeMsgs(page); render(); markRead(roomId); }
    } catch { /* lần sự kiện sau sẽ bù */ }
  }
  async function markRead(id) {
    try { await api('POST', `/rooms/${id}/read`); } catch { return; }
    const r = st.rooms.find((x) => x.id === id); if (r && r.unreadCount) { r.unreadCount = 0; render(); }
  }
  // ── Tệp đính kèm ────────────────────────────────────────────────────────
  let pendSeq = 0;
  function addFiles(roomId, files) {
    if (!files.length) return;
    const list = st.pending[roomId] || (st.pending[roomId] = []);
    for (const f of files.slice(0, 10)) {
      // Ảnh dán từ clipboard thường không có tên ("image.png") — đặt tên theo giờ cho dễ nhận.
      const name = f.name && f.name !== 'image.png' ? f.name : 'anh-dan-' + new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '') + extOf(f.name || '.png');
      const ext = extOf(name);
      const p = { key: ++pendSeq, name, size: f.size, kind: IMAGES.includes(ext) ? 'anh' : 'tep', status: 'up', att: null, preview: null, error: '' };
      if (!ALLOWED.includes(ext)) { p.status = 'err'; p.error = 'Loại tệp không gửi được'; }
      else if (f.size > MAX_BYTES) { p.status = 'err'; p.error = 'Quá 15 MB'; }
      if (p.kind === 'anh' && p.status === 'up') p.preview = URL.createObjectURL(f);
      list.push(p);
      if (p.status === 'up') upload(roomId, p, f, name);
    }
    render();
  }
  async function upload(roomId, p, file, name) {
    const fd = new FormData(); fd.append('file', file, name);
    try {
      const r = await fetch(`${API}/rooms/${roomId}/upload`, { method: 'POST', headers: { 'X-Session-Id': session }, body: fd });
      if (r.status === 401) { dead = true; host.remove(); return; }
      const data = await r.json().catch(() => null);
      if (!r.ok || !data || !safeUrl(data.url)) throw new Error((data && data.error) || ('HTTP ' + r.status));
      p.att = data; p.status = 'ok';
    } catch (e) { p.status = 'err'; p.error = e.message || 'Tải lên hỏng'; }
    render();
  }
  function removePending(roomId, key) {
    const list = st.pending[roomId] || [];
    const i = list.findIndex((p) => p.key === key);
    if (i >= 0) { if (list[i].preview) URL.revokeObjectURL(list[i].preview); list.splice(i, 1); }
    render();
  }

  async function postMessage(roomId, body, att) {
    const m = await api('POST', `/rooms/${roomId}/messages`, att ? { body, attachment: att } : { body });
    mergeMsgs([m]);
    const r = st.rooms.find((x) => x.id === roomId);
    if (r) { r.lastPreview = body ? body.slice(0, 200) : (att.kind === 'anh' ? '[Ảnh] ' : '[Tệp] ') + att.name; r.lastActivityAt = m.createdUtc; }
  }
  async function send(ta) {
    if (!st.room || st.busy) return;
    const roomId = st.room.id;
    const body = (st.drafts[roomId] || ta.value || '').trim();
    const pend = st.pending[roomId] || [];
    if (pend.some((p) => p.status === 'up')) return;   // nút Gửi đã khoá; Enter cũng phải chờ
    const ready = pend.filter((p) => p.status === 'ok');
    if (!body && !ready.length) return;
    st.busy = true; errMsg = '';
    try {
      // Chữ đi kèm tệp ĐẦU TIÊN; mỗi tệp còn lại là một tin — giống mọi app chat khác.
      if (ready.length) {
        for (let i = 0; i < ready.length; i++) {
          await postMessage(roomId, i === 0 ? (body || null) : null, ready[i].att);
          if (i === 0) st.drafts[roomId] = '';
          removePending(roomId, ready[i].key);
        }
      } else {
        await postMessage(roomId, body, null);
        st.drafts[roomId] = '';
      }
      render(); jumpBottom();
      root.getElementById('tk-soan')?.focus();
    } catch (e) { if (!dead) { errMsg = 'Chưa gửi được: ' + e.message; render(); } }   // nháp + tệp chưa gửi còn nguyên
    finally { st.busy = false; }
  }
  async function recallMsg(m) {
    try { await api('DELETE', `/messages/${m.id}`); m.deletedUtc = new Date().toISOString(); m.body = null; render(); }
    catch (e) { if (!dead) { errMsg = 'Không thu hồi được: ' + e.message; render(); } }
  }
  function openNew() {
    st.view = 'new'; st.newType = 'group'; st.picked = new Set(); st.userSearch = ''; st.newTitle = ''; errMsg = '';
    render(); if (!st.users.size) loadUsers().then(render);
  }
  async function createRoom() {
    if (st.busy) return;
    st.busy = true; errMsg = ''; render();
    try {
      const body = st.newType === 'direct'
        ? { entityType: 'direct', memberUserIds: [...st.picked] }
        : { entityType: 'group', title: (st.newTitle || '').trim() || null, memberUserIds: [...st.picked] };
      const r = await api('POST', '/rooms', body);
      st.busy = false;
      await loadRooms();
      openRoom(r.id);
    } catch (e) { st.busy = false; if (!dead) { errMsg = e.message; render(); } }
  }
  function jumpBottom() { const ms = W.querySelector('.ms'); if (ms) ms.scrollTop = ms.scrollHeight; }

  // ── Real-time ───────────────────────────────────────────────────────────
  // EventSource không gửi được header nên phiên đi ?sessionId=. Trình duyệt tự nối lại; mỗi lần
  // nối lại thì bù những gì có thể đã rơi trong lúc đứt.
  let es = null, hadError = false;
  function subscribe() {
    if (dead || es) return;
    es = new EventSource(API + '/events?sessionId=' + encodeURIComponent(session));
    es.onmessage = (ev) => {
      let d; try { d = JSON.parse(ev.data); } catch { return; }
      if (st.room && d.roomId === st.room.id && st.view === 'room') fetchNewer();
      refreshRoomsSoon();
    };
    es.onopen = () => { if (hadError) { hadError = false; loadRooms(); fetchNewer(); } };
    es.onerror = () => { hadError = true; if (dead && es) { es.close(); es = null; } };
  }

  // ── API cho trang CRM ───────────────────────────────────────────────────
  window.TkInternalChat = {
    open() { st.open = true; loadRooms(); render(); },
    async openEntity(type, entityId, title) {
      if (!(type === 'order' || type === 'company') || !entityId) throw new Error('openEntity: cần type order|company và mã');
      st.open = true; errMsg = ''; render();
      try {
        const r = await api('POST', '/rooms', { entityType: type, entityId: String(entityId), title: title || null, memberUserIds: [] });
        await loadRooms(); openRoom(r.id);
      } catch (e) { if (!dead) { errMsg = e.message; render(); } }
    },
  };

  // ── Khởi động ───────────────────────────────────────────────────────────
  function boot() {
    (document.body || document.documentElement).appendChild(host);
    // Esc thu khung lại (khi focus đang ở trong widget) — không nghe phím toàn trang.
    root.addEventListener('keydown', (e) => { if (e.key === 'Escape' && st.open) { st.open = false; render(); host.shadowRoot.querySelector('.fab')?.focus(); } });
    let rz = null;
    window.addEventListener('resize', () => { clearTimeout(rz); rz = setTimeout(() => { if (st.open) render(); }, 150); });
    render();
    loadUsers().then(loadRooms).then(subscribe);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
