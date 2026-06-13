// core/auth.jsx — Auth TourKit DÙNG CHUNG cho TOÀN APP.
//   window.tourkitAuth  — session helpers + authedFetch (gắn X-Session-Id, 401→logout)
//   window.LoginGate    — màn đăng nhập full-screen (gate cả app, app.jsx render khi chưa login)
// Trước đây login nằm trong pages/assistant.jsx; tách ra đây để mọi feature dùng chung.

(function () {
  'use strict';

  const SESSION_KEY = 'tourkit_tk_session';
  const USER_KEY = 'tourkit_tk_user';

  const getSessionId = () => localStorage.getItem(SESSION_KEY) || null;
  const getUser = () => { try { return JSON.parse(localStorage.getItem(USER_KEY) || 'null'); } catch { return null; } };
  const isAuthed = () => !!getSessionId();

  function emit() { window.dispatchEvent(new Event('tourkit-auth-changed')); }

  function setSession(data) {
    localStorage.setItem(SESSION_KEY, data.sessionId);
    const user = { fullName: data.fullName, companyName: data.companyName, tenantId: data.tenantId };
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    emit();
    return user;
  }
  function logout() {
    localStorage.removeItem(SESSION_KEY);
    localStorage.removeItem(USER_KEY);
    emit();
  }
  function onChange(cb) {
    window.addEventListener('tourkit-auth-changed', cb);
    window.addEventListener('storage', cb);
    return () => {
      window.removeEventListener('tourkit-auth-changed', cb);
      window.removeEventListener('storage', cb);
    };
  }

  async function postLogin(url, body) {
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const data = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(data.error || ('HTTP ' + r.status));
    return setSession(data);
  }
  const login = ({ username, password, domain }) => postLogin('/api/v1/login', { username, password, domain });
  const loginToken = (token) => postLogin('/api/v1/login-token', { token });

  // Xác thực lại session với server (sau reload/restart). Trả user | null.
  async function refresh() {
    const sid = getSessionId();
    if (!sid) return null;
    try {
      const r = await fetch('/api/v1/session', { headers: { 'X-Session-Id': sid } });
      if (!r.ok) { logout(); return null; }
      const info = await r.json();
      if (info.error) { logout(); return null; }
      const user = { fullName: info.fullName, companyName: info.companyName, tenantId: info.tenantId };
      localStorage.setItem(USER_KEY, JSON.stringify(user));
      emit();
      return user;
    } catch { return getUser(); }   // mạng lỗi → giữ session local, không đá ra
  }

  // fetch gắn X-Session-Id; 401 → logout (về LoginGate); 429 → emit event để chip quota refresh.
  async function authedFetch(url, opts = {}) {
    const sid = getSessionId();
    const headers = Object.assign({}, opts.headers || {}, sid ? { 'X-Session-Id': sid } : {});
    const r = await fetch(url, Object.assign({}, opts, { headers }));
    if (r.status === 401) logout();
    if (r.status === 429) {
      // Clone để consumer phía caller vẫn đọc được body. Best-effort.
      try {
        const clone = r.clone();
        const body = await clone.json();
        if (body && body.quota) {
          window.dispatchEvent(new CustomEvent('tourkit:quota', { detail: body.quota }));
        }
      } catch { /* ignore parse fail */ }
    }
    return r;
  }

  window.tourkitAuth = {
    SESSION_KEY, USER_KEY,
    getSessionId, getUser, isAuthed, login, loginToken, logout, onChange, refresh, authedFetch,
  };

  // ─── <LoginGate> — màn đăng nhập full-screen ────────────────────────────────
  const { useState } = React;

  // Domain (tenant) lưu rời ra localStorage vì browser password manager chỉ lưu cặp (username,password).
  // Password do browser quản lý qua autocomplete attrs trên form — KHÔNG lưu vào localStorage.
  const DOMAIN_KEY = 'tourkit_tk_domain';

  function LoginGate({ onAuthed }) {
    const [mode, setMode] = useState('form');   // 'form' | 'token'
    const [domain, setDomain] = useState(() => localStorage.getItem(DOMAIN_KEY) || '');
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [token, setToken] = useState('');
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState(null);
    const [showPwd, setShowPwd] = useState(false);
    const [remember, setRemember] = useState(() => !!localStorage.getItem(DOMAIN_KEY));

    async function go(fn) {
      setBusy(true); setErr(null);
      try { const u = await fn(); onAuthed && onAuthed(u); }
      catch (e) { setErr(e.message || 'Đăng nhập thất bại'); }
      finally { setBusy(false); }
    }
    const doForm = () => {
      if (!domain.trim() || !username.trim() || !password) { setErr('Nhập đủ domain, tài khoản, mật khẩu'); return; }
      go(async () => {
        const u = await login({ username: username.trim(), password, domain: domain.trim() });
        // Tick "Ghi nhớ tôi" → lưu domain để prefill lần sau (username/password do browser quản).
        if (remember) localStorage.setItem(DOMAIN_KEY, domain.trim());
        else localStorage.removeItem(DOMAIN_KEY);
        return u;
      });
    };
    const doToken = () => {
      if (!token.trim()) { setErr('Dán token kết nối'); return; }
      go(() => loginToken(token.trim()));
    };

    return (
      <div className="login-screen">
        {/* Panel trái: <img> poster bg-login.png — đổi từ background-image sang <img>
            để dễ control responsive + lazy load + alt text. CSS .login-brand-img
            xử lý object-fit/position. Toàn bộ content đã in trong ảnh. */}
        <aside className="login-brand" aria-hidden="true">
          <img className="login-brand-img" src="/lib/bg-login.png" alt=""
            width="1024" height="1280" loading="eager" decoding="async" />
        </aside>

        {/* Form (phải) */}
        <main className="login-pane">
          <div className="login-card">
            <h2 className="login-title">Đăng nhập</h2>
            <p className="login-sub">Dùng tài khoản TourKit của bạn để vào hệ thống.</p>

            <div className="login-tabs">
              <button className={'login-tab' + (mode === 'form' ? ' on' : '')} onClick={() => { setMode('form'); setErr(null); }}>Tài khoản</button>
              <button className={'login-tab' + (mode === 'token' ? ' on' : '')} onClick={() => { setMode('token'); setErr(null); }}>Token</button>
            </div>

            {mode === 'form' ? (
              // Real <form> + autocomplete attrs → browser tự bật prompt "Lưu mật khẩu" sau submit thành công,
              // và prefill (username, password) lần truy cập sau. Domain prefill từ localStorage (DOMAIN_KEY).
              <form onSubmit={e => { e.preventDefault(); doForm(); }} autoComplete="on">
                <label className="login-label">Domain / Tenant</label>
                <input className="login-field" name="domain" autoComplete="organization"
                  placeholder="vd: tourkit-vn-xxxx" value={domain}
                  onChange={e => setDomain(e.target.value)} />
                <label className="login-label">Tài khoản</label>
                <input className="login-field" name="username" autoComplete="username"
                  placeholder="Tên đăng nhập" value={username}
                  onChange={e => setUsername(e.target.value)} />
                <label className="login-label">Mật khẩu</label>
                <div style={{position: 'relative'}}>
                  <input className="login-field" name="password" autoComplete="current-password"
                    type={showPwd ? 'text' : 'password'} placeholder="••••••••" value={password}
                    onChange={e => setPassword(e.target.value)}
                    style={{paddingRight: 40, width: '100%', boxSizing: 'border-box'}} />
                  <button type="button" tabIndex={-1}
                    onClick={() => setShowPwd(s => !s)}
                    aria-label={showPwd ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                    title={showPwd ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                    style={{position: 'absolute', right: 8, top: '50%', transform: 'translateY(-50%)',
                      background: 'transparent', border: 'none', padding: 6, cursor: 'pointer',
                      color: showPwd ? 'var(--accent, #2563eb)' : 'var(--text-3, #9ca3af)',
                      display: 'flex', alignItems: 'center'}}>
                    {showPwd ? (
                      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                        strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z"/>
                        <circle cx="12" cy="12" r="3"/>
                      </svg>
                    ) : (
                      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                        strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-10-7-10-7a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 10 7 10 7a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/>
                        <line x1="1" y1="1" x2="23" y2="23"/>
                      </svg>
                    )}
                  </button>
                </div>
                <label style={{display: 'flex', alignItems: 'center', gap: 8, fontSize: 13,
                  color: 'var(--text-2)', marginTop: 14, cursor: 'pointer'}}>
                  <input type="checkbox" checked={remember}
                    onChange={e => setRemember(e.target.checked)} />
                  Ghi nhớ tôi trên thiết bị này
                </label>
                {err && <div className="login-err">{err}</div>}
                <button className="login-btn" type="submit" disabled={busy}>{busy ? 'Đang đăng nhập…' : 'Đăng nhập'}</button>
              </form>
            ) : (
              <>
                <label className="login-label">Token kết nối</label>
                <textarea className="login-field login-token" rows={5} placeholder="Dán token (đã mã hóa) tại đây…"
                  value={token} onChange={e => setToken(e.target.value)} />
                {err && <div className="login-err">{err}</div>}
                <button className="login-btn" disabled={busy} onClick={doToken}>{busy ? 'Đang kết nối…' : 'Kết nối'}</button>
              </>
            )}

            <p className="login-note">Phiên đăng nhập giữ an toàn phía máy chủ — JWT không bao giờ xuống trình duyệt.</p>
          </div>
        </main>
      </div>
    );
  }

  window.LoginGate = LoginGate;
})();
