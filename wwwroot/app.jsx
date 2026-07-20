// app.jsx — App shell: header + nav + router + global state (tweaks, toasts, AI settings).
// Mỗi page là component riêng ở /pages/ — App KHÔNG quản lý state của page.
//
// CÁCH THÊM 1 PAGE MỚI:
//   1. Tạo file pages/<name>.jsx:
//        function MyPage({ pushToast }) { return <div>...</div>; }
//        window.MyPage = MyPage;
//   2. Add <script type="text/babel" src="pages/<name>.jsx"></script> vào index.html
//      (sau pages/wizard.jsx)
//   3. Add <Route path="/<name>" render={() => <MyPage pushToast={pushToast} />} />
//      trong <Router> dưới đây
//   4. Add <Link to="/<name>">Tên</Link> trong nav để user truy cập

const { useState: uS, useEffect: uE } = React;
const { Router, Route, Link } = window.tourkitRouter;

function userInitials(name) {
  if (!name) return '👤';
  const w = name.trim().split(/\s+/);
  return (w.slice(-2).map(x => x[0] || '').join('') || name[0] || '?').toUpperCase();
}

// Sidebar nav (style TourKit: dọc bên trái, mục active nền cam).
// '/' là landing public; /travai là trang mặc định sau đăng nhập. Wizard tour quote ở '/wizard'.
// "Chi phí AI" /ai-usage CHỈ hiện khi debug ON — page giữ accessible qua URL trực tiếp.
// Icon mapping: mỗi feature 1 icon RIÊNG (trước: 3 mục cùng 'sparkle' khó phân biệt).
// Logic: icon phản ánh động từ chính của feature, không trùng lặp.
// Sidebar nav — gom NHÓM theo phạm vi nghiệp vụ. Mỗi group có `label` (sẽ là nhãn nhỏ uppercase)
// + `items[]`. Khi sidebar thu gọn → group label ẩn, chỉ còn icon items, các group cách nhau bằng
// 1 đường mảnh để vẫn nhận biết phân tách trực quan.
const NAV_GROUPS = [
  { label: 'Tổng quan', items: [
    { to: '/travai',    icon: 'mic',     label: 'TRAVAI' },  // HUD hội thoại 3D + giọng đọc ("Trà vải") — trang mặc định sau đăng nhập
    { to: '/assistant', icon: 'chart',   label: 'Trợ lý số liệu' },   // data/chart analytics
  ]},
  { label: 'Khách hàng & Bán hàng', items: [
    { to: '/customers', icon: 'users',   label: 'Khách hàng' },       // people
    { to: '/deals',     icon: 'trend',   label: 'AI phân tích Cơ hội' },  // opportunity analysis
    { to: '/mail',      icon: 'mail',    label: 'Hộp thư AI' },       // envelope
  ]},
  { label: 'Sản phẩm Tour', items: [
    { to: '/ncc-list',     icon: 'download', label: 'AI Import NCC' },      // NCC: import + danh sách (đặt trên Tính giá Tour)
    { to: '/wizard',       icon: 'dollar', label: 'Tính giá Tour' },        // pricing/quote
    { to: '/tour-builder', icon: 'plane',  label: 'Soạn Tour GIT (AI)' },   // travel/itinerary
    { to: '/visa/history', icon: 'shield', label: 'Thẩm định Visa' },       // hub: danh sách hồ sơ đã chấm + nút mở wizard (gộp Lịch sử + Thẩm định)
  ]},
  // Khối "Tích hợp": gate theo TỪNG item bằng quyền CH_HT_XEM (Cấu hình hệ thống - Xem). widget-admin +
  // visa-config thiếu quyền → ẨN khỏi nav VÀ route trả trang "Không có quyền". Riêng /workflows KHÔNG
  // gate cứng — LUÔN hiện, tự scope "Theo người dùng" bên trong trang (WorkflowsPage).
  { label: 'Tích hợp', items: [
    { to: '/widget-admin', icon: 'sparkle', label: 'Widget Chat', requirePerm: 'CH_HT_XEM' },   // embed JS widget cho site khách
    { to: '/visa-config',  icon: 'sliders', label: 'Câu hỏi Visa', requirePerm: 'CH_HT_XEM' },  // admin tenant chỉnh wizard câu hỏi
    // R2 (BugTRAV-AI Re-Open): gate CẢ "Tự động hóa" theo CH_HT_XEM — trước để trống nên user thiếu
    // quyền (vd trang01) VẪN thấy khối "Tích hợp". Cả khối cấu hình phải yêu cầu quyền Xem cấu hình hệ thống (mirror CRM).
    { to: '/workflows',    icon: 'zap',     label: 'Tự động hóa', requirePerm: 'CH_HT_XEM' },
  ]},
];
// Route → mã quyền yêu cầu. Derived từ item.requirePerm (PER-ITEM để /workflows không bị gate cứng).
const ROUTE_REQUIRE_PERM = NAV_GROUPS
  .flatMap(g => g.items)
  .filter(it => it.requirePerm)
  .reduce((m, it) => (m[it.to] = it.requirePerm, m), {});
