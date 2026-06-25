// Admin shell — /admin-trav-ai/*
// Single-file React app: shell + login + sub-router + tất cả page components.
(function () {
  const { useState, useEffect } = React;

  const SESSION_KEY = "tkai_admin_session";

  // ── Session helpers ────────────────────────────────────────────────────────
  function loadSession() {
    try {
      const raw = localStorage.getItem(SESSION_KEY);
      if (!raw) return null;
      const s = JSON.parse(raw);
      if (!s.token || !s.username) return null;
      return s;
    } catch { return null; }
  }
  function saveSession(s) { localStorage.setItem(SESSION_KEY, JSON.stringify(s)); }
  function clearSession() { localStorage.removeItem(SESSION_KEY); }

  // Fetch wrapper auto-attach X-Admin-Session. 401 → clear + reload (về login).
  async function adminFetch(url, opts = {}) {
    const s = loadSession();
    const headers = new Headers(opts.headers || {});
    if (s?.token) headers.set("X-Admin-Session", s.token);
    if (opts.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    const res = await fetch(url, { ...opts, headers });
    if (res.status === 401) {
      clearSession();
      window.location.reload();
      throw new Error("unauthorized");
    }
    return res;
  }

  // ── Login form ─────────────────────────────────────────────────────────────
  function AdminLogin({ onLoggedIn }) {
    const [username, setUsername] = useState("");
    const [password, setPassword] = useState("");
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");

    async function submit(e) {
      e?.preventDefault?.();
      setError("");
      setBusy(true);
      try {
        const res = await fetch("/api/v1/admin/auth/login", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ username: username.trim(), password })
        });
        const data = await res.json();
        if (!res.ok) {
          setError(data?.error || "Login fail");
          return;
        }
        saveSession({ token: data.token, username: data.username });
        onLoggedIn();
      } catch (ex) {
        setError(ex.message || "Lỗi mạng");
      } finally {
        setBusy(false);
      }
    }

    return (
      <div className="admin-login-wrap">
        <form className="admin-login-card" onSubmit={submit}>
          <h1>TRAV-AI · Admin</h1>
          <p className="admin-login-sub">Đăng nhập để vào hệ quản trị</p>
          <label>Username</label>
          <input value={username} onChange={e => setUsername(e.target.value)} autoFocus />
          <label>Password</label>
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} />
          {error && <div className="admin-login-error">{error}</div>}
          <button type="submit" disabled={busy || !username || !password}>
            {busy ? "Đang đăng nhập…" : "Đăng nhập"}
          </button>
        </form>
      </div>
    );
  }

  // ── Auth gate: validate session ở mount, switch giữa login/shell ───────────
  function AdminAuthGate() {
    const [state, setState] = useState({ status: "checking", username: null });

    async function check() {
      const s = loadSession();
      if (!s) { setState({ status: "anonymous", username: null }); return; }
      try {
        const res = await fetch("/api/v1/admin/auth/me", {
          headers: { "X-Admin-Session": s.token }
        });
        if (!res.ok) {
          clearSession();
          setState({ status: "anonymous", username: null });
          return;
        }
        const data = await res.json();
        setState({ status: "authed", username: data.username });
      } catch {
        setState({ status: "anonymous", username: null });
      }
    }

    useEffect(() => { check(); }, []);

    if (state.status === "checking")
      return <div className="admin-loading">Đang kiểm tra phiên…</div>;
    if (state.status === "anonymous")
      return <AdminLogin onLoggedIn={check} />;
    return <AdminShellPlaceholder username={state.username} onLogout={() => { clearSession(); check(); }} />;
  }

  // Placeholder — Task 7 thay bằng AdminShell thật
  function AdminShellPlaceholder({ username, onLogout }) {
    return (
      <div className="admin-loading">
        Logged in as <b>{username}</b><br/>
        <button onClick={onLogout}>Logout</button>
      </div>
    );
  }

  // Expose adminFetch để page components dùng (Task 8+)
  window.adminFetch = adminFetch;

  const root = ReactDOM.createRoot(document.getElementById("admin-root"));
  root.render(<AdminAuthGate />);
})();
