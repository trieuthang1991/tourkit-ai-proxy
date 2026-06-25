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
    return <AdminShell username={state.username} onLogout={async () => {
      try { await adminFetch("/api/v1/admin/auth/logout", { method: "POST" }); } catch {}
      clearSession();
      check();
    }} />;
  }

  // ── Nav config — thêm trang admin mới = push 1 entry vào đây ───────────────
  // (Đầu file vì tham chiếu component declarations bên dưới; nhưng JS hoisting
  // function declarations → OK đặt trước.)
  const ADMIN_NAV = [
    { path: "ai-usage",      label: "AI Usage",        icon: "📊", component: AiUsagePage },
    { path: "quota",         label: "Quota",           icon: "💎", component: QuotaPage },
    { path: "consult-leads", label: "Đăng ký tư vấn",  icon: "📞", component: ConsultLeadsPage },
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

  // ────── Trang Quota — list tenant + top-up qua prompt() ─────────────────────
  function QuotaPage() {
    const [items, setItems] = useState([]);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState("");

    async function load() {
      setLoading(true); setError("");
      try {
        const r = await window.adminFetch("/api/v1/admin/ui/quota");
        const data = await r.json();
        if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
        setItems(data.items || []);
      } catch (e) {
        setError(e.message || "Lỗi tải dữ liệu");
      } finally {
        setLoading(false);
      }
    }

    useEffect(() => { load(); }, []);

    async function onTopUp(tenantId, displayName) {
      const raw = window.prompt(`Cộng bao nhiêu lượt cho "${displayName}"?`, "100");
      if (raw == null) return;
      const amount = parseInt(raw, 10);
      if (!Number.isInteger(amount) || amount < 1) {
        alert("Số lượt phải là số nguyên ≥ 1");
        return;
      }
      try {
        const r = await window.adminFetch(`/api/v1/admin/ui/quota/${encodeURIComponent(tenantId)}/topup`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ amount })
        });
        const data = await r.json();
        if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
        await load();
      } catch (e) {
        alert(e.message || "Top-up thất bại");
      }
    }

    if (loading && items.length === 0) return <div className="ai-usage-loading">Đang tải…</div>;
    if (error) return <div className="ai-usage-error">⚠️ {error}</div>;

    return (
      <div>
        <div className="ai-usage-header">
          <h1 className="ai-usage-title">Quota AI · Tenant</h1>
          <button className="ai-usage-range-btn" onClick={load} disabled={loading}>↻ Refresh</button>
        </div>
        <div className="quota-section">
          <table className="quota-table">
            <thead>
              <tr>
                <th>#</th><th>Tenant</th><th>Used / Limit</th><th>% Used</th><th>Remaining</th><th></th>
              </tr>
            </thead>
            <tbody>
              {items.map((t, i) => {
                const pct = t.usedPct ?? 0;
                const color = pct >= 90 ? "red" : pct >= 70 ? "amber" : "green";
                return (
                  <tr key={t.tenantId || `(empty-${i})`}>
                    <td className="quota-rank">{i + 1}</td>
                    <td>
                      <div className="quota-name">{t.displayName || "(system)"}</div>
                      <div className="quota-tid">{t.tenantId}</div>
                    </td>
                    <td className="quota-num">{fmtNum(t.used)} / {fmtNum(t.limit)}</td>
                    <td className="quota-pct-cell">
                      <div className={`quota-pct-bar quota-pct-${color}`}>
                        <div className="quota-pct-fill" style={{ width: `${Math.min(100, pct)}%` }} />
                      </div>
                      <span className="quota-pct-num">{pct}%</span>
                    </td>
                    <td className="quota-num">{fmtNum(t.remaining)}</td>
                    <td>
                      <button
                        className="ai-usage-range-btn quota-btn-sm"
                        onClick={() => onTopUp(t.tenantId, t.displayName || t.tenantId)}
                      >+ Top-up</button>
                    </td>
                  </tr>
                );
              })}
              {items.length === 0 && (
                <tr><td colSpan={6} className="quota-empty">Chưa có tenant nào dùng AI.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  // ────── Trang Đăng ký tư vấn — đọc data/consult-leads.jsonl + status side-car ─
  function ConsultLeadsPage() {
    const [filter, setFilter] = useState("pending");   // pending | contacted | all
    const [search, setSearch] = useState("");
    const [resp, setResp] = useState({ items: [], totals: { all: 0, pending: 0, contacted: 0 } });
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState("");
    const [busyId, setBusyId] = useState(null);

    async function load() {
      setLoading(true); setError("");
      try {
        const qs = new URLSearchParams({ status: filter });
        const r = await window.adminFetch("/api/v1/admin/ui/consult-leads?" + qs.toString());
        const data = await r.json();
        if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
        setResp(data);
      } catch (e) {
        setError(e.message || "Lỗi tải dữ liệu");
      } finally {
        setLoading(false);
      }
    }

    useEffect(() => { load(); }, [filter]);

    async function toggleContacted(row) {
      setBusyId(row.id);
      try {
        const r = await window.adminFetch(
          `/api/v1/admin/ui/consult-leads/${encodeURIComponent(row.id)}/contacted`, {
            method: "POST",
            body: JSON.stringify({ contacted: !row.contacted })
          });
        const data = await r.json();
        if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
        await load();
      } catch (e) {
        alert(e.message || "Cập nhật thất bại");
      } finally {
        setBusyId(null);
      }
    }

    async function copyPhone(phone) {
      try {
        await navigator.clipboard.writeText(phone);
      } catch { /* clipboard có thể bị block khi không HTTPS — ignore */ }
    }

    // Search client-side để khỏi round-trip mỗi keystroke. Dataset nhỏ (vài trăm lead).
    const q = search.trim().toLowerCase();
    const rows = q
      ? resp.items.filter(r =>
          (r.fullName || "").toLowerCase().includes(q) ||
          (r.phone    || "").toLowerCase().includes(q) ||
          (r.email    || "").toLowerCase().includes(q) ||
          (r.company  || "").toLowerCase().includes(q) ||
          (r.feature  || "").toLowerCase().includes(q))
      : resp.items;

    return (
      <div>
        <div className="ai-usage-header">
          <h1 className="ai-usage-title">Đăng ký tư vấn</h1>
          <button className="ai-usage-range-btn" onClick={load} disabled={loading}>↻ Refresh</button>
        </div>

        <div className="ai-usage-filters" style={{ marginBottom: 16 }}>
          <div className="ai-usage-range">
            {[
              { key: "pending",   label: `Chưa liên hệ (${resp.totals.pending})` },
              { key: "contacted", label: `Đã liên hệ (${resp.totals.contacted})` },
              { key: "all",       label: `Tất cả (${resp.totals.all})` },
            ].map(t => (
              <button key={t.key}
                className={"ai-usage-tab" + (t.key === filter ? " active" : "")}
                onClick={() => setFilter(t.key)}>
                {t.label}
              </button>
            ))}
          </div>
          <input
            type="search"
            placeholder="Tìm tên / SĐT / email / công ty / tính năng…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            style={{
              flex: "1 1 280px", minWidth: 220,
              padding: "8px 12px", fontSize: 13,
              border: "1px solid var(--border-warm)", borderRadius: 6,
              background: "var(--bg-surface)", color: "var(--text-primary)",
              fontFamily: "var(--sans)"
            }}
          />
        </div>

        {error && <div className="ai-usage-error">⚠️ {error}</div>}
        {loading && rows.length === 0 && <div className="ai-usage-loading">Đang tải…</div>}

        <div className="quota-section">
          <table className="quota-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Khách</th>
                <th>SĐT</th>
                <th>Email / Công ty</th>
                <th>Tính năng quan tâm</th>
                <th>Ghi chú</th>
                <th>Đăng ký lúc</th>
                <th>Trạng thái</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={r.id}>
                  <td className="quota-rank">{i + 1}</td>
                  <td>
                    <div className="quota-name">{r.fullName || "(không tên)"}</div>
                    {r.ip && <div className="quota-tid">IP {r.ip}</div>}
                  </td>
                  <td>
                    <button
                      onClick={() => copyPhone(r.phone)}
                      title="Click để copy SĐT"
                      style={{
                        background: "none", border: "none", padding: 0,
                        cursor: "pointer", color: "var(--accent-deep)",
                        fontFamily: "var(--mono)", fontSize: 13,
                        textDecoration: "underline dotted"
                      }}
                    >{r.phone}</button>
                  </td>
                  <td>
                    {r.email && <div style={{ fontSize: 13 }}>{r.email}</div>}
                    {r.company && <div className="quota-tid">{r.company}</div>}
                    {!r.email && !r.company && <span style={{ color: "var(--text-faint)" }}>—</span>}
                  </td>
                  <td>
                    {r.feature
                      ? <span style={{
                          fontSize: 12, padding: "3px 8px", borderRadius: 999,
                          background: "var(--accent-tint)", color: "var(--accent-deep)",
                          fontWeight: 500
                        }}>{r.feature}</span>
                      : <span style={{ color: "var(--text-faint)" }}>—</span>}
                  </td>
                  <td style={{ maxWidth: 280, whiteSpace: "normal", lineHeight: 1.4, color: "var(--text-muted)", fontSize: 12 }}>
                    {r.note || <span style={{ color: "var(--text-faint)" }}>—</span>}
                  </td>
                  <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>
                    {fmtDate(r.createdUtc)}
                  </td>
                  <td>
                    {r.contacted
                      ? <span title={r.contactedBy ? `bởi ${r.contactedBy} · ${fmtDate(r.contactedUtc)}` : ""}
                              style={{
                                fontSize: 11, padding: "3px 8px", borderRadius: 999,
                                background: "rgba(77,124,92,0.12)", color: "#3F6B4E",
                                fontWeight: 600, letterSpacing: 0.3, textTransform: "uppercase"
                              }}>✓ Đã liên hệ</span>
                      : <span style={{
                              fontSize: 11, padding: "3px 8px", borderRadius: 999,
                              background: "rgba(180,83,9,0.12)", color: "#B45309",
                              fontWeight: 600, letterSpacing: 0.3, textTransform: "uppercase"
                            }}>● Chưa liên hệ</span>}
                  </td>
                  <td>
                    <button
                      className="ai-usage-range-btn quota-btn-sm"
                      onClick={() => toggleContacted(r)}
                      disabled={busyId === r.id}
                    >{r.contacted ? "↩ Bỏ đánh dấu" : "✓ Đã liên hệ"}</button>
                  </td>
                </tr>
              ))}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={9} className="quota-empty">
                  {q ? `Không tìm thấy lead khớp "${search}".`
                     : (filter === "pending"   ? "🎉 Không còn lead chưa liên hệ."
                     :  filter === "contacted" ? "Chưa có lead nào được đánh dấu đã liên hệ."
                     : "Chưa có ai đăng ký tư vấn.")}
                </td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  // Expose adminFetch để page components dùng (Task 8+)
  window.adminFetch = adminFetch;

  const root = ReactDOM.createRoot(document.getElementById("admin-root"));
  root.render(<AdminAuthGate />);
})();