// Flat list — dùng cho navQuery search ở topbar (giữ logic cũ).
const NAV = NAV_GROUPS.flatMap(g => g.items);
const NAV_DEBUG_GROUP = { label: 'Debug', items: [
  { to: '/ai-usage', icon: 'chart', label: 'Chi phí AI' },
]};

// Mobile quick-nav: 5 tính năng ƯU TIÊN hiện inline ở topbar (≤900px).
// Còn lại đầy đủ trong drawer khi click hamburger (style giống sidebar desktop).
const MOBILE_QUICK = [
  { to: '/wizard',    icon: 'dollar', label: 'Wizard' },
  { to: '/assistant', icon: 'chart',  label: 'Trợ lý' },
  { to: '/customers', icon: 'users',  label: 'Khách' },
  { to: '/deals',     icon: 'zap',    label: 'Deal' },
  { to: '/visa',      icon: 'shield', label: 'Visa' },
];

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "theme": "orange",
  "aiTone": "Thân thiện, gọi Anh/Chị",
  "density": "comfortable",
  "demoLoaded": true
}/*EDITMODE-END*/;

// Trang "Không có quyền xem" — mirror text web CRM khi user vào page bị gate perm.
// Đặt ngoài App vì component nhỏ, không dùng state riêng.
function AccessDeniedPage({ need }) {
  return (
    <main className="page" style={{ padding: 60, textAlign: 'center' }}>
      <div style={{ maxWidth: 480, margin: '0 auto', color: 'var(--text)' }}>
        <div style={{ fontSize: 44, marginBottom: 16 }}>🔒</div>
        <h2 style={{ margin: '0 0 8px', fontSize: 22, fontWeight: 700 }}>Bạn không có quyền xem</h2>
        <p style={{ color: 'var(--text-3)', fontSize: 14, marginBottom: 20, lineHeight: 1.6 }}>
          Trang này yêu cầu quyền <strong style={{ color: 'var(--text)' }}>{need || '—'}</strong> (Xem cấu hình hệ thống).
          Liên hệ quản trị viên tenant để được cấp quyền, hoặc quay về trang chính.
        </p>
        <a href="/travai"
           onClick={e => { e.preventDefault(); window.tourkitRouter.navigate('/travai'); }}
           className="btn btn-primary" style={{ display: 'inline-flex', gap: 6 }}>
          <Icon name="arrowLeft" size={14} /> Về trang chính
        </a>
      </div>
    </main>
  );
}

