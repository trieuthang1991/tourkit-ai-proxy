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

  // fetch gắn X-Session-Id; 401 → logout (về LoginGate).
  async function authedFetch(url, opts = {}) {
    const sid = getSessionId();
    const headers = Object.assign({}, opts.headers || {}, sid ? { 'X-Session-Id': sid } : {});
    const r = await fetch(url, Object.assign({}, opts, { headers }));
    if (r.status === 401) logout();
    return r;
  }

  window.tourkitAuth = {
    SESSION_KEY, USER_KEY,
    getSessionId, getUser, isAuthed, login, loginToken, logout, onChange, refresh, authedFetch,
  };

  // ─── <LoginGate> — màn đăng nhập full-screen ────────────────────────────────
  const { useState } = React;

  function LoginGate({ onAuthed }) {
    const [mode, setMode] = useState('form');   // 'form' | 'token'
    const [domain, setDomain] = useState('');
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [token, setToken] = useState('');
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState(null);

    async function go(fn) {
      setBusy(true); setErr(null);
      try { const u = await fn(); onAuthed && onAuthed(u); }
      catch (e) { setErr(e.message || 'Đăng nhập thất bại'); }
      finally { setBusy(false); }
    }
    const doForm = () => {
      if (!domain.trim() || !username.trim() || !password) { setErr('Nhập đủ domain, tài khoản, mật khẩu'); return; }
      go(() => login({ username: username.trim(), password, domain: domain.trim() }));
    };
    const doToken = () => {
      if (!token.trim()) { setErr('Dán token kết nối'); return; }
      go(() => loginToken(token.trim()));
    };

    return (
      <div className="login-screen">
        {/* Panel thương hiệu (trái) */}
        <aside className="login-brand">
          <div className="login-brand-rings" aria-hidden="true" />
          <div className="login-brand-inner">
            <div className="login-logo">
              <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" /><path d="M12 8v8M8 12h8" />
              </svg>
            </div>
            <h1 className="login-brand-title">TOURKIT <span>AI Operation</span></h1>
            <p className="login-brand-tag">Vận hành tour thông minh — tạo tour, chăm sóc khách, phân tích số liệu &amp; hộp thư AI, tất cả trên dữ liệu thật của bạn.</p>
            <ul className="login-brand-feats">
              <li><span>✦</span> Wizard tạo tour ưu tiên nhà cung cấp của bạn</li>
              <li><span>✦</span> Chấm hạng &amp; chăm sóc khách hàng bằng AI</li>
              <li><span>✦</span> Trợ lý số liệu &amp; Hộp thư AI</li>
            </ul>
          </div>
          <div className="login-brand-foot">Tourkit · AI Proxy</div>
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
              <>
                <label className="login-label">Domain / Tenant</label>
                <input className="login-field" placeholder="vd: tourkit-vn-xxxx" value={domain}
                  onChange={e => setDomain(e.target.value)} />
                <label className="login-label">Tài khoản</label>
                <input className="login-field" placeholder="Tên đăng nhập" value={username}
                  onChange={e => setUsername(e.target.value)} />
                <label className="login-label">Mật khẩu</label>
                <input className="login-field" type="password" placeholder="••••••••" value={password}
                  onChange={e => setPassword(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') doForm(); }} />
                {err && <div className="login-err">{err}</div>}
                <button className="login-btn" disabled={busy} onClick={doForm}>{busy ? 'Đang đăng nhập…' : 'Đăng nhập'}</button>
              </>
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
