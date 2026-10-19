/*!
 * TourKit — Chat nội bộ (widget nhúng CRM)
 *
 *   <script async src="https://<proxy>/internal-chat.js" data-session="{X-Session-Id}"></script>
 *
 * Tuỳ chọn data-*:
 *   data-position="br|bl"   góc màn hình (mặc định br)
 *   data-z-index="…"        lớp chồng
 *   data-order-url="…"      mẫu link mở đơn, "{id}" thay bằng mã đơn (hiện nút "Mở đơn")
 *   data-launcher="false"   ẩn nút nổi — trang CRM tự đặt nút riêng rồi gọi TkInternalChat.open()
 *
 * API cho trang CRM (mọi hàm open* trả Promise<{ roomId }>, hỏng thì reject Error):
 *   TkInternalChat.open() / close() / toggle()                 khung danh sách
 *   TkInternalChat.openEntity('order', 'DH-0142', { title, members: [12, 34] }, { in: 'dock' })
 *   TkInternalChat.openDirect(12)                              1-1 với một người
 *   TkInternalChat.openGroup({ title: 'Đoàn Nhật', members: [12, 34] })
 *   TkInternalChat.openRoom(roomId)                            phòng đã có
 *   TkInternalChat.renameRoom(roomId, 'Tên mới')               (không áp cho phòng 1-1)
 *   TkInternalChat.addMembers(roomId, [12, 34])
 *   TkInternalChat.removeMember(roomId, 12) / leaveRoom(roomId)
 *   TkInternalChat.closeRoom(roomId)
 *   TkInternalChat.getUnread()                                 tổng tin chưa đọc
 *   TkInternalChat.on('unread' | 'message' | 'room' | 'error' | 'ready', fn)  → hàm huỷ đăng ký
 *   TkInternalChat.setLauncherVisible(true|false)
 *   TkInternalChat.destroy()                                   gỡ hẳn (đăng xuất)
 *   opts.in: 'dock' (ô chat nhỏ nổi, mặc định, tối đa 2 ô) | 'panel' (mở trong khung danh sách)
 *
 * Script nạp async: gọi API trước khi nạp xong thì chờ sự kiện window "tk-internal-chat-ready".
 *
 * JS thuần, Shadow DOM tách CSS khỏi trang CRM. Chữ người dùng nhập vào DOM bằng textContent.
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

  // Cùng danh sách trắng với máy chủ (InternalAttachmentRules) — chặn sớm cho đỡ một lượt tải hỏng.
  const ALLOWED = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.txt', '.csv', '.zip', '.rar'];
  const IMAGES = ['.png', '.jpg', '.jpeg', '.gif', '.webp'];
  const MAX_BYTES = 15 * 1024 * 1024;
  const PAGE = 50;

  // ── Trạng thái ──────────────────────────────────────────────────────────
  const st = {
    launcher: scriptEl.getAttribute('data-launcher') !== 'false',
    open: false, view: 'list', roomId: null,      // khung danh sách + phòng đang mở trong khung
    docks: [], dockMin: new Set(),                 // ô chat nhỏ: mã phòng, mới nhất ở cuối
    conv: new Map(),                               // roomId → { msgs, hasOlder, loaded }
    rooms: [], me: null, users: new Map(),
    filter: 'all', search: '',
    newType: 'group', picked: new Set(), userSearch: '', newTitle: '',
    drafts: {}, pending: {}, sending: new Set(), dragKey: null, busy: false,
    settings: null, renameDraft: {}, addSearch: '', addPicked: new Set(),   // màn cấu hình phòng (key = 'panel-12' | 'dock-12')
    size: (() => { try { return localStorage.getItem('tk-chat-size') === 'large' ? 'large' : 'compact'; } catch { return 'compact'; } })(),
  };
  let errMsg = '';
  const vw = () => window.innerWidth;
  const isLarge = () => st.size === 'large' && vw() >= 760;
  // Màn rộng 2 ô, vừa 1 ô, hẹp thì không có ô nổi — mở thẳng trong khung.
  const maxDocks = () => (vw() >= 1100 ? 2 : vw() >= 760 ? 1 : 0);

  // ── Sự kiện cho trang CRM ───────────────────────────────────────────────
  const listeners = { unread: new Set(), message: new Set(), room: new Set(), error: new Set(), ready: new Set() };
  function emit(ev, data) { for (const fn of listeners[ev] || []) { try { fn(data); } catch (e) { console.error('[tk-chat] listener', e); } } }
  let lastUnread = -1;
  function emitUnread() { const n = totalUnread(); if (n !== lastUnread) { lastUnread = n; emit('unread', n); } }

  // ── Gọi API ─────────────────────────────────────────────────────────────
  let dead = false;
  async function api(method, path, body) {
    const opt = { method, headers: { 'X-Session-Id': session } };
    if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    const r = await fetch(API + path, opt);
    if (r.status === 401) { die(); throw new Error('Phiên đăng nhập đã hết hạn'); }
    if (r.status === 204) return null;
    const data = await r.json().catch(() => null);
    if (!r.ok) throw new Error((data && data.error) || ('HTTP ' + r.status));
    return data;
  }
  function die() { dead = true; host.remove(); if (es) { es.close(); es = null; } console.warn('[tk-chat] phiên hết hạn — widget tắt'); }

  // ── Dựng DOM ────────────────────────────────────────────────────────────
  const host = document.createElement('div');
  host.id = 'tk-internal-chat';
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
    .fab .n{position:absolute;top:-6px;${POS}:-6px;min-width:22px;height:22px;padding:0 6px;border-radius:6px;background:#fff;color:#1f3fbf;border:2px solid #1f3fbf;font-size:12px;font-weight:700;display:grid;place-items:center}
    .p{position:absolute;bottom:72px;${POS}:0;width:min(380px,calc(100vw - 32px));height:min(560px,calc(100vh - 120px));background:#fff;border:1px solid #e3e6ec;border-radius:6px;box-shadow:0 12px 32px rgba(17,21,28,.14);display:flex;flex-direction:column;overflow:hidden}
    .p.nofab{bottom:0}
    .p.lg{width:min(920px,calc(100vw - 32px));height:min(720px,calc(100vh - 120px));flex-direction:row}
    .pane{position:relative;display:flex;flex-direction:column;flex:1;min-width:0;min-height:0}
    .lg .pane.list{flex:0 0 300px;border-right:1px solid #e3e6ec}
    .lg .ms{padding:16px 20px}.lg .m{max-width:70%}.lg .img img{max-width:320px;max-height:320px}
    .blank{flex:1;display:grid;place-items:center;color:#5b6270;font-size:13px;background:#f5f6f8;text-align:center;padding:24px}
    .docks{position:absolute;bottom:0;display:flex;flex-direction:${POS === 'right' ? 'row-reverse' : 'row'};gap:12px;align-items:flex-end}
    .dk{width:328px;height:min(460px,calc(100vh - 48px));background:#fff;border:1px solid #e3e6ec;border-radius:6px;box-shadow:0 12px 32px rgba(17,21,28,.16);display:flex;flex-direction:column;overflow:hidden}
    .dk.min{height:auto}
    .dk .hd{min-height:48px;padding:6px 4px 6px 8px}
    .dk .ttl{flex:1;min-width:0;display:flex;align-items:center;gap:8px;border:0;background:transparent;color:#11151c;text-align:left;padding:4px;border-radius:6px}
    .dk .ttl:hover{background:#f5f6f8}
    .dk .ib{width:32px;height:32px}
    .hd{display:flex;align-items:center;gap:8px;padding:10px 8px 10px 12px;border-bottom:1px solid #e3e6ec;min-height:57px}
    .hd h2,.hd .h{margin:0;font-size:15px;font-weight:600;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .hd small{display:block;font-size:12px;color:#5b6270;font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .ib{width:36px;height:36px;border:0;border-radius:6px;background:transparent;color:#5b6270;display:grid;place-items:center;flex-shrink:0}
    .ib:hover{background:#eef1fc;color:#1f3fbf}
    .pri{height:36px;padding:0 12px;border:0;border-radius:6px;background:#1f3fbf;color:#fff;font-size:13px;font-weight:600;display:flex;align-items:center;gap:6px;flex-shrink:0}
    .pri:hover{background:#18329a}.pri:disabled{opacity:.55;cursor:default}
    .sec{height:30px;padding:0 10px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;color:#11151c;font-size:12px;text-decoration:none;display:inline-flex;align-items:center;flex-shrink:0}
    .sec:hover{background:#eef1fc}
    .tools{padding:12px 16px 8px;display:flex;flex-direction:column;gap:10px}
    .in{width:100%;height:38px;border:1px solid #d5d9e0;border-radius:6px;padding:0 10px;font:inherit;font-size:14px;color:#11151c;background:#fff}
    .chips{display:flex;gap:6px;flex-wrap:wrap}
    .chip{height:30px;padding:0 12px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;color:#11151c;font-size:13px}
    .chip[aria-pressed="true"]{border-color:#1f3fbf;background:#eef1fc;color:#1f3fbf;font-weight:600}
    ul{list-style:none;margin:0;padding:0 8px 8px;overflow:auto;flex:1}
    .it{width:100%;display:flex;gap:10px;align-items:center;padding:10px 8px;border:0;border-radius:6px;background:transparent;color:#11151c;text-align:left}
    .it:hover{background:#f5f6f8}.it.u{background:#eef1fc}
    .it[aria-current="true"]{outline:2px solid #1f3fbf;outline-offset:-2px}
    .av{width:40px;height:40px;flex-shrink:0;border-radius:6px;background:#e3e6ec;color:#11151c;display:grid;place-items:center;font-weight:600;font-size:13px}
    .av.c{background:#eef1fc;color:#1f3fbf}.av.s{width:32px;height:32px;font-size:12px}
    .tx{flex:1;min-width:0;display:flex;flex-direction:column;gap:2px}
    .r1,.r2{display:flex;gap:8px;align-items:center}
    .nm{font-weight:600;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .tm{font-size:12px;color:#5b6270;flex-shrink:0}.u .tm{color:#1f3fbf;font-weight:600}
    .pv{font-size:13px;color:#5b6270;flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.u .pv{color:#11151c;font-weight:500}
    .bd{min-width:20px;height:20px;padding:0 6px;border-radius:6px;background:#1f3fbf;color:#fff;font-size:12px;font-weight:600;display:grid;place-items:center;flex-shrink:0}
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
    .img{display:block;border-radius:6px;overflow:hidden;border:1px solid #e3e6ec;background:#fff;line-height:0}
    .img img{display:block;max-width:220px;max-height:220px;object-fit:cover}
    .fa{display:flex;align-items:center;gap:8px;padding:8px 10px;border-radius:6px;background:#fff;border:1px solid #e3e6ec;color:#11151c;text-decoration:none;max-width:240px}
    .fa:hover{border-color:#1f3fbf}
    .fa .fn{font-size:13px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .fa .fs{font-size:11px;color:#5b6270}
    .fa svg{color:#1f3fbf;flex-shrink:0}
    .cp{border-top:1px solid #e3e6ec;padding:10px 12px;display:flex;flex-direction:column;gap:6px}
    .cp .row{display:flex;gap:6px;align-items:flex-end}
    textarea{flex:1;min-width:0;resize:none;border:1px solid #d5d9e0;border-radius:6px;padding:8px 10px;font:inherit;font-size:14px;color:#11151c;min-height:44px;max-height:120px}
    .send{width:44px;height:44px;flex-shrink:0;border:0;border-radius:6px;background:#1f3fbf;color:#fff;display:grid;place-items:center}
    .send:hover{background:#18329a}.send:disabled{opacity:.55;cursor:default}
    .hint{font-size:11px;color:#5b6270}
    .dk .hint{display:none}
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
    .pend{display:flex;gap:6px;flex-wrap:wrap}
    .pc{position:relative;display:flex;align-items:center;gap:6px;height:44px;padding:0 28px 0 6px;border:1px solid #d5d9e0;border-radius:6px;background:#fff;max-width:200px;font-size:12px}
    .pc img{width:32px;height:32px;object-fit:cover;border-radius:4px}
    .pc .fn{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .pc.err{border-color:#c0392b;color:#8a1c12}
    .pc .x{position:absolute;top:2px;right:2px;width:22px;height:22px;border:0;background:transparent;color:#5b6270;display:grid;place-items:center}
    .pc .st{font-size:11px;color:#5b6270}
    .drop{position:absolute;inset:0;background:rgba(238,241,252,.92);border:2px dashed #1f3fbf;border-radius:6px;display:grid;place-items:center;color:#1f3fbf;font-weight:600;pointer-events:none;z-index:2}
    .sr{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
    @media (prefers-reduced-motion:reduce){*{transition:none!important;scroll-behavior:auto!important}}
  </style><div class="w"></div>`;
  const W = root.querySelector('.w');

  // ── Tiện ích ────────────────────────────────────────────────────────────
  function el(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'on') for (const [ev, fn] of Object.entries(v)) e.addEventListener(ev, fn);
      else e.setAttribute(k, v === true ? '' : String(v));
    }
    for (const k of kids.flat()) if (k != null && k !== false) e.append(k.nodeType ? k : document.createTextNode(String(k)));
    return e;
  }
  const ICONS = {
    chat: 'M4 5h16v11H9l-5 4z', plus: 'M12 5v14M5 12h14', x: 'M6 6l12 12M18 6L6 18', back: 'M15 6l-6 6 6 6',
    send: 'M4 12l16-8-6 16-2-7z', order: 'M7 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM9 8h6M9 12h6M9 16h4',
    company: 'M4 21V7l8-4 8 4v14M9 21v-6h6v6', group: 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM3 20c0-3 3-5 6-5s6 2 6 5M17 11.5a2.5 2.5 0 1 0 0-5M15 15c3 0 6 1.5 6 4.5',
    clip: 'M21 12.5l-8.5 8.5a5 5 0 0 1-7-7l9-9a3.5 3.5 0 0 1 5 5l-9 9a2 2 0 0 1-3-3l8-8',
    file: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5',
    max: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5', min: 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5',
    dash: 'M5 12h14',
    gear: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z', pop: 'M14 4h6v6M20 4l-8 8M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  };
  function icon(name, size) {
    const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('width', size || 18); s.setAttribute('height', size || 18); s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('fill', 'none'); s.setAttribute('stroke', 'currentColor'); s.setAttribute('stroke-width', '2');
    s.setAttribute('stroke-linecap', 'round'); s.setAttribute('stroke-linejoin', 'round'); s.setAttribute('aria-hidden', 'true');
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path'); p.setAttribute('d', ICONS[name]); s.append(p);
    return s;
  }
  const extOf = (n) => { const i = (n || '').lastIndexOf('.'); return i < 0 ? '' : n.slice(i).toLowerCase(); };
  const fmtSize = (b) => b >= 1048576 ? (b / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  const safeUrl = (u) => typeof u === 'string' && /^https?:\/\//i.test(u) ? u : null;
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
  const roomOf = (id) => st.rooms.find((x) => x.id === id) || { id, entityType: 'group', members: [] };
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
    return el('span', { class: cls + (r.entityType === 'group' ? '' : ' c'), 'aria-hidden': 'true' }, icon(ic, small ? 18 : 20));
  }
  const totalUnread = () => st.rooms.reduce((s, r) => s + (r.unreadCount || 0), 0);
  const convOf = (id) => { let c = st.conv.get(id); if (!c) { c = { msgs: [], hasOlder: false, loaded: false }; st.conv.set(id, c); } return c; };
  const panelRoomVisible = (id) => st.open && st.view === 'room' && st.roomId === id;
  const dockVisible = (id) => st.docks.includes(id) && !st.dockMin.has(id);
  const isVisible = (id) => panelRoomVisible(id) || dockVisible(id);

  // ── Vẽ ──────────────────────────────────────────────────────────────────
  // Vẽ lại cả widget mỗi lần đổi — phải trả lại focus, con trỏ và vị trí cuộn của TỪNG khung tin,
  // không thì người đang gõ bị văng khỏi ô ngay khi có tin mới tới.
  function render() {
    if (dead) return;
    const scroll = {};
    for (const ms of W.querySelectorAll('.ms')) {
      scroll[ms.dataset.k] = { bottom: ms.scrollHeight - ms.scrollTop - ms.clientHeight < 40, top: ms.scrollTop };
    }
    const act = root.activeElement;
    const actId = act && act.id;
    const sel = act && typeof act.selectionStart === 'number' ? [act.selectionStart, act.selectionEnd] : null;
    W.replaceChildren();

    const lg = isLarge();
    let panelW = 0;
    if (st.open) {
      const cls = 'p' + (lg ? ' lg' : '') + (st.launcher ? '' : ' nofab');
      if (lg) {
        const right = st.view === 'room' && st.roomId ? roomPane(st.roomId, 'panel') : st.view === 'new' ? viewNew()
          : el('div', { class: 'blank' }, 'Chọn một cuộc trò chuyện bên trái, hoặc bấm "Trò chuyện mới".');
        W.append(el('section', { class: cls, 'aria-label': 'Chat nội bộ' }, viewList(), right));
        panelW = Math.min(920, vw() - 32);
      } else {
        const one = st.view === 'room' && st.roomId ? roomPane(st.roomId, 'panel') : st.view === 'new' ? viewNew() : viewList();
        W.append(el('section', { class: cls, 'aria-label': 'Chat nội bộ' }, one));
        panelW = Math.min(380, vw() - 32);
      }
    }
    // Ô chat nhỏ nằm cạnh nút nổi; khung danh sách đang mở thì xếp sang bên cạnh khung. Số ô hiện
    // tính theo CHỖ TRỐNG thật (khung lớn đang mở chiếm gần hết màn) — ô không vừa thì ẩn tạm,
    // vẫn giữ trong danh sách, thu khung lại là hiện lại.
    const off = st.open ? panelW + 12 : (st.launcher ? 72 : 0);
    const fit = Math.max(0, Math.floor((vw() - 24 - 16 - off) / (328 + 12)));
    const docks = st.docks.slice(-Math.min(maxDocks(), fit));
    if (docks.length) {
      W.append(el('div', { class: 'docks', style: `${POS}:${off}px` }, docks.slice().reverse().map((id) => dockWin(id))));
    }
    if (st.launcher) {
      const n = totalUnread();
      W.append(el('button', {
        class: 'fab', type: 'button', 'aria-expanded': String(st.open),
        'aria-label': st.open ? 'Thu nhỏ chat nội bộ' : ('Mở chat nội bộ' + (n ? ', ' + n + ' tin chưa đọc' : '')),
        on: { click: () => (st.open ? closePanel() : openPanel()) },
      }, icon(st.open ? 'x' : 'chat', 26), n ? el('span', { class: 'n', 'aria-hidden': 'true' }, n > 99 ? '99+' : n) : null));
    }

    for (const ms of W.querySelectorAll('.ms')) {
      const s = scroll[ms.dataset.k];
      // Khung tin mở lần đầu thì xuống tin mới nhất; màn cấu hình (data-top) thì đứng ở đầu.
      if (!s) ms.scrollTop = ms.dataset.top ? 0 : ms.scrollHeight;
      else ms.scrollTop = s.bottom && !ms.dataset.top ? ms.scrollHeight : s.top;
    }
    if (actId) {
      const n = root.getElementById(actId);
      if (n) { n.focus(); if (sel) { try { n.setSelectionRange(sel[0], sel[1]); } catch { /* ô không có con trỏ */ } } }
    }
    emitUnread();
  }

  function liveInput(set) {
    // Bỏ qua lúc bộ gõ đang ghép chữ (composition) — vẽ lại giữa chừng là phá dấu tiếng Việt.
    return {
      input: (e) => { set(e.target.value); if (!e.isComposing) render(); },
      compositionend: (e) => { set(e.target.value); render(); },
    };
  }
  function errBox() { return errMsg ? el('div', { class: 'err', role: 'alert' }, errMsg) : null; }
  function closeBtn() { return el('button', { class: 'ib', type: 'button', 'aria-label': 'Thu nhỏ', on: { click: closePanel } }, icon('x')); }
  function backBtn() { return el('button', { class: 'ib', type: 'button', 'aria-label': 'Về danh sách', on: { click: () => { st.view = 'list'; st.roomId = null; errMsg = ''; loadRooms(); render(); } } }, icon('back')); }
  function sizeBtn() {
    const lg = st.size === 'large';
    return el('button', { class: 'ib', type: 'button', 'aria-label': lg ? 'Thu về khung nhỏ' : 'Phóng to', title: lg ? 'Thu về khung nhỏ' : 'Phóng to',
      on: { click: () => { st.size = lg ? 'compact' : 'large'; try { localStorage.setItem('tk-chat-size', st.size); } catch { /* chế độ riêng tư */ } render(); } } },
      icon(lg ? 'min' : 'max'));
  }

  function viewList() {
    const lg = isLarge();
    const q = st.search.trim().toLowerCase();
    const rooms = st.rooms.filter((r) => {
      if (st.filter === 'unread' && !r.unreadCount) return false;
      if (st.filter === 'entity' && !(r.entityType === 'order' || r.entityType === 'company')) return false;
      return !q || roomName(r).toLowerCase().includes(q);
    });
    const unreadRooms = st.rooms.filter((r) => r.unreadCount).length;
    const chip = (key, label) => el('button', { class: 'chip', type: 'button', 'aria-pressed': String(st.filter === key), on: { click: () => { st.filter = key; render(); } } }, label);
    const search = el('input', { class: 'in', type: 'search', id: 'tk-tim', placeholder: 'Tìm người hoặc phòng', on: liveInput((v) => { st.search = v; }) });
    search.value = st.search;
    return el('div', { class: 'pane list' },
      el('div', { class: 'hd' },
        el('h2', {}, 'Chat nội bộ'),
        el('button', { class: 'pri', type: 'button', 'aria-label': 'Trò chuyện mới', on: { click: () => openNew() } }, icon('plus', 16), lg ? null : 'Trò chuyện mới'),
        vw() >= 760 ? sizeBtn() : null, closeBtn()),
      el('div', { class: 'tools' },
        el('label', { for: 'tk-tim', class: 'sr' }, 'Tìm người hoặc phòng'), search,
        el('div', { class: 'chips' }, chip('all', 'Tất cả'), chip('unread', 'Chưa đọc' + (unreadRooms ? ' · ' + unreadRooms : '')), chip('entity', 'Theo đơn'))),
      st.view === 'room' || st.view === 'new' ? null : errBox(),
      rooms.length
        ? el('ul', {}, rooms.map((r) => el('li', {}, el('button', { class: 'it' + (r.unreadCount ? ' u' : ''), type: 'button',
            'aria-current': lg && st.view === 'room' && st.roomId === r.id ? 'true' : null,
            on: { click: () => showInPanel(r.id) } },
            roomAvatar(r),
            el('span', { class: 'tx' },
              el('span', { class: 'r1' }, el('span', { class: 'nm' }, roomName(r)), el('span', { class: 'tm' }, shortTime(r.lastActivityAt))),
              el('span', { class: 'r2' }, el('span', { class: 'pv' }, r.lastPreview || 'Chưa có tin nhắn'),
                r.unreadCount ? el('span', { class: 'bd' }, r.unreadCount >= 100 ? '99+' : r.unreadCount) : null))))))
        : el('div', { class: 'empty' }, st.rooms.length ? 'Không có phòng nào khớp.' : 'Chưa có cuộc trò chuyện nào. Bấm "Trò chuyện mới" để bắt đầu.'));
  }

  /// Danh sách tin + ô soạn của một phòng — dùng chung cho khung lớn và ô chat nhỏ.
  /// k = 'panel' | 'dock' làm hậu tố id để hai nơi cùng mở một phòng không trùng id phần tử.
  function gearBtn(id, key) {
    const on = st.settings === key;
    return el('button', { class: 'ib', type: 'button', 'aria-pressed': String(on), 'aria-label': on ? 'Đóng cấu hình phòng' : 'Cấu hình phòng: tên, thành viên',
      title: 'Cấu hình phòng', on: { click: () => toggleSettings(id, key) } }, icon('gear', 16));
  }
  function toggleSettings(id, key) {
    if (st.settings === key) { st.settings = null; }
    else { st.settings = key; st.addSearch = ''; st.addPicked = new Set(); st.renameDraft[id] = roomOf(id).title || ''; if (!st.users.size) loadUsers().then(render); }
    errMsg = ''; st.errRoom = null; render();
  }

  /// Cấu hình phòng: đổi tên, xem/bỏ thành viên, thêm người, rời phòng. Phòng 1-1 không có màn này.
  function settingsBody(id, key) {
    const r = roomOf(id);
    const members = (r.members || []).map((m) => m.userId);
    const nameId = 'tk-ten-phong-' + key, findId = 'tk-them-' + key;
    const name = el('input', { class: 'in', id: nameId, type: 'text', maxlength: '120', placeholder: 'Tên phòng',
      on: { input: (e) => { st.renameDraft[id] = e.target.value; }, keydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); doRename(id); } } } });
    name.value = st.renameDraft[id] ?? (r.title || '');
    const q = st.addSearch.trim().toLowerCase();
    const candidates = [...st.users.entries()].filter(([uid, n]) => !members.includes(uid) && (!q || n.toLowerCase().includes(q)));
    const find = el('input', { class: 'in', id: findId, type: 'search', placeholder: 'Tìm nhân viên để thêm', on: liveInput((v) => { st.addSearch = v; }) });
    find.value = st.addSearch;
    const n = st.addPicked.size;
    return [
      el('div', { class: 'ms', 'data-k': key + '-cfg', 'data-top': '1', style: 'background:#fff;gap:14px' },
        el('div', {},
          el('label', { class: 'lb', for: nameId }, r.entityType === 'order' ? 'Tên hiển thị (đi sau mã đơn)' : 'Tên phòng'),
          el('div', { style: 'display:flex;gap:6px' }, name,
            el('button', { class: 'pri', type: 'button', disabled: st.busy, on: { click: () => doRename(id) } }, 'Lưu'))),
        el('div', {},
          el('span', { class: 'lb' }, 'Thành viên · ' + members.length),
          el('ul', { style: 'padding:0;overflow:visible' }, members.map((uid) => el('li', { class: 'opt', style: 'cursor:default' },
            el('span', { class: 'av s', 'aria-hidden': 'true' }, initials(userName(uid))),
            el('span', { style: 'flex:1;min-width:0' }, userName(uid), uid === st.me ? el('span', { class: 'tm' }, ' · Bạn') : null),
            uid === st.me
              ? el('button', { class: 'sec', type: 'button', on: { click: () => doRemove(id, uid) } }, 'Rời phòng')
              : el('button', { class: 'sec', type: 'button', 'aria-label': 'Bỏ ' + userName(uid) + ' khỏi phòng', on: { click: () => doRemove(id, uid) } }, 'Bỏ'))))),
        el('div', {},
          el('label', { class: 'lb', for: findId }, 'Thêm người'), find,
          candidates.length
            ? el('ul', { style: 'padding:4px 0 0;overflow:visible' }, candidates.slice(0, 50).map(([uid, nm]) => el('li', {},
                el('label', { class: 'opt' },
                  el('input', { type: 'checkbox', checked: st.addPicked.has(uid), on: { change: () => { st.addPicked.has(uid) ? st.addPicked.delete(uid) : st.addPicked.add(uid); render(); } } }),
                  el('span', { class: 'av s', 'aria-hidden': 'true' }, initials(nm)), el('span', {}, nm)))))
            : el('div', { class: 'hint', style: 'padding:8px 0' }, st.users.size ? 'Không còn ai để thêm.' : 'Đang tải danh sách nhân viên…'))),
      st.errRoom === id ? errBox() : null,
      el('div', { class: 'ft' },
        el('button', { class: 'pri', type: 'button', disabled: !n || st.busy, on: { click: () => doAdd(id) } }, n ? 'Thêm ' + n + ' người' : 'Chọn người để thêm'),
        el('button', { class: 'sec', type: 'button', style: 'justify-content:center;height:36px', on: { click: () => toggleSettings(id, key) } }, 'Xong'))];
  }

  function roomBody(id, k) {
    const r = roomOf(id);
    const c = convOf(id);
    const key = k + '-' + id;
    if (st.settings === key && r.entityType !== 'direct') return settingsBody(id, key);
    const list = el('div', { class: 'ms', 'data-k': key, 'aria-live': 'polite' });
    if (c.hasOlder) list.append(el('button', { class: 'sec', type: 'button', style: 'align-self:center', on: { click: () => loadOlder(id, key) } }, 'Xem tin cũ hơn'));
    if (c.loaded && !c.msgs.length) list.append(el('div', { class: 'empty' }, 'Chưa có tin nhắn. Gửi lời chào đầu tiên.'));
    let prevDay = null;
    for (const m of c.msgs) {
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
      const recall = mine && !deleted ? el('button', { class: 'rc', type: 'button', on: { click: () => recallMsg(id, m) } }, 'Thu hồi') : null;
      list.append(el('div', { class: 'm' + (mine ? ' me' : '') },
        !mine && r.entityType !== 'direct' ? el('span', { class: 'who' }, userName(m.senderUserId, m.senderUsername)) : null,
        attEl ? el('div', { class: 'row' }, bubble ? null : recall, attEl) : null,
        bubble ? el('div', { class: 'row' }, recall, bubble) : null,
        el('span', { class: 't' }, hm(m.createdUtc))));
    }

    const taId = 'tk-soan-' + key, fileId = 'tk-tep-' + key;
    const ta = el('textarea', { id: taId, rows: '2', maxlength: '4000', placeholder: r.entityType === 'direct' ? 'Nhắn cho ' + roomName(r) : 'Nhắn cho cả phòng',
      on: {
        input: (e) => { st.drafts[id] = e.target.value; },
        keydown: (e) => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(id, taId); } },
        // Dán ảnh chụp màn hình hoặc tệp đã copy trong Explorer/Finder. Dán chữ thì để mặc định.
        paste: (e) => { const files = [...(e.clipboardData?.files || [])]; if (files.length) { e.preventDefault(); addFiles(id, files); } },
      } });
    ta.value = st.drafts[id] || '';
    const picker = el('input', { type: 'file', multiple: true, class: 'sr', id: fileId, tabindex: '-1', 'aria-hidden': 'true',
      accept: ALLOWED.join(','), on: { change: (e) => { addFiles(id, [...e.target.files]); e.target.value = ''; } } });
    const pend = st.pending[id] || [];
    const uploading = pend.some((p) => p.status === 'up');
    const pendEl = pend.length ? el('div', { class: 'pend' }, pend.map((p) => el('div', { class: 'pc' + (p.status === 'err' ? ' err' : '') },
      p.preview ? el('img', { src: p.preview, alt: '' }) : icon('file', 20),
      el('span', { class: 'tx' }, el('span', { class: 'fn' }, p.name),
        el('span', { class: 'st' }, p.status === 'up' ? 'Đang tải lên…' : p.status === 'err' ? p.error : fmtSize(p.size))),
      el('button', { class: 'x', type: 'button', 'aria-label': 'Bỏ ' + p.name, on: { click: () => removePending(id, p.key) } }, icon('x', 12))))) : null;

    return [list, st.errRoom === id ? errBox() : null,
      el('div', { class: 'cp' },
        pendEl,
        el('div', { class: 'row' },
          el('button', { class: 'ib', type: 'button', 'aria-label': 'Đính kèm tệp', on: { click: () => picker.click() } }, icon('clip')),
          picker,
          el('label', { for: taId, class: 'sr' }, 'Soạn tin nhắn'), ta,
          el('button', { class: 'send', type: 'button', 'aria-label': uploading ? 'Đang tải tệp lên' : 'Gửi', disabled: uploading || st.sending.has(id),
            on: { click: () => send(id, taId) } }, icon('send'))),
        el('span', { class: 'hint' }, 'Enter để gửi · Shift + Enter xuống dòng · Dán hoặc kéo thả tệp vào đây')),
      st.dragKey === key ? el('div', { class: 'drop' }, 'Thả tệp để gửi vào phòng') : null];
  }
  function dropHandlers(id, key, getEl) {
    return {
      dragover: (e) => { if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); if (st.dragKey !== key) { st.dragKey = key; render(); } } },
      dragleave: (e) => { const me = getEl(); if (me && (e.target === me || !me.contains(e.relatedTarget))) { st.dragKey = null; render(); } },
      drop: (e) => { e.preventDefault(); st.dragKey = null; addFiles(id, [...(e.dataTransfer?.files || [])]); },
    };
  }
  function orderLink(r) {
    return r.entityType === 'order' && ORDER_URL
      ? el('a', { class: 'sec', href: ORDER_URL.replace('{id}', encodeURIComponent(r.entityId)), target: '_top' }, 'Mở đơn') : null;
  }

  function roomPane(id, k) {
    const r = roomOf(id);
    const lg = isLarge();
    const sub = r.entityType === 'direct' ? null : (r.members || []).length + ' thành viên';
    let pane = null;
    pane = el('div', { class: 'pane', role: 'region', 'aria-label': 'Trò chuyện: ' + roomName(r), on: dropHandlers(id, k + '-' + id, () => pane) },
      el('div', { class: 'hd' }, lg ? null : backBtn(), roomAvatar(r, true),
        el('h2', {}, roomName(r), sub ? el('small', {}, sub) : null), orderLink(r),
        r.entityType === 'direct' ? null : gearBtn(id, k + '-' + id),
        maxDocks() ? el('button', { class: 'ib', type: 'button', 'aria-label': 'Tách ra ô chat nhỏ', title: 'Tách ra ô chat nhỏ',
          on: { click: () => { const rid = id; st.view = 'list'; st.roomId = null; if (!lg) closePanel(); addDock(rid); } } }, icon('pop')) : null,
        lg || vw() < 760 ? null : sizeBtn(), lg ? null : closeBtn()),
      roomBody(id, k));
    return pane;
  }

  function dockWin(id) {
    const r = roomOf(id);
    const min = st.dockMin.has(id);
    const unread = r.unreadCount || 0;
    let win = null;
    const toggleMin = () => { if (min) { st.dockMin.delete(id); markRead(id); } else st.dockMin.add(id); render(); };
    win = el('section', { class: 'dk' + (min ? ' min' : ''), 'aria-label': 'Ô chat: ' + roomName(r), on: min ? null : dropHandlers(id, 'dock-' + id, () => win) },
      el('div', { class: 'hd' },
        el('button', { class: 'ttl', type: 'button', 'aria-expanded': String(!min), 'aria-label': (min ? 'Mở ô ' : 'Thu gọn ô ') + roomName(r), on: { click: toggleMin } },
          roomAvatar(r, true), el('span', { class: 'h' }, roomName(r)), min && unread ? el('span', { class: 'bd' }, unread > 99 ? '99+' : unread) : null),
        min ? null : orderLink(r),
        min || r.entityType === 'direct' ? null : gearBtn(id, 'dock-' + id),
        min ? null : el('button', { class: 'ib', type: 'button', 'aria-label': 'Mở trong khung lớn', title: 'Mở trong khung lớn', on: { click: () => { closeDock(id); showInPanel(id); } } }, icon('max', 16)),
        el('button', { class: 'ib', type: 'button', 'aria-label': min ? 'Mở ô' : 'Thu gọn', title: min ? 'Mở ô' : 'Thu gọn', on: { click: toggleMin } }, icon(min ? 'max' : 'dash', 16)),
        el('button', { class: 'ib', type: 'button', 'aria-label': 'Đóng ô chat', title: 'Đóng', on: { click: () => closeDock(id) } }, icon('x', 16))),
      min ? null : el('div', { class: 'pane' }, roomBody(id, 'dock')));
    return win;
  }

  function viewNew() {
    const q = st.userSearch.trim().toLowerCase();
    const people = [...st.users.entries()].filter(([id, n]) => id !== st.me && (!q || n.toLowerCase().includes(q)));
    const direct = st.newType === 'direct';
    const pickToggle = (id) => {
      if (direct) st.picked = new Set([id]);
      else if (st.picked.has(id)) st.picked.delete(id); else st.picked.add(id);
      render();
    };
    const title = el('input', { class: 'in', id: 'tk-ten', type: 'text', maxlength: '120', placeholder: 'Ví dụ: Đoàn Nhật tháng 11',
      on: { input: (e) => { st.newTitle = e.target.value; } } });
    title.value = st.newTitle;
    const us = el('input', { class: 'in', id: 'tk-tim-nv', type: 'search', placeholder: 'Tìm theo tên nhân viên', on: liveInput((v) => { st.userSearch = v; }) });
    us.value = st.userSearch;
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
        el('button', { class: 'pri', type: 'button', disabled: !can || st.busy, on: { click: createFromForm } }, 'Bắt đầu trò chuyện')));
  }

  // ── Dữ liệu ─────────────────────────────────────────────────────────────
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
      // Phòng đang hiện trên màn hình thì không tính là chưa đọc (đã/ sắp đánh dấu đọc).
      for (const r of st.rooms) if (isVisible(r.id)) r.unreadCount = 0;
      render();
    } catch (e) { if (!dead) { errMsg = 'Không tải được danh sách phòng.'; render(); } }
  }
  let refreshTimer = null;
  const refreshRoomsSoon = () => { clearTimeout(refreshTimer); refreshTimer = setTimeout(loadRooms, 800); };

  async function ensureConv(id) {
    const c = convOf(id);
    if (c.loaded) return fetchNewer(id);
    try {
      const page = await api('GET', `/rooms/${id}/messages?limit=${PAGE}`);
      c.msgs = (page || []).slice().reverse();
      c.hasOlder = (page || []).length === PAGE;
      c.loaded = true;
      render();
    } catch (e) { if (!dead) { errMsg = e.message; st.errRoom = id; render(); } throw e; }
  }
  async function loadOlder(id, key) {
    const c = convOf(id); const first = c.msgs[0]; if (!first) return;
    const ms = W.querySelector(`.ms[data-k="${key}"]`); const before = ms ? ms.scrollHeight - ms.scrollTop : 0;
    try {
      const page = await api('GET', `/rooms/${id}/messages?limit=${PAGE}&beforeUtc=${encodeURIComponent(first.createdUtc)}&beforeId=${first.id}`);
      c.msgs = (page || []).slice().reverse().concat(c.msgs);
      c.hasOlder = (page || []).length === PAGE;
      render();
      const ms2 = W.querySelector(`.ms[data-k="${key}"]`); if (ms2) ms2.scrollTop = ms2.scrollHeight - before;
    } catch (e) { if (!dead) { errMsg = e.message; st.errRoom = id; render(); } }
  }
  function mergeMsgs(id, list) {
    const c = convOf(id);
    const seen = new Set(c.msgs.map((m) => m.id));
    for (const m of list) if (!seen.has(m.id)) { c.msgs.push(m); seen.add(m.id); }
    c.msgs.sort((a, b) => (a.createdUtc < b.createdUtc ? -1 : a.createdUtc > b.createdUtc ? 1 : a.id - b.id));
  }
  async function fetchNewer(id) {
    const c = convOf(id); if (!c.loaded) return;
    const last = c.msgs.length ? c.msgs[c.msgs.length - 1].id : 0;
    try {
      const page = await api('GET', `/rooms/${id}/messages?afterId=${last}&limit=100`);
      if (page && page.length) { mergeMsgs(id, page); render(); if (isVisible(id)) markRead(id); }
    } catch { /* lần sự kiện sau sẽ bù */ }
  }
  async function markRead(id) {
    try { await api('POST', `/rooms/${id}/read`); } catch { return; }
    const r = st.rooms.find((x) => x.id === id); if (r && r.unreadCount) { r.unreadCount = 0; render(); }
  }

  // ── Mở / đóng ───────────────────────────────────────────────────────────
  function openPanel() { st.open = true; loadRooms(); render(); }
  function closePanel() { st.open = false; errMsg = ''; render(); }
  function showInPanel(id) {
    errMsg = ''; st.errRoom = null;
    st.open = true; st.view = 'room'; st.roomId = id;
    render();
    ensureConv(id).then(() => markRead(id)).catch(() => {});
  }
  function addDock(id) {
    if (!maxDocks()) return showInPanel(id);
    st.docks = st.docks.filter((x) => x !== id);
    st.docks.push(id);
    while (st.docks.length > maxDocks()) st.docks.shift();   // ô cũ nhất nhường chỗ
    st.dockMin.delete(id);
    render();
    ensureConv(id).then(() => markRead(id)).catch(() => {});
    setTimeout(() => root.getElementById('tk-soan-dock-' + id)?.focus(), 0);
  }
  function closeDock(id) { st.docks = st.docks.filter((x) => x !== id); st.dockMin.delete(id); render(); }
  function openNew() {
    st.open = true; st.view = 'new'; st.newType = 'group'; st.picked = new Set(); st.userSearch = ''; st.newTitle = ''; errMsg = '';
    render(); if (!st.users.size) loadUsers().then(render);
  }

  /// Tạo/lấy phòng rồi hiện ra. Dùng chung cho form tạo trò chuyện và mọi hàm API công khai.
  async function createAndShow(payload, where) {
    const r = await api('POST', '/rooms', payload);
    await loadRooms();
    if (where === 'panel') showInPanel(r.id); else addDock(r.id);
    return { roomId: r.id };
  }
  async function createFromForm() {
    if (st.busy) return;
    st.busy = true; errMsg = ''; render();
    try {
      const body = st.newType === 'direct'
        ? { entityType: 'direct', memberUserIds: [...st.picked] }
        : { entityType: 'group', title: (st.newTitle || '').trim() || null, memberUserIds: [...st.picked] };
      st.busy = false;
      await createAndShow(body, 'panel');
    } catch (e) { st.busy = false; if (!dead) { errMsg = e.message; render(); } }
  }

  // ── Gửi + tệp đính kèm ──────────────────────────────────────────────────
  let pendSeq = 0;
  function addFiles(roomId, files) {
    if (!files.length) return;
    const list = st.pending[roomId] || (st.pending[roomId] = []);
    for (const f of files.slice(0, 10)) {
      // Ảnh dán từ clipboard thường tên "image.png" — đặt tên theo giờ cho dễ nhận.
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
      if (r.status === 401) { die(); return; }
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
    mergeMsgs(roomId, [m]);
    const r = st.rooms.find((x) => x.id === roomId);
    if (r) { r.lastPreview = body ? body.slice(0, 200) : (att.kind === 'anh' ? '[Ảnh] ' : '[Tệp] ') + att.name; r.lastActivityAt = m.createdUtc; }
  }
  async function send(roomId, taId) {
    if (st.sending.has(roomId)) return;
    const body = (st.drafts[roomId] || '').trim();
    const pend = st.pending[roomId] || [];
    if (pend.some((p) => p.status === 'up')) return;   // nút Gửi đã khoá; Enter cũng phải chờ
    const ready = pend.filter((p) => p.status === 'ok');
    if (!body && !ready.length) return;
    st.sending.add(roomId); errMsg = ''; st.errRoom = null;
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
    } catch (e) { if (!dead) { errMsg = 'Chưa gửi được: ' + e.message; st.errRoom = roomId; } }   // nháp + tệp chưa gửi còn nguyên
    finally {
      st.sending.delete(roomId);
      render();
      for (const ms of W.querySelectorAll(`.ms[data-k$="-${roomId}"]`)) ms.scrollTop = ms.scrollHeight;
      root.getElementById(taId)?.focus();
    }
  }
  async function recallMsg(roomId, m) {
    try { await api('DELETE', `/messages/${m.id}`); m.deletedUtc = new Date().toISOString(); m.body = null; m.attachment = null; render(); }
    catch (e) { if (!dead) { errMsg = 'Không thu hồi được: ' + e.message; st.errRoom = roomId; render(); } }
  }

  // ── Cấu hình phòng ──────────────────────────────────────────────────────
  async function cfgCall(id, fn) {
    if (st.busy) return;
    st.busy = true; errMsg = ''; st.errRoom = null; render();
    try { await fn(); await loadRooms(); }
    catch (e) { if (!dead) { errMsg = e.message; st.errRoom = id; } throw e; }
    finally { st.busy = false; render(); }
  }
  const doRename = (id) => cfgCall(id, () => api('PATCH', `/rooms/${id}`, { title: (st.renameDraft[id] || '').trim() || null })).catch(() => {});
  const doAdd = (id) => cfgCall(id, async () => { await api('POST', `/rooms/${id}/members`, { memberUserIds: [...st.addPicked] }); st.addPicked = new Set(); st.addSearch = ''; }).catch(() => {});
  const doRemove = (id, uid) => cfgCall(id, async () => {
    await api('DELETE', `/rooms/${id}/members/${uid}`);
    if (uid === st.me) leftRoom(id);
  }).catch(() => {});
  /// Mình không còn trong phòng (tự rời hoặc bị bỏ): đóng mọi chỗ đang hiện phòng đó.
  function leftRoom(id) {
    closeDock(id);
    if (st.roomId === id) { st.view = 'list'; st.roomId = null; }
    if (st.settings && st.settings.endsWith('-' + id)) st.settings = null;
    st.conv.delete(id);
    render();
  }

  // ── Real-time ───────────────────────────────────────────────────────────
  // EventSource không gửi được header nên phiên đi ?sessionId=. Trình duyệt tự nối lại; mỗi lần
  // nối lại thì bù những gì có thể đã rơi trong lúc đứt.
  let es = null, hadError = false;
  function subscribe() {
    if (dead || es) return;
    es = new EventSource(API + '/events?sessionId=' + encodeURIComponent(session));
    es.onmessage = (ev) => {
      let d; try { d = JSON.parse(ev.data); } catch { return; }
      if (d.messageId == null) {
        // Phòng đổi cấu hình (tên, thành viên). Tải lại ngay; mình bị bỏ ra thì đóng phòng đó.
        loadRooms().then(() => { if (!st.rooms.some((r) => r.id === d.roomId)) leftRoom(d.roomId); });
        emit('room', { roomId: d.roomId });
        return;
      }
      if (st.conv.get(d.roomId)?.loaded) fetchNewer(d.roomId);
      refreshRoomsSoon();
      emit('message', { roomId: d.roomId, messageId: d.messageId });
    };
    es.onopen = () => { if (hadError) { hadError = false; loadRooms(); for (const [id, c] of st.conv) if (c.loaded) fetchNewer(id); } };
    es.onerror = () => { hadError = true; };
  }

  // ── API công khai cho trang CRM ─────────────────────────────────────────
  function fail(e) { const msg = e && e.message ? e.message : String(e); emit('error', { message: msg }); return Promise.reject(e instanceof Error ? e : new Error(msg)); }
  const ids = (xs) => [...new Set((xs || []).map(Number).filter((n) => Number.isInteger(n) && n > 0))];
  const whereOf = (opts) => (opts && opts.in === 'panel' ? 'panel' : 'dock');
  window.TkInternalChat = {
    open() { openPanel(); },
    close() { closePanel(); },
    toggle() { st.open ? closePanel() : openPanel(); },
    /** Mở phòng theo đơn hàng / công ty, kèm sẵn người tham gia. Gọi lại nhiều lần vẫn ra CÙNG một phòng. */
    openEntity(type, entityId, info, opts) {
      if (!(type === 'order' || type === 'company') || entityId == null || String(entityId).trim() === '')
        return fail(new Error('openEntity: cần type "order" | "company" và mã'));
      const i = typeof info === 'string' ? { title: info } : (info || {});
      return createAndShow({ entityType: type, entityId: String(entityId).trim(), title: i.title || null, memberUserIds: ids(i.members) }, whereOf(opts)).catch(fail);
    },
    /** Nhắn 1-1 với một nhân viên (mã người CRM). */
    openDirect(userId, opts) {
      const u = ids([userId]);
      if (!u.length) return fail(new Error('openDirect: cần mã người hợp lệ'));
      return createAndShow({ entityType: 'direct', memberUserIds: u }, whereOf(opts)).catch(fail);
    },
    /** Tạo nhóm mới (mỗi lần gọi là một nhóm mới). */
    openGroup(info, opts) {
      const i = info || {};
      const m = ids(i.members);
      if (!m.length) return fail(new Error('openGroup: cần ít nhất một người trong members'));
      return createAndShow({ entityType: 'group', title: i.title || null, memberUserIds: m }, whereOf(opts)).catch(fail);
    },
    /** Mở một phòng đã có theo mã. */
    openRoom(roomId, opts) {
      const id = Number(roomId);
      if (!Number.isInteger(id) || id <= 0) return fail(new Error('openRoom: mã phòng không hợp lệ'));
      if (whereOf(opts) === 'panel') showInPanel(id); else addDock(id);
      return Promise.resolve({ roomId: id });
    },
    /** Đổi tên phòng (không áp cho phòng 1-1). title rỗng = bỏ tên. */
    renameRoom(roomId, title) {
      return api('PATCH', `/rooms/${Number(roomId)}`, { title: title == null ? null : String(title) }).then(loadRooms).catch(fail);
    },
    /** Thêm người vào phòng (mã người CRM). */
    addMembers(roomId, members) {
      const m = ids(members);
      if (!m.length) return fail(new Error('addMembers: cần ít nhất một mã người'));
      return api('POST', `/rooms/${Number(roomId)}/members`, { memberUserIds: m }).then(loadRooms).catch(fail);
    },
    /** Bỏ một người khỏi phòng. Bỏ chính mình = rời phòng. */
    removeMember(roomId, userId) {
      const id = Number(roomId), u = Number(userId);
      return api('DELETE', `/rooms/${id}/members/${u}`).then(() => { if (u === st.me) leftRoom(id); return loadRooms(); }).catch(fail);
    },
    leaveRoom(roomId) { return st.me == null ? fail(new Error('leaveRoom: chưa biết mã người của bạn')) : this.removeMember(roomId, st.me); },
    closeRoom(roomId) { const id = Number(roomId); closeDock(id); if (st.roomId === id) { st.view = 'list'; st.roomId = null; render(); } },
    getUnread() { return totalUnread(); },
    on(ev, fn) {
      if (!listeners[ev] || typeof fn !== 'function') throw new Error('on: sự kiện không có — unread | message | room | error | ready');
      listeners[ev].add(fn);
      // Người nghe đến sau vẫn phải biết số hiện tại — không thì badge riêng của CRM trống cho tới
      // lần đổi kế tiếp, mà có khi cả buổi không đổi.
      if (ev === 'unread' && lastUnread >= 0) queueMicrotask(() => { try { fn(totalUnread()); } catch (e) { console.error('[tk-chat] listener', e); } });
      return () => listeners[ev].delete(fn);
    },
    setLauncherVisible(v) { st.launcher = !!v; render(); },
    /** Gỡ hẳn widget (đăng xuất / đổi tài khoản). Nạp lại script sau đó là khởi tạo mới. */
    destroy() {
      dead = true;
      if (es) { es.close(); es = null; }
      host.remove();
      for (const k of Object.keys(listeners)) listeners[k].clear();
      delete window.TkInternalChat;
    },
  };

  // ── Khởi động ───────────────────────────────────────────────────────────
  function boot() {
    (document.body || document.documentElement).appendChild(host);
    // Esc: đóng khung danh sách (khi focus đang trong widget). Không nghe phím toàn trang.
    root.addEventListener('keydown', (e) => { if (e.key === 'Escape' && st.open) { st.open = false; render(); root.querySelector('.fab')?.focus(); } });
    let rz = null;
    window.addEventListener('resize', () => { clearTimeout(rz); rz = setTimeout(render, 150); });
    render();
    loadUsers().then(loadRooms).then(() => {
      subscribe();
      emit('ready', { me: st.me });
      window.dispatchEvent(new CustomEvent('tk-internal-chat-ready'));
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
