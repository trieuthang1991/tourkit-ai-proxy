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
// '/' giờ là Home Launcher (pages/home.jsx); Wizard tour quote chuyển sang '/wizard'.
const NAV = [
  { to: '/',          icon: 'sparkle', label: 'Trang chủ' },
  { to: '/assistant', icon: 'sparkle', label: 'Trợ lý số liệu' },
  { to: '/wizard',    icon: 'sparkle', label: 'Tính giá Tour' },
  { to: '/customers', icon: 'users',   label: 'Khách hàng' },
  { to: '/deals',     icon: 'trend',   label: 'Ưu tiên Deal AI' },
  { to: '/mail',      icon: 'paper',   label: 'Hộp thư AI' },
  { to: '/visa',      icon: 'shield',  label: 'Thẩm định Visa' },
  { to: '/tour-builder', icon: 'pin',  label: 'Soạn Tour GIT (AI)' },
  { to: '/ai-usage',     icon: 'chart',  label: 'Chi phí AI' },
];

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "theme": "orange",
  "aiTone": "Thân thiện, gọi Anh/Chị",
  "density": "comfortable",
  "demoLoaded": true
}/*EDITMODE-END*/;

function App() {
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
  const isActive = (p) => p === '/' ? cur === '/' : cur.startsWith(p);
  const activeNav = NAV.find(n => isActive(n.to));

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
      }
    }} />;
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
          <div className="sidebar-logo-text">TOURKIT<span>AI Operation</span></div>
        </div>
        <div className="sidebar-navlabel">Tính năng</div>
        <nav className="sidebar-nav">
          {NAV.map(n => (
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
        </nav>
        {/* Toggle thu/mở ở chân sidebar (Linear/Notion-style) */}
        <button className="sidebar-toggle" onClick={toggleSidebar}
          title={sidebarCollapsed ? 'Mở rộng menu (Ctrl+B)' : 'Thu gọn menu (Ctrl+B)'}
          aria-label={sidebarCollapsed ? 'Mở rộng menu' : 'Thu gọn menu'}
          aria-pressed={sidebarCollapsed}>
          <Icon name={sidebarCollapsed ? 'chevronRight' : 'chevronLeft'} size={15} stroke={2.4} />
          <span>{sidebarCollapsed ? 'Mở rộng' : 'Thu gọn'}</span>
        </button>
      </aside>

      <div className="app-main">
        <header className="topbar">
          <div className="topbar-left">
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
            <button className="tb-icon" title="Hướng dẫn sử dụng"
              onClick={() => pushToast('Chọn tính năng ở thanh bên trái để bắt đầu')}>
              <Icon name="book" size={18} />
            </button>
            <button className="tb-icon" title="Thông báo"
              onClick={() => pushToast('Chưa có thông báo mới')}>
              <Icon name="bell" size={18} />
            </button>
            <button className="tb-ai" onClick={() => setAiSettingsOpen(true)} title={`AI: ${aiCfg.provider} · ${aiCfg.model}`}>
              <Icon name="sparkle" size={14} /> <span>AI: {aiCfg.model}</span>
            </button>
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
                  <button className="tb-menu-item" onClick={() => { setUserMenu(false); setAiSettingsOpen(true); }}>
                    <Icon name="sparkle" size={15} /> Cấu hình AI
                  </button>
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
        <Route path="/"          render={() => <window.HomePage pushToast={pushToast} />} />
        <Route path="/wizard"    render={() => <window.WizardPage pushToast={pushToast} tweaks={t} />} />
        <Route path="/customers" render={() => <window.CustomersPage pushToast={pushToast} />} />
        <Route path="/assistant" render={() => <window.AssistantPage pushToast={pushToast} />} />
        <Route path="/mail"      render={() => <window.MailPage pushToast={pushToast} />} />
        <Route path="/visa"      render={() => <window.VisaPage pushToast={pushToast} />} />
        <Route path="/deals"     render={() => <window.DealsPage pushToast={pushToast} />} />
        <Route path="/tour-builder" render={() => <window.TourBuilderPage pushToast={pushToast} />} />
        <Route path="/ai-usage"     render={() => <window.AiUsagePage pushToast={pushToast} />} />
        <Route path="*"          render={() => (
          <main className="page" style={{padding: 40, textAlign: 'center', color: 'var(--text-3)'}}>
            Trang không tồn tại. <Link to="/" style={{color: 'var(--accent)'}}>Về trang chính</Link>
          </main>
        )} />
      </Router>
      </div>{/* /app-main */}

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
