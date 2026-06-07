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
  const { useState: s, useMemo: m, useEffect: e } = React;
  const navigate = window.tourkitRouter.navigate;
  const Icon = window.Icon;

  // 7 AI Agent — mỗi cái = 1 tính năng. Sửa ở đây = đổi launcher.
  const AGENTS = [
    { id: 'chat',      to: '/assistant',    icon: 'sparkle', title: 'AI Chat',                       desc: 'Đội ngũ kinh doanh ảo đắc lực của bạn',     tags: ['trợ lý','số liệu','chatbot'], featured: true },
    { id: 'quote',     to: '/wizard',       icon: 'sparkle', title: 'AI Tính giá Tour',              desc: 'Tính toán chính xác, tối ưu biên lợi nhuận', tags: ['báo giá','tour','wizard'] },
    { id: 'customers', to: '/customers',    icon: 'users',   title: 'AI phân tích khách hàng',       desc: 'Thấu hiểu hành vi, kiến tạo chân dung khách hàng', tags: ['khách','phân tích'] },
    { id: 'deals',     to: '/deals',        icon: 'trend',   title: 'AI Phân tích Cơ hội bán hàng',  desc: 'Nhận diện xu thế thị trường du lịch',       tags: ['deal','bán hàng','ưu tiên'] },
    { id: 'mail',      to: '/mail',         icon: 'paper',   title: 'AI Mail',                       desc: 'Gửi thư điện tử thông minh, tự động hóa tương tác', tags: ['email','gmail'] },
    { id: 'visa',      to: '/visa',         icon: 'shield',  title: 'AI Thẩm định Visa',             desc: 'Đánh giá chính xác tỉ lệ đậu Visa',         tags: ['visa','hồ sơ'] },
    { id: 'tour',      to: '/tour-builder', icon: 'pin',     title: 'AI nhập Tour',                  desc: 'Chuyển văn bản thô thành hành trình hoàn mỹ', tags: ['tour','git','nhập liệu'] },
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

  // Thử lần lượt mascot.png → .jpg → .webp; tất cả fail → SVG fallback cam.
  function MascotImage() {
    const candidates = ['/lib/mascot.png', '/lib/mascot.jpg', '/lib/mascot.webp'];
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

  function HomePage({ pushToast }) {
    const [q, setQ] = s('');
    const [now, setNow] = s(() => new Date());
    const [dark, setDark] = s(() => {
      try { return localStorage.getItem('tk_home_theme') === 'dark'; } catch { return false; }
    });

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
                <span className="hp-bar-brand-name">TOURKIT</span>
                <span className="hp-bar-brand-chip">AI AGENT</span>
              </div>
              <div className="hp-bar-brand-tag">Chuyển đổi số doanh nghiệp du lịch</div>
            </div>
          </div>

          <div className="hp-bar-title">
            <div className="hp-bar-title-row">
              <Icon name="sparkle" size={18} />
              <span className="hp-bar-title-text">TOURKIT AI-POWERED FUTURE</span>
            </div>
            <div className="hp-bar-title-sub">Thông minh hơn. Vận hành dễ dàng hơn. Tăng trưởng bền vững hơn.</div>
          </div>

          <div className="hp-bar-meta">
            <button className="hp-theme" onClick={() => setDark(d => !d)}
              title={dark ? 'Đổi sang sáng' : 'Đổi sang tối'}>
              <span className="hp-theme-ico">{dark ? '🌙' : '☀'}</span>
              <span>{dark ? 'Âm bản (Tối)' : 'Dương bản (Sáng)'}</span>
            </button>
            <span className="hp-status">
              <span className="hp-status-dot" /> TỨC THỜI
            </span>
            <span className="hp-clock">
              <Icon name="clock" size={13} />
              <span className="hp-clock-val">{fmtClock(now)}</span>
            </span>
          </div>
        </header>

        {/* ─── Greeting strip ──────────────────────────────────────────── */}
        <div className="hp-greet">
          <span className="hp-greet-emoji" aria-hidden>👋</span>
          <span>Chào <b>{greetingByHour()}</b> <b>{name}</b>. Tôi là Trợ lý AI riêng của bạn. Hãy cho tôi biết bạn cần hỗ trợ gì?</span>
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

        {/* ─── Mascot card ─────────────────────────────────────────────── */}
        <div className="hp-mascot-card">
          <div className="hp-mascot-ring">
            <div className="hp-mascot-orb">
              <MascotImage />
            </div>
          </div>
          <div className="hp-mascot-label">
            <div className="hp-mascot-title">TOURKIT AI</div>
            <div className="hp-mascot-sub"><span aria-hidden>🤖</span> Đang sẵn sàng tư vấn Tour &amp; Telesales!</div>
          </div>
        </div>

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
