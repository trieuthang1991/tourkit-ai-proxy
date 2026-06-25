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
    return <AdminShell username={state.username} onLogout={() => { clearSession(); check(); }} />;
  }

  // ── Nav config — thêm trang admin mới = push 1 entry vào đây ───────────────
  // (Đầu file vì tham chiếu component declarations bên dưới; nhưng JS hoisting
  // function declarations → OK đặt trước.)
  const ADMIN_NAV = [
    { path: "ai-usage", label: "AI Usage", icon: "📊", component: AiUsagePage },
  ];
  const DEFAULT_PATH = "ai-usage";

  // ── Sub-router: đọc location.pathname, push/pop state ──────────────────────
  function useAdminRoute() {
    const [path, setPath] = useState(() => extractPath(location.pathname));
    useEffect(() => {
      function onPop() { setPath(extractPath(location.pathname)); }
      window.addEventListener("popstate", onPop);
      return () => window.removeEventListener("popstate", onPop);
    }, []);
    function navigate(p) {
      const full = "/admin-trav-ai/" + p;
      if (location.pathname !== full) history.pushState({}, "", full);
      setPath(p);
    }
    return [path, navigate];
  }

  function extractPath(pathname) {
    const m = pathname.match(/^\/admin-trav-ai\/?(.*)$/);
    if (!m) return DEFAULT_PATH;
    const rest = (m[1] || "").replace(/\/$/, "");
    return rest || DEFAULT_PATH;
  }

  // ── Shell: sidebar + topbar + content ─────────────────────────────────────
  function AdminShell({ username, onLogout }) {
    const [path, navigate] = useAdminRoute();
    const current = ADMIN_NAV.find(n => n.path === path) || ADMIN_NAV[0];
    const Page = current.component;

    return (
      <div className="admin-shell">
        <aside className="admin-sidebar">
          <div className="admin-brand">TRAV-AI<br/><small>Admin</small></div>
          <nav>
            {ADMIN_NAV.map(item => (
              <a key={item.path}
                 href={"/admin-trav-ai/" + item.path}
                 onClick={e => { e.preventDefault(); navigate(item.path); }}
                 className={"admin-nav-item" + (item.path === current.path ? " active" : "")}>
                <span className="admin-nav-icon">{item.icon}</span>
                <span>{item.label}</span>
              </a>
            ))}
          </nav>
        </aside>
        <main className="admin-main">
          <header className="admin-topbar">
            <div className="admin-topbar-title">{current.label}</div>
            <div className="admin-topbar-right">
              <span className="admin-topbar-user">👤 {username}</span>
              <button className="admin-topbar-logout" onClick={onLogout}>Đăng xuất</button>
            </div>
          </header>
          <section className="admin-content">
            <Page />
          </section>
        </main>
      </div>
    );
  }

  // ── AI Usage page ─────────────────────────────────────────────────────────
  function fmtVnd(n) {
    if (n == null) return "—";
    return new Intl.NumberFormat("vi-VN").format(Math.round(n)) + " ₫";
  }
  function fmtNum(n) {
    if (n == null) return "0";
    return new Intl.NumberFormat("vi-VN").format(n);
  }
  function fmtDate(s) {
    if (!s) return "—";
    try {
      const d = new Date(s);
      return d.toLocaleString("vi-VN", { dateStyle: "short", timeStyle: "short" });
    } catch { return s; }
  }

  function TenantsTable({ rows, activeTenant, onPick }) {
    if (!rows || rows.length === 0)
      return <div className="ai-usage-placeholder">Không có dữ liệu tenant.</div>;
    return (
      <div className="ai-usage-section">
        <h3 className="ai-usage-section-title">🏢 Top tenants</h3>
        <div className="ai-usage-table-wrap">
          <table className="ai-usage-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Tenant</th>
                <th className="num">Số call</th>
                <th className="num">Chi phí</th>
                <th className="num">% share</th>
                <th>Last call</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((t, i) => (
                <tr key={t.tenantId}
                    className={(t.tenantId === activeTenant ? "active " : "") + "clickable"}
                    onClick={() => onPick(t.tenantId === activeTenant ? null : t.tenantId)}
                    title={t.tenantId === activeTenant ? "Click để bỏ filter" : "Click để xem chi tiết tenant này"}>
                  <td>{i + 1}</td>
                  <td>
                    <div className="tenant-name">{t.tenantName}</div>
                    <div className="tenant-id">{t.tenantId}</div>
                  </td>
                  <td className="num">{fmtNum(t.calls)}</td>
                  <td className="num">{fmtVnd(t.costVnd)}</td>
                  <td className="num">{(t.sharePct ?? 0).toFixed(1)}%</td>
                  <td>{fmtDate(t.lastCallAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  function ModelTable({ rows }) {
    if (!rows || rows.length === 0)
      return <div className="ai-usage-placeholder">Không có dữ liệu model.</div>;
    return (
      <div className="ai-usage-section">
        <h3 className="ai-usage-section-title">🤖 By model</h3>
        <div className="ai-usage-table-wrap">
          <table className="ai-usage-table">
            <thead>
              <tr>
                <th>Model</th>
                <th className="num">Số call</th>
                <th className="num">Input tokens</th>
                <th className="num">Output tokens</th>
                <th className="num">Chi phí</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(m => (
                <tr key={m.model}>
                  <td><code>{m.model}</code></td>
                  <td className="num">{fmtNum(m.calls)}</td>
                  <td className="num">{fmtNum(m.inTokens)}</td>
                  <td className="num">{fmtNum(m.outTokens)}</td>
                  <td className="num">{fmtVnd(m.costVnd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  function DailyChart({ rows }) {
    const canvasRef = React.useRef(null);
    const chartRef = React.useRef(null);

    useEffect(() => {
      let cancelled = false;
      async function render() {
        if (!rows || rows.length === 0) return;
        // chart-loader.js phơi ra window.ensureChart() (lazy load Chart.js).
        if (!window.ensureChart) { console.warn("ensureChart missing"); return; }
        await window.ensureChart();
        if (cancelled || !canvasRef.current) return;
        if (chartRef.current) { chartRef.current.destroy(); chartRef.current = null; }
        const ctx = canvasRef.current.getContext("2d");
        chartRef.current = new window.Chart(ctx, {
          type: "line",
          data: {
            labels: rows.map(r => r.date),
            datasets: [{
              label: "Chi phí (VND)",
              data: rows.map(r => r.costVnd),
              borderColor: "#F97316",
              backgroundColor: "rgba(249,115,22,0.12)",
              fill: true,
              tension: 0.25,
              pointRadius: 3
            }]
          },
          options: {
            responsive: true, maintainAspectRatio: false,
            plugins: {
              legend: { display: false },
              tooltip: {
                callbacks: {
                  label: (ctx) => {
                    const i = ctx.dataIndex;
                    const r = rows[i];
                    return ` ${fmtVnd(r.costVnd)} · ${fmtNum(r.calls)} call`;
                  }
                }
              }
            },
            scales: {
              y: {
                beginAtZero: true,
                ticks: { callback: (v) => new Intl.NumberFormat("vi-VN").format(v) }
              }
            }
          }
        });
      }
      render();
      return () => {
        cancelled = true;
        if (chartRef.current) { chartRef.current.destroy(); chartRef.current = null; }
      };
    }, [rows]);

    if (!rows || rows.length === 0)
      return <div className="ai-usage-placeholder">Không có dữ liệu daily.</div>;

    return (
      <div className="ai-usage-section">
        <h3 className="ai-usage-section-title">📈 Chi phí theo ngày</h3>
        <div className="ai-usage-chart"><canvas ref={canvasRef} /></div>
      </div>
    );
  }

  function AiUsagePage() {
    const [days, setDays] = useState(30);
    const [tenantFilter, setTenantFilter] = useState(null);
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState("");

    async function load() {
      setLoading(true); setError("");
      try {
        const qs = new URLSearchParams({ days: String(days) });
        if (tenantFilter) qs.set("tenantId", tenantFilter);
        const res = await window.adminFetch("/api/v1/admin/ui/ai-usage?" + qs.toString());
        if (!res.ok) {
          const err = await res.json().catch(() => ({}));
          throw new Error(err.error || "Lỗi tải dữ liệu");
        }
        setData(await res.json());
      } catch (ex) {
        setError(ex.message || String(ex));
      } finally {
        setLoading(false);
      }
    }

    useEffect(() => { load(); }, [days, tenantFilter]);

    return (
      <div className="ai-usage-page">
        <div className="ai-usage-filters">
          <div className="ai-usage-range">
            {[7, 30, 90].map(d => (
              <button key={d}
                className={"ai-usage-tab" + (d === days ? " active" : "")}
                onClick={() => setDays(d)}>
                {d} ngày
              </button>
            ))}
          </div>
          {tenantFilter && (
            <div className="ai-usage-tenant-pill">
              Đang xem tenant: <b>{tenantFilter}</b>
              <button onClick={() => setTenantFilter(null)}>Xem tất cả ×</button>
            </div>
          )}
        </div>

        {loading && <div className="ai-usage-loading">Đang tải…</div>}
        {error && <div className="ai-usage-error">{error} <button onClick={load}>Thử lại</button></div>}

        {data && !loading && (
          <>
            <div className="ai-usage-stats">
              <div className="ai-usage-stat">
                <div className="ai-usage-stat-label">Số call</div>
                <div className="ai-usage-stat-value">{fmtNum(data.totals.calls)}</div>
              </div>
              <div className="ai-usage-stat">
                <div className="ai-usage-stat-label">Input tokens</div>
                <div className="ai-usage-stat-value">{fmtNum(data.totals.inTokens)}</div>
              </div>
              <div className="ai-usage-stat">
                <div className="ai-usage-stat-label">Output tokens</div>
                <div className="ai-usage-stat-value">{fmtNum(data.totals.outTokens)}</div>
              </div>
              <div className="ai-usage-stat">
                <div className="ai-usage-stat-label">Tổng chi phí</div>
                <div className="ai-usage-stat-value">{fmtVnd(data.totals.costVnd)}</div>
              </div>
            </div>
            <TenantsTable
              rows={data.byTenant}
              activeTenant={tenantFilter}
              onPick={setTenantFilter} />
            <ModelTable rows={data.byModel} />
            <DailyChart rows={data.byDay} />
          </>
        )}
      </div>
    );
  }

  // Expose adminFetch để page components dùng (Task 8+)
  window.adminFetch = adminFetch;

  const root = ReactDOM.createRoot(document.getElementById("admin-root"));
  root.render(<AdminAuthGate />);
})();
