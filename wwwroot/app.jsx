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
  const [toasts, setToasts] = uS([]);

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

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-title-row">
          <div className="app-brand">
            <div className="app-logo">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" />
                <path d="M12 8v8M8 12h8" />
              </svg>
            </div>
            <div>
              <h1 className="app-title">AI TOUR OPERATION & QUOTATION</h1>
              <p className="app-tagline">Xây dựng chương trình & tính giá tour thông minh · Tourkit v1.0</p>
            </div>
          </div>
          <nav style={{display: 'flex', gap: 8, alignItems: 'center', marginLeft: 24}}>
            <Link to="/" className="btn btn-ghost btn-sm">
              <Icon name="sparkle" size={14} /> Wizard
            </Link>
            <Link to="/customers" className="btn btn-ghost btn-sm">
              <Icon name="users" size={14} /> Khách hàng
            </Link>
            <Link to="/quotes" className="btn btn-ghost btn-sm">
              <Icon name="paper" size={14} /> Tour đã lưu
            </Link>
          </nav>
          <div className="app-actions" style={{marginLeft: 'auto'}}>
            <button className="btn btn-ghost btn-sm" onClick={() => setAiSettingsOpen(true)} title={`AI: ${aiCfg.provider} · ${aiCfg.model}`}>
              <Icon name="sparkle" size={14} /> AI: {aiCfg.model}
            </button>
            <div className="user-chip">
              <div className="user-avatar">AH</div>
              <span>Anh Hùng · Sales</span>
            </div>
          </div>
        </div>
      </header>

      {/* Router: chọn page theo hash. Thêm page = thêm <Route> ở đây. */}
      <Router>
        <Route path="/"          render={() => <window.WizardPage pushToast={pushToast} tweaks={t} />} />
        <Route path="/customers" render={() => <window.CustomersPage pushToast={pushToast} />} />
        <Route path="/quotes"    render={() => <window.QuotesPage pushToast={pushToast} />} />
        <Route path="*"          render={() => (
          <main className="page" style={{padding: 40, textAlign: 'center', color: 'var(--text-3)'}}>
            Trang không tồn tại. <Link to="/" style={{color: 'var(--accent)'}}>Về trang chính</Link>
          </main>
        )} />
      </Router>

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
