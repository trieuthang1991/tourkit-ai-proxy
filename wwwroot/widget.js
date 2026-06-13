/*!
 * TRAV-AI Chat Widget
 * Embed (2 cách):
 *   1. Token sẵn có:
 *      <script async src="https://your-proxy/widget.js" data-token="trav_xxx"></script>
 *   2. Auto-init bằng Crypton token TourKit (cùng /login-token):
 *      <script async src="https://your-proxy/widget.js" data-login-token="ZGV1Z3Iz..."></script>
 *      → widget gọi /api/v1/widget/init lần đầu, cache trav_xxx vào localStorage, lần sau reuse.
 * Optional data-*: greeting, color, bot-name, position (br|bl), z-index
 *
 * Self-contained, vanilla JS, Shadow DOM CSS isolation. No build, no deps.
 */
(async function () {
  'use strict';

  // ── Locate own <script> tag for data-attrs + API base ─────────────────────
  const scriptEl = document.currentScript
    || document.querySelector('script[src*="widget.js"][data-token]')
    || document.querySelector('script[src*="widget.js"][data-login-token]');
  if (!scriptEl) { console.warn('[trav-widget] không tìm thấy <script> tag'); return; }

  // API base = origin của script src (khách dán "https://proxy/widget.js" → API = "https://proxy")
  const apiBase = (() => {
    try { return new URL(scriptEl.src).origin; }
    catch { return location.origin; }
  })();

  // ── Resolve token: ưu tiên data-token (đã có). Nếu chỉ có data-login-token → auto-init. ──
  let token = scriptEl.getAttribute('data-token');
  const loginToken = scriptEl.getAttribute('data-login-token');

  if (!token && loginToken) {
    // Cache key đơn giản: 24 ký tự đầu của Crypton (đủ unique mà không full-leak token vào localStorage key).
    const cacheKey = 'trav-widget-auto:' + loginToken.slice(0, 24);
    token = localStorage.getItem(cacheKey);
    if (!token) {
      try {
        const r = await fetch(`${apiBase}/api/v1/widget/init`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ token: loginToken }),
        });
        const data = await r.json();
        if (r.ok && data.token) {
          token = data.token;
          localStorage.setItem(cacheKey, token);
        } else {
          console.warn('[trav-widget] init thất bại:', data.error || ('HTTP ' + r.status));
          return;
        }
      } catch (e) {
        console.warn('[trav-widget] init lỗi mạng:', e.message);
        return;
      }
    }
  }

  if (!token) { console.warn('[trav-widget] thiếu data-token hoặc data-login-token'); return; }

  // Override qua data-* (nếu admin muốn đổi greeting per-site)
  const overrideGreeting = scriptEl.getAttribute('data-greeting');
  const overrideColor    = scriptEl.getAttribute('data-color');
  const overrideBotName  = scriptEl.getAttribute('data-bot-name');
  const position         = (scriptEl.getAttribute('data-position') || 'br').toLowerCase();
  const zIndex           = scriptEl.getAttribute('data-z-index') || '2147483600';

  // ── Mount host element + Shadow DOM (CSS isolation) ───────────────────────
  const host = document.createElement('div');
  host.id = 'trav-ai-widget-host';
  host.style.position = 'fixed';
  host.style.zIndex = zIndex;
  host.style[position === 'bl' ? 'left' : 'right'] = '20px';
  host.style.bottom = '20px';
  document.body.appendChild(host);
  const root = host.attachShadow({ mode: 'open' });

  // ── State ────────────────────────────────────────────────────────────────
  let cfg = { botName: 'Trợ lý AI', greeting: 'Xin chào!', color: '#F97316', enabled: true };
  let open = false;
  const history = [];   // [{role:'user'|'assistant', content:'...'}]
  let sending = false;

  // ── Styles (đặt trong Shadow → không leak ra site khách) ─────────────────
  const style = document.createElement('style');
  style.textContent = `
    :host, * { box-sizing: border-box; }
    .root {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Be Vietnam Pro', sans-serif;
      font-size: 14px; color: #1F2937;
    }
    .bubble {
      width: 60px; height: 60px; border-radius: 50%;
      background: var(--c); color: #fff;
      box-shadow: 0 10px 28px rgba(0,0,0,0.18), 0 4px 12px rgba(0,0,0,0.12);
      display: grid; place-items: center;
      cursor: pointer; border: none; outline: none;
      transition: transform 0.18s ease, box-shadow 0.18s ease;
      animation: bubbleFloat 3.4s ease-in-out infinite;
    }
    .bubble:hover { transform: translateY(-2px); box-shadow: 0 14px 34px rgba(0,0,0,0.22); animation-play-state: paused; }
    .bubble.hidden { display: none; }
    .bubble svg { width: 26px; height: 26px; stroke: currentColor; fill: none; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round;
      animation: iconWobble 5.6s ease-in-out infinite; transform-origin: center; }
    /* Pulse halo ring khi idle — nhắc khẽ "tôi đang ở đây" */
    .bubble::before {
      content: ''; position: absolute; inset: -4px; border-radius: 50%;
      box-shadow: 0 0 0 0 var(--c); opacity: 0.55;
      animation: bubblePulse 2.6s ease-out infinite;
      pointer-events: none;
    }
    @keyframes bubbleFloat {
      0%, 100% { transform: translateY(0); }
      50%      { transform: translateY(-4px); }
    }
    @keyframes iconWobble {
      0%, 92%, 100% { transform: rotate(0deg) scale(1); }
      94%           { transform: rotate(-8deg) scale(1.06); }
      96%           { transform: rotate(8deg)  scale(1.06); }
      98%           { transform: rotate(-4deg) scale(1.03); }
    }
    @keyframes bubblePulse {
      0%   { box-shadow: 0 0 0 0 var(--c);    opacity: 0.45; }
      80%  { box-shadow: 0 0 0 14px transparent; opacity: 0; }
      100% { box-shadow: 0 0 0 0 transparent; opacity: 0; }
    }
    .badge {
      position: absolute; top: -4px; right: -4px;
      width: 14px; height: 14px; border-radius: 50%;
      background: #10B981; border: 2px solid #fff;
    }
    .panel {
      position: absolute; bottom: 0; right: 0;
      width: 380px; max-width: calc(100vw - 32px);
      height: 560px; max-height: calc(100vh - 60px);
      background: #fff; border-radius: 18px; overflow: hidden;
      box-shadow: 0 20px 60px rgba(0,0,0,0.22), 0 8px 24px rgba(0,0,0,0.12);
      display: none; flex-direction: column;
      transform-origin: bottom right;
      animation: slideUp 0.22s cubic-bezier(0.16, 1, 0.3, 1);
    }
    .panel.open { display: flex; }
    @keyframes slideUp { from { opacity: 0; transform: translateY(20px) scale(0.96); } to { opacity: 1; transform: none; } }
    .header {
      padding: 16px 18px;
      background: linear-gradient(135deg, var(--c) 0%, var(--c2) 100%);
      color: #fff; display: flex; align-items: center; gap: 12px;
    }
    .header-avatar {
      width: 38px; height: 38px; border-radius: 50%;
      background: #fff; overflow: hidden;
      box-shadow: 0 2px 6px rgba(0,0,0,0.12);
    }
    .header-avatar img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .header-title { flex: 1; }
    .header-name { font-weight: 700; font-size: 15px; line-height: 1.2; }
    .header-status { font-size: 11px; opacity: 0.92; margin-top: 2px; display: flex; align-items: center; gap: 5px; }
    .header-status::before {
      content: ''; width: 7px; height: 7px; border-radius: 50%;
      background: #BBF7D0; box-shadow: 0 0 0 2px rgba(187,247,208,0.35);
    }
    .header-actions { display: flex; gap: 6px; }
    .header-btn {
      background: rgba(255,255,255,0.18); border: 0; color: #fff;
      width: 30px; height: 30px; border-radius: 8px; cursor: pointer;
      display: grid; place-items: center;
    }
    .header-btn:hover { background: rgba(255,255,255,0.30); }
    .header-btn svg { width: 15px; height: 15px; stroke: currentColor; fill: none; stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
    .messages {
      flex: 1; overflow-y: auto; padding: 16px;
      background: linear-gradient(180deg, #FFF7ED 0%, #fff 100%);
      display: flex; flex-direction: column; gap: 10px;
    }
    .msg { max-width: 82%; padding: 10px 14px; border-radius: 14px; line-height: 1.45; word-wrap: break-word; white-space: pre-wrap;
      animation: msgIn 0.32s cubic-bezier(0.18, 0.89, 0.32, 1.18) both;
      transform-origin: bottom left; }
    .msg.user { transform-origin: bottom right; animation-name: msgInRight; }
    .msg.bot   { background: #fff; border: 1px solid #FED7AA; align-self: flex-start; border-bottom-left-radius: 4px; }
    .msg.user  { background: var(--c); color: #fff; align-self: flex-end; border-bottom-right-radius: 4px; }
    @keyframes msgIn {
      0%   { opacity: 0; transform: translateY(8px) scale(0.94); }
      100% { opacity: 1; transform: translateY(0)   scale(1); }
    }
    @keyframes msgInRight {
      0%   { opacity: 0; transform: translateX(10px) scale(0.96); }
      100% { opacity: 1; transform: translateX(0)    scale(1); }
    }
    .msg.typing::after {
      content: '...'; display: inline-block; width: 1.5em;
      animation: dots 1.2s steps(4, end) infinite;
    }
    /* Caret nhấp nháy khi đang stream — nhìn "AI đang gõ" thật hơn */
    .msg[data-streaming]::after {
      content: '▍'; display: inline-block; margin-left: 1px;
      color: var(--c); opacity: 0.85; font-weight: 400;
      animation: caretBlink 0.9s steps(2, start) infinite;
      transform: translateY(-1px);
    }
    @keyframes caretBlink {
      0%, 100% { opacity: 0; }
      50%      { opacity: 0.85; }
    }
    .crm-chip {
      display: inline-flex; align-items: center; gap: 4px;
      margin-top: 6px; padding: 2px 8px; border-radius: 999px;
      background: #DCFCE7; color: #15803D;
      font-size: 10.5px; font-weight: 700;
      letter-spacing: 0.3px;
    }
    .crm-chip::before { content: '✓'; font-size: 11px; }
    @keyframes dots { 0% { content: '.'; } 33% { content: '..'; } 66% { content: '...'; } }
    .panel.expanded {
      width: min(720px, calc(100vw - 32px));
      height: min(840px, calc(100vh - 60px));
    }
    .quick-replies {
      display: flex; flex-wrap: wrap; gap: 6px;
      margin: 6px 0 2px 0;
    }
    .quick-chip {
      background: #fff; border: 1px solid var(--c); color: var(--c);
      padding: 6px 12px; border-radius: 999px; font-size: 12.5px;
      cursor: pointer; transition: all 0.15s; font-family: inherit;
      animation: msgIn 0.28s ease-out both;
    }
    .quick-chip:nth-child(2) { animation-delay: 0.05s; }
    .quick-chip:nth-child(3) { animation-delay: 0.10s; }
    .quick-chip:nth-child(4) { animation-delay: 0.15s; }
    .quick-chip:hover { background: var(--c); color: #fff; transform: translateY(-1px); }
    .quick-chip:active { transform: translateY(0) scale(0.96); }
    .attachments {
      display: flex; flex-wrap: wrap; gap: 6px; padding: 8px 12px 0;
      background: #fff; border-top: 1px solid #F1F5F9;
    }
    .attachments:empty { display: none; }
    .att-chip {
      display: flex; align-items: center; gap: 6px;
      padding: 4px 8px 4px 4px; border-radius: 8px;
      background: #F1F5F9; font-size: 12px; max-width: 200px;
    }
    .att-chip img { width: 28px; height: 28px; object-fit: cover; border-radius: 4px; }
    .att-chip .att-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .att-chip .att-rm { background: none; border: 0; cursor: pointer; color: #64748B; font-size: 14px; line-height: 1; padding: 2px 4px; }
    .att-chip .att-rm:hover { color: #DC2626; }
    .input-row {
      padding: 10px 12px 12px; border-top: 1px solid #F1F5F9; background: #fff;
      display: flex; gap: 6px; align-items: flex-end;
    }
    .toolbar-btn {
      background: transparent; border: 0;
      width: 36px; height: 36px; border-radius: 8px; cursor: pointer;
      display: grid; place-items: center; color: #64748B;
      transition: all 0.15s;
    }
    .toolbar-btn:hover { background: #F1F5F9; color: var(--c); transform: translateY(-1px); }
    .toolbar-btn:active { transform: translateY(0) scale(0.92); }
    .toolbar-btn.recording { color: #fff; background: #DC2626; animation: pulse 1.2s ease-in-out infinite; }
    @keyframes pulse {
      0%, 100% { box-shadow: 0 0 0 0 rgba(220,38,38,0.5); }
      50%      { box-shadow: 0 0 0 6px rgba(220,38,38,0); }
    }
    .toolbar-btn svg { width: 18px; height: 18px; stroke: currentColor; fill: none; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
    .input {
      flex: 1; resize: none; border: 1px solid #E2E8F0; border-radius: 12px;
      padding: 10px 14px; font: inherit; outline: none; max-height: 100px;
      line-height: 1.4;
    }
    .input:focus { border-color: var(--c); box-shadow: 0 0 0 3px rgba(249,115,22,0.12); }
    .send {
      background: var(--c); color: #fff; border: 0; border-radius: 12px;
      width: 42px; height: 42px; cursor: pointer; display: grid; place-items: center;
    }
    .send:disabled { background: #CBD5E1; cursor: not-allowed; }
    .send svg { width: 18px; height: 18px; stroke: currentColor; fill: none; stroke-width: 2.5; stroke-linecap: round; stroke-linejoin: round; }
    .footer {
      text-align: center; padding: 8px; font-size: 11px; color: #94A3B8;
      background: #fff; border-top: 1px solid #F8FAFC;
    }
    .footer a { color: var(--c); text-decoration: none; font-weight: 600; }
    @media (max-width: 480px) {
      .panel, .panel.expanded {
        width: 100vw; height: 100vh; max-height: 100vh;
        border-radius: 0; bottom: -20px; right: -20px;
      }
    }
  `;
  root.appendChild(style);

  // ── Markup ───────────────────────────────────────────────────────────────
  const rootDiv = document.createElement('div');
  rootDiv.className = 'root';
  rootDiv.innerHTML = `
    <button class="bubble" type="button" aria-label="Mở chat">
      <svg viewBox="0 0 24 24"><path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z"/></svg>
      <span class="badge"></span>
    </button>
    <div class="panel" role="dialog" aria-label="Cửa sổ chat">
      <div class="header">
        <div class="header-avatar">
          <img src="${apiBase}/lib/trav-ai.png" alt="" />
        </div>
        <div class="header-title">
          <div class="header-name"></div>
          <div class="header-status">Đang sẵn sàng</div>
        </div>
        <div class="header-actions">
          <button class="header-btn header-reset" type="button" aria-label="Xoá hội thoại" title="Xoá hội thoại">
            <svg viewBox="0 0 24 24"><path d="M1 4v6h6M23 20v-6h-6"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>
          </button>
          <button class="header-btn header-expand" type="button" aria-label="Mở rộng" title="Mở rộng">
            <svg viewBox="0 0 24 24" class="ico-expand"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg>
            <svg viewBox="0 0 24 24" class="ico-collapse" style="display:none"><path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/></svg>
          </button>
          <button class="header-btn header-close" type="button" aria-label="Đóng" title="Đóng">
            <svg viewBox="0 0 24 24"><path d="M18 6L6 18M6 6l12 12"/></svg>
          </button>
        </div>
      </div>
      <div class="messages" role="log"></div>
      <div class="attachments"></div>
      <div class="input-row">
        <button class="toolbar-btn btn-mic" type="button" aria-label="Thu âm" title="Thu âm">
          <svg viewBox="0 0 24 24"><path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2M12 19v4M8 23h8"/></svg>
        </button>
        <button class="toolbar-btn btn-file" type="button" aria-label="Đính kèm file" title="Đính kèm ảnh / PDF">
          <svg viewBox="0 0 24 24"><path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/></svg>
        </button>
        <input type="file" class="file-input" accept="image/*,application/pdf" multiple hidden />
        <textarea class="input" rows="1" placeholder="Nhập tin nhắn..." aria-label="Tin nhắn"></textarea>
        <button class="send" type="button" aria-label="Gửi" disabled>
          <svg viewBox="0 0 24 24"><path d="M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z"/></svg>
        </button>
      </div>
      <div class="footer">Cung cấp bởi <a href="#" target="_blank" rel="noopener">TRAV-AI</a></div>
    </div>
  `;
  root.appendChild(rootDiv);

  const $ = sel => root.querySelector(sel);
  const elBubble  = $('.bubble');
  const elPanel   = $('.panel');
  const elName    = $('.header-name');
  const elClose   = $('.header-close');
  const elReset   = $('.header-reset');
  const elExpand  = $('.header-expand');
  const elMsgs    = $('.messages');
  const elAtts    = $('.attachments');
  const elInput   = $('.input');
  const elSend    = $('.send');
  const elMic     = $('.btn-mic');
  const elFile    = $('.btn-file');
  const elFileInp = $('.file-input');

  // ── Attachments state ────────────────────────────────────────────────────
  const attachments = [];   // { name, dataUrl, mime, kind: 'image'|'doc' }
  let expanded = false;
  let recording = false;
  let mediaRecorder = null;
  let mediaChunks = [];
  let mediaStream = null;
  const STORAGE_KEY = 'trav-widget-' + token;

  // ── Fetch config + render greeting ───────────────────────────────────────
  fetch(`${apiBase}/api/v1/widget/config?token=${encodeURIComponent(token)}`)
    .then(r => r.ok ? r.json() : Promise.reject(r))
    .then(c => {
      cfg = {
        botName:  overrideBotName  || c.botName  || cfg.botName,
        greeting: overrideGreeting || c.greeting || cfg.greeting,
        color:    overrideColor    || c.color    || cfg.color,
        enabled:  c.enabled !== false
      };
      applyConfig();
    })
    .catch(() => {
      // Token không hợp lệ / endpoint không reach → ẩn widget (im lặng, không quấy site khách).
      host.style.display = 'none';
    });

  function applyConfig() {
    if (!cfg.enabled) { host.style.display = 'none'; return; }
    rootDiv.style.setProperty('--c', cfg.color);
    rootDiv.style.setProperty('--c2', shade(cfg.color, -18));
    elName.textContent = cfg.botName;
    // Khôi phục history từ localStorage trước khi push greeting (greeting chỉ hiện khi không có history)
    const restored = restoreHistory();
    if (!restored) {
      pushBot(cfg.greeting);
      renderQuickReplies();
    }
  }

  // ── Persist history (localStorage per-token) ─────────────────────────────
  function saveHistory() {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(history.slice(-50))); } catch {}
  }
  function restoreHistory() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return false;
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr) || arr.length === 0) return false;
      for (const m of arr) {
        const d = document.createElement('div');
        d.className = 'msg ' + (m.role === 'assistant' ? 'bot' : 'user');
        d.textContent = m.content || '';
        elMsgs.appendChild(d);
        history.push({ role: m.role, content: m.content });
      }
      scrollBottom();
      return true;
    } catch { return false; }
  }
  function resetChat() {
    history.length = 0;
    elMsgs.innerHTML = '';
    attachments.length = 0;
    renderAttachments();
    try { localStorage.removeItem(STORAGE_KEY); } catch {}
    pushBot(cfg.greeting);
    renderQuickReplies();
  }

  // ── Quick replies (3 chip gợi ý dưới greeting) ───────────────────────────
  const QUICK_REPLIES = ['Có tour nào đang mở?', 'Báo giá tour cho tôi', 'Thông tin liên hệ'];
  function renderQuickReplies() {
    const last = elMsgs.lastElementChild;
    if (!last) return;
    const row = document.createElement('div');
    row.className = 'quick-replies';
    QUICK_REPLIES.forEach(text => {
      const c = document.createElement('button');
      c.className = 'quick-chip'; c.type = 'button'; c.textContent = text;
      c.onclick = () => { row.remove(); elInput.value = text; elInput.dispatchEvent(new Event('input', { bubbles: true })); send(); };
      row.appendChild(c);
    });
    last.appendChild(row);
    scrollBottom();
  }

  // Darken hex color N% (negative = darker). Quick helper cho gradient header.
  function shade(hex, p) {
    const m = hex.match(/^#([0-9a-f]{6})$/i);
    if (!m) return hex;
    const n = parseInt(m[1], 16);
    let r = (n >> 16) & 0xff, g = (n >> 8) & 0xff, b = n & 0xff;
    r = Math.max(0, Math.min(255, Math.round(r * (100 + p) / 100)));
    g = Math.max(0, Math.min(255, Math.round(g * (100 + p) / 100)));
    b = Math.max(0, Math.min(255, Math.round(b * (100 + p) / 100)));
    return '#' + [r,g,b].map(x => x.toString(16).padStart(2,'0')).join('');
  }

  // ── Open / close / expand / reset ─────────────────────────────────────────
  elBubble.addEventListener('click', () => toggle(true));
  elClose.addEventListener('click', () => toggle(false));
  elReset.addEventListener('click', () => {
    if (confirm('Xoá toàn bộ hội thoại?')) resetChat();
  });
  elExpand.addEventListener('click', () => {
    expanded = !expanded;
    elPanel.classList.toggle('expanded', expanded);
    elExpand.querySelector('.ico-expand').style.display = expanded ? 'none' : '';
    elExpand.querySelector('.ico-collapse').style.display = expanded ? '' : 'none';
  });

  // ── Attachments ──────────────────────────────────────────────────────────
  elFile.addEventListener('click', () => elFileInp.click());
  elFileInp.addEventListener('change', async (e) => {
    for (const f of e.target.files) {
      if (f.size > 8 * 1024 * 1024) {
        alert(`${f.name}: file >8MB, bỏ qua`); continue;
      }
      const dataUrl = await readAsDataUrl(f);
      const kind = f.type.startsWith('image/') ? 'image' : (f.type === 'application/pdf' ? 'doc' : null);
      if (!kind) { alert(`${f.name}: chỉ hỗ trợ ảnh hoặc PDF`); continue; }
      attachments.push({ name: f.name, dataUrl, mime: f.type, kind });
    }
    elFileInp.value = '';
    renderAttachments();
  });
  function readAsDataUrl(file) {
    return new Promise((resolve, reject) => {
      const r = new FileReader();
      r.onload = () => resolve(r.result);
      r.onerror = reject;
      r.readAsDataURL(file);
    });
  }
  function renderAttachments() {
    elAtts.innerHTML = '';
    attachments.forEach((a, i) => {
      const chip = document.createElement('div');
      chip.className = 'att-chip';
      const preview = a.kind === 'image'
        ? `<img src="${a.dataUrl}" alt="" />`
        : '<span style="font-size:18px">📄</span>';
      chip.innerHTML = `${preview}<span class="att-name">${a.name}</span><button class="att-rm" type="button" aria-label="Xoá">×</button>`;
      chip.querySelector('.att-rm').onclick = () => { attachments.splice(i, 1); renderAttachments(); };
      elAtts.appendChild(chip);
    });
    // Send button: enabled nếu có text HOẶC có attachment
    elSend.disabled = sending || (elInput.value.trim().length === 0 && attachments.length === 0);
  }

  // ── Mic recording ───────────────────────────────────────────────────────
  elMic.addEventListener('click', async () => {
    if (recording) { stopRecord(); return; }
    if (!navigator.mediaDevices?.getUserMedia) { alert('Trình duyệt không hỗ trợ thu âm'); return; }
    try {
      mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      mediaRecorder = new MediaRecorder(mediaStream);
      mediaChunks = [];
      mediaRecorder.ondataavailable = (e) => { if (e.data.size > 0) mediaChunks.push(e.data); };
      mediaRecorder.onstop = transcribeRecording;
      mediaRecorder.start();
      recording = true;
      elMic.classList.add('recording');
      elMic.title = 'Bấm dừng và gửi đi';
    } catch (e) {
      alert('Không truy cập được mic: ' + e.message);
    }
  });
  function stopRecord() {
    if (!mediaRecorder) return;
    mediaRecorder.stop();
    mediaStream?.getTracks().forEach(t => t.stop());
    recording = false;
    elMic.classList.remove('recording');
    elMic.title = 'Thu âm';
  }
  async function transcribeRecording() {
    if (mediaChunks.length === 0) return;
    const blob = new Blob(mediaChunks, { type: 'audio/webm' });
    const fd = new FormData();
    fd.append('token', token);
    fd.append('file', blob, 'audio.webm');
    elMic.disabled = true;
    elInput.placeholder = 'Đang chuyển giọng nói thành văn bản...';
    try {
      const r = await fetch(`${apiBase}/api/v1/widget/transcribe`, { method: 'POST', body: fd });
      const data = await r.json();
      if (data.text) {
        elInput.value = (elInput.value ? elInput.value + ' ' : '') + data.text;
        elInput.dispatchEvent(new Event('input', { bubbles: true }));
        elInput.focus();
      } else if (data.error) alert('STT lỗi: ' + data.error);
    } catch (e) {
      alert('Lỗi gửi audio: ' + e.message);
    } finally {
      elMic.disabled = false;
      elInput.placeholder = 'Nhập tin nhắn...';
    }
  }

  function toggle(o) {
    open = o;
    elBubble.classList.toggle('hidden', o);
    elPanel.classList.toggle('open', o);
    if (o) setTimeout(() => elInput.focus(), 100);
  }

  // ── Input handling ───────────────────────────────────────────────────────
  elInput.addEventListener('input', () => {
    elSend.disabled = sending || (elInput.value.trim().length === 0 && attachments.length === 0);
    elInput.style.height = 'auto';
    elInput.style.height = Math.min(100, elInput.scrollHeight) + 'px';
  });
  elInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  });
  elSend.addEventListener('click', send);

  function pushBot(text) {
    const d = document.createElement('div');
    d.className = 'msg bot'; d.textContent = text;
    elMsgs.appendChild(d); scrollBottom();
    history.push({ role: 'assistant', content: text });
    saveHistory();
  }
  function pushUser(text, attachedThumbs) {
    const d = document.createElement('div');
    d.className = 'msg user'; d.textContent = text;
    if (attachedThumbs?.length) {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;gap:4px;margin-top:6px;flex-wrap:wrap';
      attachedThumbs.forEach(a => {
        if (a.kind === 'image') {
          const i = document.createElement('img');
          i.src = a.dataUrl; i.style.cssText = 'max-width:80px;max-height:80px;border-radius:6px';
          row.appendChild(i);
        } else {
          const s = document.createElement('span');
          s.style.cssText = 'font-size:12px;background:rgba(255,255,255,0.25);padding:2px 8px;border-radius:6px';
          s.textContent = '📄 ' + a.name;
          row.appendChild(s);
        }
      });
      d.appendChild(row);
    }
    elMsgs.appendChild(d); scrollBottom();
    history.push({ role: 'user', content: text });
    saveHistory();
  }
  function pushTyping() {
    const d = document.createElement('div');
    d.className = 'msg bot typing'; d.dataset.typing = '1';
    elMsgs.appendChild(d); scrollBottom();
    return d;
  }
  function scrollBottom() { elMsgs.scrollTop = elMsgs.scrollHeight; }

  async function send() {
    const text = elInput.value.trim();
    if ((!text && attachments.length === 0) || sending) return;
    sending = true;
    elInput.value = ''; elInput.style.height = 'auto';
    elSend.disabled = true;

    // Snapshot attachments để render trong bubble + gửi backend, rồi clear khỏi input area.
    const sentAttachments = attachments.slice();
    attachments.length = 0;
    renderAttachments();

    pushUser(text || '(đính kèm)', sentAttachments);
    const typing = pushTyping();

    try {
      const beforeNew = history.slice(0, -1);
      const images = sentAttachments.filter(a => a.kind === 'image').map(a => a.dataUrl);
      const documents = sentAttachments.filter(a => a.kind === 'doc').map(a => a.dataUrl);
      const resp = await fetch(`${apiBase}/api/v1/widget/chat/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token, message: text || 'Vui lòng phân tích đính kèm', history: beforeNew, images, documents })
      });
      if (!resp.ok || !resp.body) throw new Error('HTTP ' + resp.status);

      const reader = resp.body.getReader();
      const dec = new TextDecoder();
      let buf = '', acc = '', first = true;

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        let idx;
        while ((idx = buf.indexOf('\n\n')) >= 0) {
          const chunk = buf.slice(0, idx); buf = buf.slice(idx + 2);
          if (!chunk.startsWith('data:')) continue;
          const json = chunk.slice(5).trim();
          if (!json) continue;
          try {
            const obj = JSON.parse(json);
            if (obj.error) throw new Error(obj.error);
            if (obj.delta) {
              if (first) {
                typing.classList.remove('typing');
                typing.removeAttribute('data-typing');
                typing.textContent = '';
                typing.setAttribute('data-streaming', '1'); // ← bật caret nhấp nháy
                first = false;
              }
              acc += obj.delta;
              typing.textContent = acc;
              scrollBottom();
            }
            if (obj.done) {
              typing.removeAttribute('data-streaming'); // ← tắt caret khi xong
              if (!acc && obj.reply) { typing.classList.remove('typing'); typing.textContent = obj.reply; acc = obj.reply; }
              if (obj.usedCrm) {
                const chip = document.createElement('div');
                chip.className = 'crm-chip';
                chip.textContent = 'Dữ liệu thật từ CRM';
                typing.appendChild(chip);
              }
              history.push({ role: 'assistant', content: acc });
              saveHistory();
            }
          } catch (e) {
            if (e.message && e.message !== 'Unexpected end of JSON input') {
              typing.classList.remove('typing');
              typing.removeAttribute('data-streaming');
              typing.textContent = 'Lỗi: ' + e.message;
            }
          }
        }
      }
    } catch (e) {
      typing.classList.remove('typing');
      typing.textContent = 'Xin lỗi, đang gặp lỗi kết nối. Anh/Chị vui lòng thử lại sau.';
      console.error('[trav-widget]', e);
    } finally {
      sending = false;
      elSend.disabled = elInput.value.trim().length === 0;
      elInput.focus();
    }
  }

  // ── Public API (window.travWidget) ───────────────────────────────────────
  window.travWidget = {
    open:  () => toggle(true),
    close: () => toggle(false),
    reset: () => { history.length = 0; elMsgs.innerHTML = ''; pushBot(cfg.greeting); }
  };
})();
