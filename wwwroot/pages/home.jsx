// pages/home.jsx — App launcher full-screen SAU đăng nhập.
// KHÔNG kế thừa sidebar/topbar của app-shell (xem app.jsx — route '/' render trực tiếp).
//
// Design read (taste skill 6/4/4):
//   - Header 3-cột: brand trái / title giữa / theme+clock phải
//   - Greeting strip
//   - Eyebrow + search filter
//   - Mascot card lớn ở giữa (ảnh /lib/mascot.png)
//   - Grid 7 Agent cards 3-3-1 (1 featured có corner cam)
//
// KHÔNG có: em-dash, scroll cue, version label, marquee, glow.

(function () {
  const { useState: s, useMemo: m, useEffect: e, useRef: r } = React;
  const navigate = window.tourkitRouter.navigate;
  const Icon = window.Icon;

  // AI Agent — mỗi cái = 1 tính năng. Sửa ở đây = đổi launcher.
  const AGENTS = [
    { id: 'chat',      to: '/assistant',    icon: 'sparkle', title: 'AI Chat',                       desc: 'Đội ngũ kinh doanh ảo đắc lực của bạn',     tags: ['trợ lý','số liệu','chatbot'], featured: true },
    { id: 'quote',     to: '/wizard',       icon: 'sparkle', title: 'AI Tính giá Tour',              desc: 'Tính toán chính xác, tối ưu biên lợi nhuận', tags: ['báo giá','tour','wizard'] },
    { id: 'customers', to: '/customers',    icon: 'users',   title: 'AI phân tích khách hàng',       desc: 'Thấu hiểu hành vi, kiến tạo chân dung khách hàng', tags: ['khách','phân tích'] },
    { id: 'deals',     to: '/deals',        icon: 'trend',   title: 'AI Phân tích Cơ hội bán hàng',  desc: 'Nhận diện xu thế thị trường du lịch',       tags: ['deal','bán hàng','ưu tiên'] },
    { id: 'mail',      to: '/mail',         icon: 'paper',   title: 'AI Mail',                       desc: 'Gửi thư điện tử thông minh, tự động hóa tương tác', tags: ['email','gmail'] },
    { id: 'visa',      to: '/visa',         icon: 'shield',  title: 'AI Thẩm định Visa',             desc: 'Đánh giá chính xác tỉ lệ đậu Visa',         tags: ['visa','hồ sơ'] },
    { id: 'tour',      to: '/tour-builder', icon: 'pin',     title: 'AI nhập Tour',                  desc: 'Chuyển văn bản thô thành hành trình hoàn mỹ', tags: ['tour','git','nhập liệu'] },
    { id: 'widget',    to: '/widget-admin', icon: 'sparkle', title: 'Widget Chat AI',                desc: 'Nhúng bot AI vào website khách bằng 1 dòng JS', tags: ['widget','embed','sdk','website'] },
    { id: 'ncc',       to: '/ncc-import',   icon: 'paper',   title: 'AI Import NCC',                 desc: 'Bóc tách Excel / PDF / Word cũ thành 13 cột chuẩn để upload', tags: ['ncc','import','nhà cung cấp','excel','pdf','word'] },
  ];

  function greetingByHour() {
    const h = new Date().getHours();
    if (h < 11) return 'buổi sáng';
    if (h < 14) return 'buổi trưa';
    if (h < 18) return 'buổi chiều';
    return 'buổi tối';
  }

  function normalize(str) {
    return (str || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd');
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }
  function fmtClock(d) {
    return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())} ${pad(d.getDate())}/${pad(d.getMonth()+1)}/${d.getFullYear()}`;
  }

  // Inline SVG robot mascot — multi-part anim (blink/wave/antenna-pulse/cape-flutter/bob).
  // Vector → scale lossless, 5 motion layers độc lập, GPU-accelerated (transform+opacity).
  function MascotBot() {
    return (
      <svg className="hp-mascot-svg" viewBox="0 0 200 220" xmlns="http://www.w3.org/2000/svg"
           role="img" aria-label="TourKit AI robot mascot">
        <defs>
          <linearGradient id="hp-mb-cape-grad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#FB923C"/>
            <stop offset="100%" stopColor="#C2410C"/>
          </linearGradient>
          <linearGradient id="hp-mb-body-grad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#FFFFFF"/>
            <stop offset="100%" stopColor="#E5E7EB"/>
          </linearGradient>
          <radialGradient id="hp-mb-eye-grad" cx="0.5" cy="0.5" r="0.5">
            <stop offset="0%" stopColor="#FDE68A"/>
            <stop offset="60%" stopColor="#F59E0B"/>
            <stop offset="100%" stopColor="#B45309"/>
          </radialGradient>
        </defs>

        {/* Cape — flutter */}
        <path className="hp-mb-cape"
          d="M 56 108 Q 28 150 36 205 L 100 198 L 164 205 Q 172 150 144 108 Z"
          fill="url(#hp-mb-cape-grad)" opacity="0.92"/>

        {/* Left arm (static) */}
        <g className="hp-mb-arm-l">
          <rect x="40" y="118" width="18" height="46" rx="9" fill="url(#hp-mb-body-grad)"/>
          <circle cx="49" cy="170" r="10" fill="#FFFFFF" stroke="#D1D5DB" strokeWidth="1"/>
        </g>

        {/* Body */}
        <rect x="55" y="100" width="90" height="98" rx="22" fill="url(#hp-mb-body-grad)" stroke="#D1D5DB" strokeWidth="1"/>
        <circle cx="100" cy="138" r="7" fill="#F97316"/>
        <circle cx="100" cy="138" r="3" fill="#FFFFFF"/>
        <line x1="100" y1="155" x2="100" y2="190" stroke="#E5E7EB" strokeWidth="1.5" strokeLinecap="round"/>

        {/* Right arm — waves */}
        <g className="hp-mb-arm-r">
          <rect x="142" y="105" width="18" height="46" rx="9" fill="url(#hp-mb-body-grad)"/>
          <circle cx="151" cy="155" r="10" fill="#FFFFFF" stroke="#D1D5DB" strokeWidth="1"/>
        </g>

        {/* Head */}
        <g className="hp-mb-head">
          <rect x="55" y="42" width="90" height="68" rx="18" fill="url(#hp-mb-body-grad)" stroke="#D1D5DB" strokeWidth="1"/>
          <circle cx="55" cy="76" r="4" fill="#D1D5DB"/>
          <circle cx="145" cy="76" r="4" fill="#D1D5DB"/>
          {/* Visor */}
          <rect x="63" y="52" width="74" height="42" rx="12" fill="#1E293B"/>
          <ellipse cx="78" cy="62" rx="10" ry="4" fill="#475569" opacity="0.55"/>
          {/* Eyes — blink */}
          <ellipse className="hp-mb-eye" cx="83" cy="74" rx="6" ry="6" fill="url(#hp-mb-eye-grad)"/>
          <ellipse className="hp-mb-eye hp-mb-eye-r" cx="117" cy="74" rx="6" ry="6" fill="url(#hp-mb-eye-grad)"/>
          <path d="M 88 96 Q 100 102 112 96" stroke="#F59E0B" strokeWidth="2.5" fill="none" strokeLinecap="round"/>
        </g>

        {/* Antenna */}
        <g className="hp-mb-antenna">
          <line x1="100" y1="42" x2="100" y2="22" stroke="#94A3B8" strokeWidth="2.5" strokeLinecap="round"/>
          <circle className="hp-mb-antenna-tip" cx="100" cy="18" r="5" fill="#F97316"/>
        </g>
      </svg>
    );
  }

  // Legacy PNG fallback — giữ lại để revert nhanh nếu cần (không dùng nữa).
  // eslint-disable-next-line no-unused-vars
  function MascotImage() {
    // Ưu tiên /lib/masco-ai.png (robot flying pose, PNG transparent — character ở
     // lower-right canvas, hợp với object-fit:contain + ring halo phía sau).
    // Mascot lớn ở trang chủ vẫn dùng masco-ai.png (robot flying pose, đẹp ở size lớn).
    // trav-ai.png chỉ dùng cho icon/avatar nhỏ (vd Chat AI greet bubble).
    const candidates = ['/lib/masco-ai.png', '/lib/mascot.png', '/lib/mascot.jpg', '/lib/mascot.webp'];
    const [idx, setIdx] = s(0);
    if (idx >= candidates.length) {
      return (
        <svg viewBox="0 0 64 64" width="120" height="120" aria-hidden>
          <defs>
            <linearGradient id="m-fb" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stopColor="#FED7AA" /><stop offset="100%" stopColor="#FB923C" />
            </linearGradient>
          </defs>
          <path fill="url(#m-fb)" d="M32 6l4 10 10 4-10 4-4 10-4-10-10-4 10-4z" />
        </svg>
      );
    }
    return (
      <img src={candidates[idx]} alt="TourKit AI mascot"
        className="hp-mascot-img" onError={() => setIdx(i => i + 1)} />
    );
  }

  // Home Chat: chat THẬT (không phải preview giả) — gọi /api/v1/chat/stream qua SSE.
  // Dùng X-Session-Id từ tourkitAuth (gắn sẵn bởi authedFetch logic). Khi user muốn xem
  // bảng/chart trả về, click "Mở rộng →" để chuyển /assistant?q=<câu hỏi cuối>.
  const QUICK_PROMPTS = [
    'Doanh thu tháng này',
    'Top 5 khách hàng',
    'Tour sắp khởi hành',
  ];
  // STAGE label tiếng Việt — đồng bộ với assistant.jsx
  const STAGE_LABEL = {
    planning:  'AI đang chọn nguồn dữ liệu…',
    fetching:  'Đang lấy số liệu từ CRM…',
    analyzing: 'Đang phân tích…',
  };

  function HomeChatCard() {
    const [input, setInput] = s('');
    const [loading, setLoading] = s(false);
    const [stage, setStage] = s(null);
    const [messages, setMessages] = s([
      { role: 'assistant', content: 'Em là TRAV-AI 👋 Anh/Chị muốn xem báo cáo, danh sách KH hay tra cứu tour ạ?' }
    ]);
    const scrollRef = r(null);
    const lastUserRef = r('');

    e(() => {
      if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }, [messages, stage]);

    async function send(textArg) {
      const text = (typeof textArg === 'string' ? textArg : input).trim();
      if (!text || loading) return;
      const sessionId = window.tourkitAuth?.getSessionId?.();
      if (!sessionId) {
        setMessages(m => [...m, { role: 'user', content: text }, { role: 'assistant', content: '⚠️ Chưa đăng nhập TourKit — không thể chat.', error: true }]);
        return;
      }
      lastUserRef.current = text;
      const next = [...messages, { role: 'user', content: text }];
      const asstIdx = next.length;
      setMessages([...next, { role: 'assistant', content: '', streaming: true }]);
      setInput('');
      setLoading(true);
      setStage('planning');

      const patch = (fn) => setMessages(m => { const c = [...m]; if (c[asstIdx]) c[asstIdx] = fn(c[asstIdx]); return c; });
      const cfg = (window.tourkit && window.tourkit.ai && window.tourkit.ai.getConfig)
        ? window.tourkit.ai.getConfig() : {};

      try {
        const resp = await fetch('/api/v1/chat/stream', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream', 'X-Session-Id': sessionId },
          body: JSON.stringify({
            messages: next, provider: cfg.provider, model: cfg.model
          })
        });
        if (resp.status === 401) {
          patch(a => ({ ...a, content: '⚠️ Phiên hết hạn — đăng nhập lại.', error: true, streaming: false }));
          return;
        }
        if (!resp.ok || !resp.body) {
          const t = await resp.text().catch(() => '');
          throw new Error(t.slice(0, 200) || ('HTTP ' + resp.status));
        }
        const reader = resp.body.getReader();
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
            if (o.error) { patch(a => ({ ...a, content: '⚠️ ' + o.error, error: true, streaming: false })); setStage(null); continue; }
            if (o.stage) { setStage(o.stage); continue; }
            if (o.delta) { setStage(null); patch(a => ({ ...a, content: a.content + o.delta })); continue; }
            if (o.done) {
              if (o.reply) patch(a => ({ ...a, content: o.reply }));
            }
          }
        }
      } catch (e) {
        patch(a => ({ ...a, content: '⚠️ ' + e.message, error: true }));
      } finally {
        patch(a => ({ ...a, streaming: false }));
        setLoading(false);
        setStage(null);
      }
    }

    const expand = () => {
      const q = lastUserRef.current || input;
      navigate(q ? '/assistant?q=' + encodeURIComponent(q) : '/assistant');
    };

    return (
      <div className="hp-chat-card" role="region" aria-label="Chat TRAV-AI">
        <div className="hp-chat-head">
          <div className="hp-chat-avatar">
            <img src="/lib/trav-ai.png" alt="" onError={ev => { ev.target.style.display='none'; }} />
          </div>
          <div className="hp-chat-head-text">
            <div className="hp-chat-head-name">
              TRAV-AI
              <span className="hp-chat-dot" aria-hidden /> <span className="hp-chat-online">đang trực tuyến</span>
            </div>
            <div className="hp-chat-head-sub">Hỏi số liệu, khách, tour — em trả ngay.</div>
          </div>
          <button type="button" className="hp-chat-expand" onClick={expand}
            title="Mở rộng sang Trợ lý số liệu (xem bảng + biểu đồ)">
            Mở rộng <Icon name="arrowRight" size={11} stroke={2.4} />
          </button>
        </div>

        <div className="hp-chat-stream" ref={scrollRef}>
          {messages.map((msg, idx) => (
            <div key={idx} className={'hp-chat-bubble hp-chat-bubble--' + msg.role + (msg.error ? ' hp-chat-bubble--error' : '')}>
              <span className="hp-chat-bubble-text">{msg.content || (msg.streaming ? '…' : '')}</span>
            </div>
          ))}
          {stage && (
            <div className="hp-chat-bubble hp-chat-bubble--bot hp-chat-stage">
              <span className="hp-chat-typing-dot" /><span className="hp-chat-typing-dot" /><span className="hp-chat-typing-dot" />
              <span className="hp-chat-stage-text">{STAGE_LABEL[stage] || stage}</span>
            </div>
          )}
        </div>

        <div className="hp-chat-quick">
          {QUICK_PROMPTS.map(p => (
            <button key={p} type="button" className="hp-chat-chip"
              disabled={loading}
              onClick={() => send(p)} title={'Hỏi: ' + p}>{p}</button>
          ))}
        </div>

        <form className="hp-chat-input" onSubmit={(ev) => { ev.preventDefault(); send(); }}>
          <input type="text" value={input} onChange={ev => setInput(ev.target.value)}
            disabled={loading}
            placeholder={loading ? 'Đang xử lý…' : 'Hỏi gì đó… (vd: doanh thu Q2, tour Đà Nẵng)'}
            aria-label="Câu hỏi cho TRAV-AI" />
          <button type="submit" className="hp-chat-send" aria-label="Gửi câu hỏi"
            disabled={loading || !input.trim()}>
            <Icon name="send" size={15} stroke={2.2} />
          </button>
        </form>
      </div>
    );
  }

  function HomePage({ pushToast }) {
    const [q, setQ] = s('');
    const [now, setNow] = s(() => new Date());
    const [dark, setDark] = s(() => {
      try { return localStorage.getItem('tk_home_theme') === 'dark'; } catch { return false; }
    });
    // Quota AI per-tenant — fetch một lần khi mount + listen event refresh từ authedFetch
    const [quota, setQuota] = s(null);
    e(() => {
      let alive = true;
      const load = async () => {
        try {
          const r = await window.tourkitAuth.authedFetch('/api/v1/quota');
          if (!alive || !r.ok) return;
          setQuota(await r.json());
        } catch {}
      };
      load();
      const onQuota = (ev) => { if (ev.detail) setQuota(ev.detail); else load(); };
      window.addEventListener('tourkit:quota', onQuota);
      return () => { alive = false; window.removeEventListener('tourkit:quota', onQuota); };
    }, []);

    // Tick clock mỗi giây
    e(() => { const id = setInterval(() => setNow(new Date()), 1000); return () => clearInterval(id); }, []);

    // Theme toggle — chỉ áp lên trang Home (data-attr), không leak sang page khác
    e(() => {
      document.documentElement.setAttribute('data-home-theme', dark ? 'dark' : 'light');
      try { localStorage.setItem('tk_home_theme', dark ? 'dark' : 'light'); } catch {}
      return () => document.documentElement.removeAttribute('data-home-theme');
    }, [dark]);

    const user = window.tourkitAuth?.getUser?.() || {};
    const name = (user.fullName || user.userName || '').trim() || 'Bạn';

    const filtered = m(() => {
      const nq = normalize(q.trim());
      if (!nq) return AGENTS;
      return AGENTS.filter(a => {
        const hay = normalize(a.title + ' ' + a.desc + ' ' + (a.tags || []).join(' '));
        return hay.includes(nq);
      });
    }, [q]);

    return (
      <div className="hp">

        {/* ─── Header 3-cột ─────────────────────────────────────────────── */}
        <header className="hp-bar">
          <div className="hp-bar-brand">
            <div className="hp-bar-logo">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" /><path d="M12 8v8M8 12h8" />
              </svg>
            </div>
            <div className="hp-bar-brand-text">
              <div className="hp-bar-brand-row">
                <span className="hp-bar-brand-name">TRAV-AI</span>
                <span className="hp-bar-brand-chip">AI AGENT</span>
              </div>
              <div className="hp-bar-brand-tag">Chuyển đổi số doanh nghiệp du lịch</div>
            </div>
          </div>

          <div className="hp-bar-title">
            <div className="hp-bar-title-row">
              <Icon name="sparkle" size={18} />
              <span className="hp-bar-title-text">TRAV-AI POWERED FUTURE</span>
            </div>
            <div className="hp-bar-title-sub">Thông minh hơn. Vận hành dễ dàng hơn. Tăng trưởng bền vững hơn.</div>
          </div>

          <div className="hp-bar-meta">
            <button className="hp-pill hp-pill--theme" onClick={() => setDark(d => !d)}
              title={dark ? 'Đổi sang sáng' : 'Đổi sang tối'}>
              <span className="hp-pill-ico">{dark ? '🌙' : '☀'}</span>
              <span>{dark ? 'Âm bản (Tối)' : 'Dương bản (Sáng)'}</span>
            </button>
            <span className="hp-pill hp-pill--status">
              <span className="hp-status-dot" /> TỨC THỜI
            </span>
            <span className="hp-pill hp-pill--clock">
              <Icon name="clock" size={13} />
              <span className="hp-clock-val">{fmtClock(now)}</span>
            </span>
            <button className="hp-pill hp-pill--logout"
              onClick={async () => {
                const ok = await window.appConfirm('Đăng xuất khỏi TourKit?', {
                  title: 'Đăng xuất', confirmLabel: 'Đăng xuất', danger: true
                });
                if (ok) window.tourkitAuth.logout();
              }}
              title={`Đăng xuất (${user.fullName || user.companyName || 'Tài khoản'})`}>
              <Icon name="arrowRight" size={14} />
              <span>Đăng xuất</span>
            </button>
          </div>
        </header>

        {/* ─── Greeting strip ──────────────────────────────────────────── */}
        <div className="hp-greet">
          <span className="hp-greet-emoji" aria-hidden>👋</span>
          <span>Chào <b>{greetingByHour()}</b> <b>{name}</b>. Tôi là Trợ lý AI riêng của bạn. Hãy cho tôi biết bạn cần hỗ trợ gì?</span>
          {quota && (() => {
            const used = quota.used || 0, limit = quota.limit || 0, left = Math.max(0, limit - used);
            const pct = limit > 0 ? (used / limit) * 100 : 0;
            const tone = pct >= 100 ? 'is-exhausted' : pct >= 90 ? 'is-warn' : '';
            return (
              <span className={'hp-greet-quota ' + tone} title={`Đã dùng ${used} / ${limit} lượt AI (còn ${left}). Quota reset thủ công.`}>
                <Icon name="sparkle" size={11} />
                <span>AI <b>{used}</b><span className="hp-greet-quota-sep">/</span><b>{limit}</b></span>
                <span className="hp-greet-quota-left">· còn <b>{left}</b></span>
              </span>
            );
          })()}
        </div>

        {/* ─── Section head + search ───────────────────────────────────── */}
        <div className="hp-section-head">
          <div className="hp-section-title">
            <div className="hp-section-ico"><Icon name="sparkle" size={16} /></div>
            <div>
              <div className="hp-section-eyebrow">CHỌN TRỢ LÝ AI CHUYÊN BIỆT</div>
              <div className="hp-section-sub">Đặt lịch trình, phân tích hồ sơ, tối ưu giá trong tích tắc</div>
            </div>
          </div>
          <div className="hp-search">
            <input value={q} onChange={ev => setQ(ev.target.value)}
              placeholder="Tìm kiếm AI Agent…" aria-label="Tìm AI Agent" />
            <span className="hp-search-count">{filtered.length}/{AGENTS.length}</span>
          </div>
        </div>

        {/* ─── Hero 2-cột: mascot trái / chat preview phải ────────────── */}
        <div className="hp-hero">
        <div className="hp-mascot-card">
          <div className="hp-mascot-ring">
            <div className="hp-mascot-orb">
              <MascotImage />
            </div>
          </div>
          {/* 4 stat bubble — opacity 0 ban đầu, fade in + bắt đầu orbit theo
              --appear-delay riêng (mascot fly-in xong ~1.6s mới tới lượt;
              stagger 300ms để mắt thấy được sequence) */}
          <div className="hp-stat-bubble" style={{ '--start': '315deg', '--appear-delay': '1.8s' }} aria-label="571 hồ sơ KH">
            <span className="hp-stat-num">571</span>
            <span className="hp-stat-lbl">Hồ sơ KH</span>
          </div>
          <div className="hp-stat-bubble" style={{ '--start': '45deg', '--appear-delay': '2.1s' }} aria-label="8 AI Agent">
            <span className="hp-stat-num">8</span>
            <span className="hp-stat-lbl">AI Agent</span>
          </div>
          <div className="hp-stat-bubble" style={{ '--start': '135deg', '--appear-delay': '2.4s' }} aria-label="17 Tool">
            <span className="hp-stat-num">17</span>
            <span className="hp-stat-lbl">Tool</span>
          </div>
          <div className="hp-stat-bubble" style={{ '--start': '225deg', '--appear-delay': '2.7s' }} aria-label="24/7 Live">
            <span className="hp-stat-num">24/7</span>
            <span className="hp-stat-lbl">Live</span>
          </div>
          <div className="hp-mascot-label">
            <div className="hp-mascot-title hp-mascot-title--anim">TRAV-AI</div>
            <div className="hp-mascot-sub">
              <img className="hp-mascot-emoji" src="/lib/trav-ai.png" alt="TRAV-AI"
                onError={e => { e.target.style.display = 'none'; }} />
              <span className="hp-mascot-typing"> Đang sẵn sàng tư vấn Tour &amp; Telesales!</span>
              <span className="hp-mascot-caret" aria-hidden>|</span>
            </div>
          </div>
        </div>
        <HomeChatCard />
        </div>{/* /.hp-hero */}

        {/* ─── Grid 7 Agent ────────────────────────────────────────────── */}
        <div className="hp-grid">
          {filtered.map(a => (
            <a key={a.id} href={a.to}
              className={'hp-card' + (a.featured ? ' featured' : '')}
              onClick={ev => {
                if (ev.defaultPrevented || ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return;
                ev.preventDefault();
                navigate(a.to);
              }}>
              <div className="hp-card-ico"><Icon name={a.icon} size={18} /></div>
              <div className="hp-card-body">
                <div className="hp-card-title">{a.title}</div>
                <div className="hp-card-desc">{a.desc}</div>
              </div>
              {a.featured && <span className="hp-card-corner" aria-hidden />}
            </a>
          ))}
          {filtered.length === 0 && (
            <div className="hp-empty">
              <Icon name="search" size={22} />
              <div><b>Không tìm thấy Agent phù hợp</b><br/>Thử từ khoá khác (vd "khách", "visa", "deal")</div>
            </div>
          )}
        </div>

      </div>
    );
  }

  window.HomePage = HomePage;
})();