function App() {
  // Ẩn boot splash (index.html) NGAY khi App mount lần đầu — chạy SAU paint commit.
  // Đặt đầu component để chạy bất kể flow nào (login splash / LoginGate / HomePage / app-shell).
  uE(() => {
    const s = document.getElementById('boot-splash');
    if (!s) return;
    s.classList.add('bs-out');
    setTimeout(() => s.remove(), 500);
  }, []);
  const [t, set] = window.useTweaks(TWEAK_DEFAULTS);
  const [aiSettingsOpen, setAiSettingsOpen] = uS(false);
  const [aiCfg, setAiCfg] = uS(() =>
    window.tourkit?.ai?.getConfig?.() || { provider: 'opencode-go', model: 'deepseek-v4-flash' });
  // Re-sync aiCfg sau mount + listen storage event (cross-tab sync).
  // Bug trước: nếu window.tourkit chưa ready lúc useState init → fallback default;
  // sau đó tab khác thay đổi cũng không sync → chip topbar lệch với localStorage.
  uE(() => {
    if (window.tourkit?.ai?.getConfig) setAiCfg(window.tourkit.ai.getConfig());
    const onStorage = (e) => {
      if (e.key === 'tourkit_ai_config' && window.tourkit?.ai?.getConfig) {
        setAiCfg(window.tourkit.ai.getConfig());
      }
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);
  const [toasts, setToasts] = uS([]);
  const [authUser, setAuthUser] = uS(() => window.tourkitAuth.getUser());
  const [authReady, setAuthReady] = uS(false);
  // Permissions user hiện tại — cache localStorage nạp lại từ /api/v1/permissions sau login/refresh.
  // Re-render sidebar/routes khi event 'tourkit-perms-changed' fire (lần đầu load xong).
  const [perms, setPerms] = uS(() => window.tourkitAuth.getPerms());
  uE(() => {
    const on = () => setPerms(window.tourkitAuth.getPerms());
    window.addEventListener('tourkit-perms-changed', on);
    window.addEventListener('tourkit-auth-changed', on);
    return () => {
      window.removeEventListener('tourkit-perms-changed', on);
      window.removeEventListener('tourkit-auth-changed', on);
    };
  }, []);
  const hasPerm = (code) => !code || (Array.isArray(perms) && perms.includes(code));
  // Filter NAV_GROUPS theo requirePerm — nhóm không đủ quyền bị ẨN hoàn toàn khỏi sidebar/drawer.
  // Ẩn theo TỪNG item.requirePerm; group rỗng (mọi item bị ẩn) thì bỏ. Item không có requirePerm
  // (vd /workflows) luôn hiện.
  const visibleGroups = NAV_GROUPS
    .map(g => ({ ...g, items: g.items.filter(it => hasPerm(it.requirePerm)) }))
    .filter(g => g.items.length > 0);
  // Gate 1 route theo permission: nếu route yêu cầu quyền mà user không có → render page "Không có quyền".
  // (URL vẫn giữ để user có thể chia sẻ link/bookmark; nếu được cấp quyền sau này thì reload thấy được.)
  const gatePerm = (route, node) => {
    const need = ROUTE_REQUIRE_PERM[route];
    return hasPerm(need) ? node : <AccessDeniedPage need={need} />;
  };
  // returnTo: đường dẫn cần đến sau khi đăng nhập.
  //   Ưu tiên: ?next=/path > pathname hiện tại (nếu khác '/').
  //   Lưu state + sessionStorage để giữ qua remount khi auth thay đổi.
  const [returnTo] = uS(() => {
    try {
      const sp = new URLSearchParams(window.location.search);
      const next = sp.get('next');
      if (next && next.startsWith('/')) {
        sessionStorage.setItem('tk_return_to', next); return next;
      }
      const saved = sessionStorage.getItem('tk_return_to');
      if (saved) return saved;
      const p = window.location.pathname || '/';
      if (p !== '/' && !p.startsWith('/login')) {
        sessionStorage.setItem('tk_return_to', p); return p;
      }
    } catch {}
    return null;
  });
  // Theo dõi path qua history API (router HTML5 — không còn `#`).
  const [cur, setCur] = uS(() => (window.location.pathname || '/').split('?')[0]);
  uE(() => {
    const f = () => setCur((window.location.pathname || '/').split('?')[0]);
    window.addEventListener('popstate', f);
    window.addEventListener('tourkit:navigate', f);
    return () => {
      window.removeEventListener('popstate', f);
      window.removeEventListener('tourkit:navigate', f);
    };
  }, []);
  // Launcher /home cũ đã bỏ → mọi truy cập điều hướng về trang mặc định /travai
  // (giữ để link/bookmark cũ không rơi vào "Trang không tồn tại").
  uE(() => {
    if (cur === '/home') window.tourkitRouter.navigate('/travai');
  }, [cur]);
  // Exact-match: `/visa` KHÔNG match `/visa/history` (trước dùng startsWith → 2 nav item
  // cùng active). Chấp nhận đánh đổi: route chi tiết (vd /quote-view/123) sẽ không sáng
  // nav cha — đúng vì page đó không có nav item riêng.
  // Ngoại lệ: wizard /visa thuộc mục "Thẩm định Visa" (/visa/history) → giữ active khi đang chấm hồ sơ.
  const isActive = (p) => cur === p || (p === '/visa/history' && cur === '/visa');
  const activeNav = NAV.find(n => isActive(n.to));

  // ─── Quota AI per-tenant ────────────────────────────────────────────────────
  // Fetch /api/v1/quota khi đăng nhập + refresh khi có event 'tourkit:quota' (do authedFetch
  // emit khi gặp 429 hoặc khi gọi AI thành công). Hiển thị chip "AI N/M" + cảnh báo khi >=90%.
  const [quota, setQuota] = uS(null);
  // Modal nạp quota AI — click chip .tb-quota → mở. Catalog + 3 gói + Tingee VietQR.
  const [showUpgrade, setShowUpgrade] = uS(false);
  uE(() => {
    if (!authUser) return;
    let alive = true;
    const load = async () => {
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/quota');
        if (!alive || !r.ok) return;
        const data = await r.json();
        setQuota(data);
      } catch {}
    };
    load();
    // Re-fetch khi event quota fire (sau 429 hoặc poll signal).
    const onQuota = (e) => {
      // Nếu event kèm snapshot trong detail → set thẳng, khỏi fetch.
      if (e.detail) setQuota(e.detail); else load();
    };
    window.addEventListener('tourkit:quota', onQuota);
    // Poll mỗi 10s — chip update gần realtime mà KHÔNG cần header X-Quota từ backend.
    // Phần lớn refresh trigger qua event 'tourkit:quota' (authedFetch sau AI call, ai-provider sau complete);
    // poll chỉ là backstop khi consumer không qua authedFetch (vd built-in claude).
    const t = setInterval(load, 10_000);
    return () => { alive = false; window.removeEventListener('tourkit:quota', onQuota); clearInterval(t); };
  }, [authUser]);

  // Đồng bộ chip user khi đăng nhập/đăng xuất (auth.jsx phát 'tourkit-auth-changed').
  uE(() => window.tourkitAuth.onChange(() => setAuthUser(window.tourkitAuth.getUser())), []);

  // Xác thực session với server khi mở app (sau reload/restart).
  // Hỗ trợ auto-login qua URL: ?token=ENC&next=/mail
  //   - Nếu có ?token=, gọi loginToken trước; thành công → giữ ?next= cho onAuthed redirect.
  //   - Token được strip khỏi URL ngay để không lộ qua reload/bookmark.
  uE(() => {
    let alive = true;
    (async () => {
      const sp = new URLSearchParams(window.location.search);
      const urlToken = sp.get('token');
      if (urlToken) {
        // Strip token khỏi URL trước khi login (an toàn nếu user F5 sau lỗi)
        sp.delete('token');
        const cleanUrl = window.location.pathname + (sp.toString() ? '?' + sp.toString() : '') + window.location.hash;
        window.history.replaceState({}, '', cleanUrl);
        try {
          const u = await window.tourkitAuth.loginToken(urlToken);
          if (alive) { setAuthUser(u); setAuthReady(true); }
          return;
        } catch (e) {
          // Lỗi token → tiếp tục flow refresh bình thường, user thấy LoginGate
          console.warn('[auth] Auto-login qua URL token thất bại:', e.message);
        }
      }
      try {
        const u = await window.tourkitAuth.refresh();
        if (alive) { setAuthUser(u); setAuthReady(true); }
      } catch {
        if (alive) setAuthReady(true);
      }
    })();
    return () => { alive = false; };
  }, []);

  uE(() => {
    document.body.classList.toggle('editorial', t.theme === 'editorial');
    const density = t.density === 'compact' ? 0.8 : t.density === 'cozy' ? 0.9 : 1;
    document.documentElement.style.setProperty('--density', density);
  }, [t.theme, t.density]);

  const pushToast = (text, kind = 'success') => {
    const id = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setToasts(ts => [...ts, { id, text, kind }]);
    setTimeout(() => setToasts(ts => ts.filter(x => x.id !== id)), 3000);
  };

  // Sidebar collapse — lưu localStorage để giữ trạng thái qua reload.
  // Mobile drawer (≤900px): open/close + lock body scroll
  const [mobileNav, setMobileNav] = uS(false);
  uE(() => {
    if (mobileNav) document.body.classList.add('app-nav-locked');
    else document.body.classList.remove('app-nav-locked');
    return () => document.body.classList.remove('app-nav-locked');
  }, [mobileNav]);
  // Đóng drawer khi route đổi (user click 1 mục → navigate → drawer tự đóng)
  uE(() => { setMobileNav(false); }, [cur]);

  const [sidebarCollapsed, setSidebarCollapsed] = uS(() => {
    try { return localStorage.getItem('tourkit_sidebar_collapsed') === '1'; } catch { return false; }
  });
  const toggleSidebar = () => {
    setSidebarCollapsed(prev => {
      const next = !prev;
      try { localStorage.setItem('tourkit_sidebar_collapsed', next ? '1' : '0'); } catch {}
      return next;
    });
  };
  // Phím tắt Ctrl/Cmd+B (chuẩn IDE/Notion) — KHÔNG fire khi đang gõ trong input/textarea/contenteditable.
  uE(() => {
    const onKey = (e) => {
      if (!(e.key === 'b' || e.key === 'B') || !(e.ctrlKey || e.metaKey)) return;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      e.preventDefault();
      toggleSidebar();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Header: tìm nhanh (lọc NAV → Enter điều hướng), toàn màn hình, menu user.
  const [navQuery, setNavQuery] = uS('');
  // Workflow debug toggle: ON → tự đính X-Debug:1 header vào mọi fetch → response kèm _trace
  const [debugOn, setDebugOn] = uS(() => window.tourkitDebug?.isOn?.() ?? false);
  const [userMenu, setUserMenu] = uS(false);
  const onNavSearch = (e) => {
    if (e.key !== 'Enter') return;
    const m = NAV.find(n => n.label.toLowerCase().includes(navQuery.trim().toLowerCase()));
    if (m) { window.tourkitRouter.navigate(m.to); setNavQuery(''); }
  };
  const toggleFullscreen = () => {
    if (!document.fullscreenElement) document.documentElement.requestFullscreen?.();
    else document.exitFullscreen?.();
  };

  // ─── PUBLIC route /q/{id} — bypass LoginGate. Khách bấm link Zalo/SMS không có session.
  // Render full-screen (không nav, không app shell) ngay trước khi gate auth.
  {
    const p = window.location.pathname || '/';
    const m = p.match(/^\/q\/([^\/]+)$/);
    if (m) {
      return <window.QuoteViewPage id={m[1]} />;
    }
  }

  // ─── PUBLIC landing /landing — marketing page, không cần đăng nhập.
  // Render full-screen (không sidebar, không app-shell).
  if ((cur === '/' || cur === '/landing') && window.LandingPage) {
    return <window.LandingPage />;
  }

  // ─── Gate toàn cục: chưa đăng nhập TourKit → màn login, không vào feature nào ───
  if (!authUser) {
    if (!authReady) return <div className="login-splash"><div className="login-splash-mark" /></div>;
    return <window.LoginGate onAuthed={(u) => {
      setAuthUser(u);
      // Sau login: nhảy về returnTo nếu có (ưu tiên ?next=/path > pathname trước login).
      const target = sessionStorage.getItem('tk_return_to');
      sessionStorage.removeItem('tk_return_to');
      if (target && target.startsWith('/') && target !== window.location.pathname) {
        window.tourkitRouter.navigate(target);
      } else {
        // Clean ?next= ra khỏi URL cho gọn
        const url = new URL(window.location.href);
        if (url.searchParams.has('next')) {
          url.searchParams.delete('next');
          window.history.replaceState({}, '', url.pathname + (url.search ? '?' + url.searchParams : '') + url.hash);
        }
        // Trang mặc định sau đăng nhập = /travai (thay launcher /home cũ khó hiểu).
        const p = window.location.hash ? window.location.hash.replace(/^#/, '') : window.location.pathname;
        if (p === '/' || p === '' || p === '/home' || p === '/landing') window.tourkitRouter.navigate('/travai');
      }
    }} />;
  }

  // Launcher /home cũ đã bỏ — hiện splash trong lúc effect ở trên redirect sang /travai
  // (tránh chớp "Trang không tồn tại" một frame trước khi điều hướng).
  if (cur === '/home') {
    return <div className="login-splash"><div className="login-splash-mark" /></div>;
  }

  return (
    <div className={'app-shell' + (sidebarCollapsed ? ' sidebar-collapsed' : '')}>
      {/* Sidebar trái — style TourKit (logo cam, mục active nền cam) */}
      <aside className="sidebar">
        <div className="sidebar-logo">
          <div className="sidebar-logo-mark">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" />
              <path d="M12 8v8M8 12h8" />
            </svg>
          </div>
          <div className="sidebar-logo-text">TRAV-AI<span>Trợ lý AI doanh nghiệp</span></div>
        </div>
        <nav className="sidebar-nav">
          {[...visibleGroups, ...(debugOn ? [NAV_DEBUG_GROUP] : [])].map((g, gi) => (
            <div key={gi} className="sidebar-group">
              <div className="sidebar-navlabel">{g.label}</div>
              {g.items.map(n => (
                <a key={n.to} href={n.to}
                   className={'sidebar-item' + (isActive(n.to) ? ' active' : '')}
                   title={sidebarCollapsed ? n.label : undefined}
                   onClick={e => {
                     if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                     e.preventDefault();
                     window.tourkitRouter.navigate(n.to);
                   }}>
                  <Icon name={n.icon} size={16} /> <span>{n.label}</span>
                </a>
              ))}
            </div>
          ))}
        </nav>
      </aside>

      <div className="app-main">
        <header className="topbar">
          <div className="topbar-left">
            <button className="tb-fs tb-sidebar" onClick={toggleSidebar}
              title={sidebarCollapsed ? 'Mở rộng menu (Ctrl+B)' : 'Thu gọn menu (Ctrl+B)'}
              aria-label={sidebarCollapsed ? 'Mở rộng menu' : 'Thu gọn menu'}
              aria-pressed={sidebarCollapsed}>
              <Icon name="panelLeft" size={17} stroke={2.2} />
            </button>
            <button className="tb-fs" onClick={toggleFullscreen} title="Toàn màn hình" aria-label="Toàn màn hình">
              <Icon name="maximize" size={17} stroke={2.2} />
            </button>
            <div className="tb-search">
              <Icon name="search" size={15} />
              <input placeholder="Tìm trang, tính năng…" value={navQuery}
                onChange={e => setNavQuery(e.target.value)} onKeyDown={onNavSearch} />
            </div>
          </div>
          <div className="topbar-actions">
            {quota && (
              <button type="button"
                className={'tb-quota' + (quota.exhausted ? ' is-exhausted' : (quota.warn ? ' is-warn' : ''))}
                title={quota.exhausted
                  ? `Hết quota AI: ${quota.used}/${quota.limit}. Click để nạp thêm.`
                  : `AI: ${quota.used}/${quota.limit} lượt (${quota.usedPct}%) · còn ${quota.remaining} lượt. Click để nạp thêm.`}
                onClick={() => setShowUpgrade(true)}>
                <Icon name="sparkle" size={13} />
                <span><b>{quota.used}</b>/{quota.limit}</span>
                <span className="tb-quota-plus" aria-hidden="true">+</span>
              </button>
            )}
            <button className="tb-icon" title="Hướng dẫn sử dụng"
              onClick={() => {
                const slug = (window.HELP_SLUG_BY_ROUTE || {})[window.location.pathname];
                window.tourkitRouter.navigate(slug ? '/help/' + slug : '/help');
              }}>
              <Icon name="book" size={18} />
            </button>
            <button className="tb-icon" title="Thông báo"
              onClick={() => pushToast('Chưa có thông báo mới')}>
              <Icon name="bell" size={18} />
            </button>
            {/* Debug toggle button đã bỏ — debug state vẫn giữ ở localStorage qua window.tourkitDebug
                (admin power-user set tay: window.tourkitDebug.setOn(true)) → /ai-usage vẫn accessible
                qua URL trực tiếp, nav "Chi phí AI" hiện khi debug ON. */}
            {/* AI cấu hình mặc định ở appsettings.json — ẩn nút topbar (key đã chuyển backend) */}
            {false && <button className="tb-ai" onClick={() => setAiSettingsOpen(true)} title={`AI: ${aiCfg.provider} · ${aiCfg.model}`}>
              <Icon name="sparkle" size={14} /> <span>AI: {aiCfg.model}</span>
            </button>}
            <div className="tb-userwrap">
              <button className="tb-user" onClick={() => setUserMenu(v => !v)}>
                <div className="user-avatar">{userInitials(authUser.fullName || authUser.companyName)}</div>
                <span className="tb-user-name">{authUser.fullName || authUser.companyName || 'Tài khoản'}</span>
                <Icon name="chevronDown" size={14} />
              </button>
              {userMenu && (<>
                <div className="tb-userbackdrop" onClick={() => setUserMenu(false)} />
                <div className="tb-menu">
                  <div className="tb-menu-head">
                    <div className="tb-menu-name">{authUser.fullName || 'Tài khoản'}</div>
                    {authUser.tenantId && <div className="tb-menu-sub">{authUser.tenantId}</div>}
                  </div>
                  {/* Cấu hình AI ẩn — key đã chuyển vào appsettings backend */}
                  {false && <button className="tb-menu-item" onClick={() => { setUserMenu(false); setAiSettingsOpen(true); }}>
                    <Icon name="sparkle" size={15} /> Cấu hình AI
                  </button>}
                  <button className="tb-menu-item danger"
                    onClick={async () => { setUserMenu(false); if (await window.appConfirm('Đăng xuất khỏi TourKit?', { title: 'Đăng xuất', confirmLabel: 'Đăng xuất', danger: true })) window.tourkitAuth.logout(); }}>
                    <Icon name="arrowRight" size={15} /> Đăng xuất
                  </button>
                </div>
              </>)}
            </div>
          </div>
        </header>

      {/* Router: chọn page theo hash. Thêm page = thêm <Route> ở đây. */}
      <Router>
        {/* '/' không có route ở đây: đã xử lý sớm là LandingPage (guest) / redirect /travai (đã login). */}
        <Route path="/wizard"    render={() => <window.WizardPage pushToast={pushToast} tweaks={t} />} />
        <Route path="/customers" render={() => <window.CustomersPage pushToast={pushToast} />} />
        <Route path="/assistant" render={() => <window.AssistantPage pushToast={pushToast} />} />
        <Route path="/travai"    render={() => <window.JarvisPage pushToast={pushToast} />} />
        <Route path="/jarvis"    render={() => <window.JarvisPage pushToast={pushToast} />} />{/* alias link cũ */}
        <Route path="/mail"      render={() => <window.MailPage pushToast={pushToast} />} />
        <Route path="/visa"      render={() => <window.VisaPage pushToast={pushToast} />} />
        <Route path="/visa/history" render={() => <window.VisaHistoryPage pushToast={pushToast} />} />
        <Route path="/deals"     render={() => <window.DealsPage pushToast={pushToast} />} />
        <Route path="/tour-builder" render={() => <window.TourBuilderPage pushToast={pushToast} />} />
        <Route path="/quotes"       render={() => <window.QuotesPage pushToast={pushToast} />} />
        <Route path="/ai-usage"     render={() => <window.AiUsagePage pushToast={pushToast} />} />
        <Route path="/widget-admin" render={() => gatePerm('/widget-admin', <window.WidgetAdminPage pushToast={pushToast} />)} />
        <Route path="/ncc-list"     render={() => <window.NccListPage pushToast={pushToast} />} />
        <Route path="/ncc-import"   render={() => <window.NccImportPage pushToast={pushToast} />} />
        <Route path="/visa-config"  render={() => gatePerm('/visa-config', <window.VisaConfigPage pushToast={pushToast} />)} />
        <Route path="/workflows"    render={() => gatePerm('/workflows', <window.WorkflowsPage pushToast={pushToast} />)} />
        <Route path="/help"         render={() => <window.HelpPage />} />
        <Route path="/help/:slug"   render={(p) => <window.HelpPage slug={p.slug} />} />
        <Route path="*"          render={() => (
          <main className="page" style={{padding: 40, textAlign: 'center', color: 'var(--text-3)'}}>
            Trang không tồn tại. <Link to="/" style={{color: 'var(--accent)'}}>Về trang chính</Link>
          </main>
        )} />
      </Router>
      </div>{/* /app-main */}

      {/* MOBILE BOTTOM DOCK (≤900px) — 5 quick + 1 Thêm (mở drawer) */}
      <nav className="app-bottom-dock" aria-label="Tính năng nhanh">
        {MOBILE_QUICK.map(q => (
          <a key={q.to} href={q.to}
             className={'app-dock-item' + (isActive(q.to) ? ' active' : '')}
             onClick={e => {
               if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
               e.preventDefault();
               window.tourkitRouter.navigate(q.to);
             }}>
            <Icon name={q.icon} size={18} stroke={2.2} />
            <span>{q.label}</span>
          </a>
        ))}
        <button
          className={'app-dock-item app-dock-more' + (mobileNav ? ' active' : '')}
          onClick={() => setMobileNav(v => !v)}
          aria-label="Mở menu đầy đủ"
          aria-expanded={mobileNav}>
          <Icon name="more" size={18} stroke={2.2} />
          <span>Thêm</span>
        </button>
      </nav>

      {/* MOBILE DRAWER (≤900px) — click "Thêm" → full menu giống sidebar desktop */}
      {mobileNav && (
        <div className="app-drawer-overlay" onClick={() => setMobileNav(false)} role="dialog" aria-modal="true">
          <div className="app-drawer-panel" onClick={e => e.stopPropagation()}>
            <div className="app-drawer-head">
              <div className="sidebar-logo">
                <div className="sidebar-logo-mark">
                  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" />
                    <path d="M12 8v8M8 12h8" />
                  </svg>
                </div>
                <div className="sidebar-logo-text">TRAV-AI<span>Trợ lý AI doanh nghiệp</span></div>
              </div>
              <button className="app-drawer-close" onClick={() => setMobileNav(false)} aria-label="Đóng">
                <Icon name="close" size={18} stroke={2.2} />
              </button>
            </div>
            <nav className="app-drawer-nav sidebar-nav">
              {[...visibleGroups, ...(debugOn ? [NAV_DEBUG_GROUP] : [])].map((g, gi) => (
                <div key={gi} className="sidebar-group">
                  <div className="sidebar-navlabel">{g.label}</div>
                  {g.items.map(n => (
                    <a key={n.to} href={n.to}
                       className={'sidebar-item' + (isActive(n.to) ? ' active' : '')}
                       onClick={e => {
                         if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                         e.preventDefault();
                         window.tourkitRouter.navigate(n.to);
                         setMobileNav(false);
                       }}>
                      <Icon name={n.icon} size={16} /> <span>{n.label}</span>
                    </a>
                  ))}
                </div>
              ))}
            </nav>
          </div>
        </div>
      )}

      <div className="toast-container">
        {toasts.map(t => (
          <div key={t.id} className={`toast ${t.kind}`}>
            <Icon name="check" size={14} stroke={2.5} /> {t.text}
          </div>
        ))}
      </div>

      {window.AISettingsDialog && <window.AISettingsDialog
        open={aiSettingsOpen}
        onClose={() => setAiSettingsOpen(false)}
        onSaved={(cfg) => {
          setAiCfg(cfg);
          pushToast(`AI: ${cfg.provider} · ${cfg.model}`);
        }}
      />}

      {window.QuotaUpgradeModal && <window.QuotaUpgradeModal
        open={showUpgrade}
        onClose={() => setShowUpgrade(false)}
        onPaid={(snap) => {
          // Sync ngay chip quota (event đã được modal phát) + toast confirm.
          pushToast(`Đã cộng ${snap.addedUnits.toLocaleString('vi-VN')} lượt AI vào tài khoản!`, 'info');
        }}
      />}

      {window.TweaksPanel && (
        <window.TweaksPanel title="Tweaks">
          <window.TweakSection label="Visual">
            <window.TweakRadio label="Color" value={t.theme} options={[
              { value: 'orange', label: 'Orange' },
              { value: 'editorial', label: 'Editorial' }
            ]} onChange={v => set('theme', v)} />
            <window.TweakRadio label="Density" value={t.density} options={[
              { value: 'compact', label: 'Compact' },
              { value: 'cozy', label: 'Cozy' },
              { value: 'comfortable', label: 'Roomy' }
            ]} onChange={v => set('density', v)} />
          </window.TweakSection>
          <window.TweakSection label="AI Behavior">
            <window.TweakSelect label="Tông giọng" value={t.aiTone} options={[
              { value: 'Thân thiện, gọi Anh/Chị', label: 'Thân thiện' },
              { value: 'Chuyên nghiệp, ngắn gọn', label: 'Chuyên nghiệp' },
              { value: 'Sales hăng hái, đầy nhiệt huyết', label: 'Sales hăng hái' }
            ]} onChange={v => set('aiTone', v)} />
          </window.TweakSection>
        </window.TweaksPanel>
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
