# Admin Quota Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trang admin `/admin-trav-ai/quota` cho phép xem quota AI mọi tenant + top-up trực tiếp từ UI, gate bằng `X-Admin-Session` (không dùng `Admin:Token` legacy).

**Architecture:** Backend reuse `TenantQuotaStore.ListAll()` + `TopUp()` đã có; expose 2 endpoint UI mới dưới `/api/v1/admin/ui/quota*` group (đã có `.RequireAdminSession()` ở group level — xem `Endpoints/AdminUiEndpoints.cs`). Frontend thêm 1 component `QuotaPage` vào file đơn `wwwroot/pages/admin.jsx` + push entry mới vào `ADMIN_NAV` array. Dialog top-up = native `prompt()` để giữ scope nhỏ (1 input số, không cần modal phức tạp).

**Tech Stack:** ASP.NET Core 8 Minimal API + Dapper (sẵn); React 18 + Babel standalone (sẵn).

---

## File Structure

**Modify:**
- `Endpoints/AdminUiEndpoints.cs` — thêm 2 route `GET /quota` + `POST /quota/{tenant}/topup` vào group `admin/ui` đã có.
- `wwwroot/pages/admin.jsx` — thêm `QuotaPage` component (~70 dòng) + push entry vào `ADMIN_NAV`.
- `wwwroot/admin.css` — thêm style `.quota-*` cho table + pct bar (~30 dòng).
- `CLAUDE.md` — 2 row mới ở API table.

**Không tạo file mới.** Backend reuse: `TenantQuotaStore` (DI singleton, có sẵn), `TkSessionRepository.GetTenantNamesAsync` (đã có, dùng cho AI Usage page).

---

### Task 1: Backend — `GET /api/v1/admin/ui/quota`

**Files:**
- Modify: `Endpoints/AdminUiEndpoints.cs`

Endpoint trả `{ items: [{tenantId, displayName, limit, used, remaining, usedPct, warn, exhausted, updatedAtUtc}] }`. `displayName` resolve qua `TkSessionRepository.GetTenantNamesAsync` (giống pattern `AiUsagePage`). `tenantId == ""` (key rỗng nếu có) → bỏ qua khi gọi GetTenantNamesAsync (sẽ throw nếu empty string trong IN clause); fallback `displayName = tenantId`.

- [ ] **Step 1: Đọc `Endpoints/AdminUiEndpoints.cs` để hiểu cấu trúc group hiện tại**

Run mental check: file đang có `group.MapGet("/ai-usage", ...)` — copy pattern. Group đã apply `.RequireAdminSession()` ở level group nên endpoint mới tự kế thừa.

- [ ] **Step 2: Thêm endpoint `GET /quota` vào group**

Trong file `Endpoints/AdminUiEndpoints.cs`, sau khối `MapGet("/ai-usage", ...)`, thêm:

```csharp
group.MapGet("/quota", async (
    TenantQuotaStore quota,
    TkSessionRepository sessions,
    CancellationToken ct) =>
{
    var snapshots = quota.ListAll(); // đã OrderByDescending(Used)
    var tenantIds = snapshots
        .Select(s => s.Tenant)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct()
        .ToList();

    Dictionary<string, string> names;
    try { names = await sessions.GetTenantNamesAsync(tenantIds, ct); }
    catch { names = new Dictionary<string, string>(); }

    var items = snapshots.Select(s => new
    {
        tenantId    = s.Tenant,
        displayName = names.TryGetValue(s.Tenant, out var n) ? n : s.Tenant,
        limit       = s.Limit,
        used        = s.Used,
        remaining   = s.Remaining,
        usedPct     = s.UsedPct,
        warn        = s.Warn,
        exhausted   = s.Exhausted,
        updatedAtUtc = s.UpdatedAt
    }).ToList();

    return Results.Json(new { items });
});
```

- [ ] **Step 3: Build + smoke test endpoint**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded, 0 errors.

Manual smoke: server đang chạy, mở DevTools console ở `/admin-trav-ai/` (đã login):

```js
fetch('/api/v1/admin/ui/quota', { headers: { 'X-Admin-Session': JSON.parse(localStorage.getItem('tkai_admin_session')).token } }).then(r => r.json()).then(console.log)
```

Expected: `{items: [{tenantId, displayName, limit: 1000, used: <int>, ...}]}` cho ≥1 tenant. Không 401, không 500.

- [ ] **Step 4: Commit**

```bash
git add Endpoints/AdminUiEndpoints.cs
git commit -m "feat(admin): GET /admin/ui/quota — list tenants quota + display name"
```

---

### Task 2: Backend — `POST /api/v1/admin/ui/quota/{tenant}/topup`

**Files:**
- Modify: `Endpoints/AdminUiEndpoints.cs`

Endpoint nhận `{ amount: int }` (POST body), gọi `TenantQuotaStore.TopUp(tenant, amount)` (đã sync SQL + log), trả `QuotaSnapshot` đã cập nhật. Validate `amount` trong khoảng `[1, 100000]` (sanity — tránh nhập nhầm 1 tỷ).

- [ ] **Step 1: Thêm record + endpoint**

Trong file `Endpoints/AdminUiEndpoints.cs`, ngay sau endpoint `/quota` ở Task 1, thêm:

```csharp
group.MapPost("/quota/{tenant}/topup", (
    string tenant,
    AdminQuotaTopUpReq req,
    TenantQuotaStore quota) =>
{
    if (string.IsNullOrWhiteSpace(tenant))
        return Results.BadRequest(new { error = "tenant trống" });
    if (req.Amount < 1 || req.Amount > 100_000)
        return Results.BadRequest(new { error = "amount phải trong [1, 100000]" });

    var snap = quota.TopUp(tenant.Trim(), req.Amount);
    return Results.Json(new
    {
        tenantId    = snap.Tenant,
        limit       = snap.Limit,
        used        = snap.Used,
        remaining   = snap.Remaining,
        usedPct     = snap.UsedPct,
        warn        = snap.Warn,
        exhausted   = snap.Exhausted,
        updatedAtUtc = snap.UpdatedAt
    });
});
```

Ở cuối file (bên ngoài class hoặc trong namespace cùng `Endpoints`), thêm record:

```csharp
public record AdminQuotaTopUpReq(int Amount);
```

(Nếu file đã có `namespace TourkitAiProxy.Endpoints;` thì đặt record cuối file là OK. Nếu thấy record `TopUpReq` ở `QuotaEndpoints.cs` thì KHÔNG reuse — đó là legacy gate khác, ta dùng record riêng để tránh nhầm contract.)

- [ ] **Step 2: Build + smoke test**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: 0 errors.

Restart server (`dotnet run`) rồi test ở DevTools (login admin trước):

```js
const t = JSON.parse(localStorage.getItem('tkai_admin_session')).token;
fetch('/api/v1/admin/ui/quota/demokh.tourkit.vn/topup', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-Admin-Session': t },
  body: JSON.stringify({ amount: 5 })
}).then(r => r.json()).then(console.log);
```

Expected: `{tenantId: "demokh.tourkit.vn", limit: <old + 5>, used: ..., ...}`. Server log: `Quota topup tenant demokh.tourkit.vn +5 → limit=<N> used=<M>`.

- [ ] **Step 3: Test validation**

```js
fetch('/api/v1/admin/ui/quota/x/topup', { method:'POST', headers:{'Content-Type':'application/json','X-Admin-Session':t}, body: JSON.stringify({amount: 0}) }).then(r => r.status)
```

Expected: 400.

- [ ] **Step 4: Commit**

```bash
git add Endpoints/AdminUiEndpoints.cs
git commit -m "feat(admin): POST /admin/ui/quota/{tenant}/topup — cộng quota qua admin session"
```

---

### Task 3: Frontend — `QuotaPage` component

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (thêm component, push nav entry)
- Modify: `wwwroot/admin.css` (thêm style `.quota-*`)

Component liệt kê tenant trong bảng, sort theo `usedPct` desc, mỗi dòng hiển thị pct bar màu (xanh < 70%, vàng 70-90%, đỏ ≥ 90%). Cột cuối nút **Top-up** mở `window.prompt("Cộng bao nhiêu lượt?", "100")` → gọi POST + reload list. Lỗi → `alert(err.message || "Top-up thất bại")`.

Reuse `adminFetch` helper đã có (xử lý header `X-Admin-Session` + parse 401 → logout).

- [ ] **Step 1: Thêm component `QuotaPage` vào `admin.jsx`**

Tìm khối comment `// ────── Pages` (hoặc khu vực `AiUsagePage` đã định nghĩa) trong `wwwroot/pages/admin.jsx`. Thêm component MỚI (đặt sau `AiUsagePage`, trước khối `ADMIN_NAV`):

```jsx
function QuotaPage() {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  async function load() {
    setLoading(true); setError("");
    try {
      const r = await adminFetch("/api/v1/admin/ui/quota");
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
    if (raw == null) return; // cancel
    const amount = parseInt(raw, 10);
    if (!Number.isInteger(amount) || amount < 1) {
      alert("Số lượt phải là số nguyên ≥ 1");
      return;
    }
    try {
      const r = await adminFetch(`/api/v1/admin/ui/quota/${encodeURIComponent(tenantId)}/topup`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ amount })
      });
      const data = await r.json();
      if (!r.ok) throw new Error(data.error || `HTTP ${r.status}`);
      await load(); // reload toàn bộ để đồng bộ pct bar
    } catch (e) {
      alert(e.message || "Top-up thất bại");
    }
  }

  if (loading && items.length === 0) return <div className="admin-loading">Đang tải…</div>;
  if (error) return <div className="admin-error">⚠️ {error}</div>;

  return (
    <div className="admin-page">
      <div className="admin-page-header">
        <h2>Quota AI · Tenant</h2>
        <button className="admin-btn" onClick={load} disabled={loading}>↻ Refresh</button>
      </div>
      <div className="quota-section">
        <table className="quota-table">
          <thead>
            <tr>
              <th>#</th><th>TENANT</th><th>USED / LIMIT</th><th>% USED</th><th>REMAINING</th><th></th>
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
                      className="admin-btn admin-btn-sm"
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
```

- [ ] **Step 2: Push entry vào `ADMIN_NAV` array**

Tìm khối `const ADMIN_NAV = [` trong `wwwroot/pages/admin.jsx`. Hiện có 1 entry cho `ai-usage`. Thêm entry MỚI ngay sau:

```js
const ADMIN_NAV = [
  { path: "ai-usage", label: "AI Usage", icon: "📊", component: AiUsagePage },
  { path: "quota",    label: "Quota",    icon: "💎", component: QuotaPage },
];
```

(Giữ đúng tên field hiện có: `path`/`label`/`icon`/`component`. Nếu shape khác, copy literal y nguyên row `ai-usage` rồi đổi 4 giá trị.)

- [ ] **Step 3: Thêm CSS `.quota-*` vào `wwwroot/admin.css`**

Append vào cuối file `wwwroot/admin.css`:

```css
/* ─── Quota page ─────────────────────────────────────────────────── */
.quota-section { background:#fff; border:1px solid #e5e7eb; border-radius:8px; overflow:hidden; }
.quota-table { width:100%; border-collapse:collapse; font-size:14px; }
.quota-table thead th {
  background:#f9fafb; color:#6b7280; font-weight:600; font-size:11px;
  letter-spacing:.04em; text-transform:uppercase; padding:10px 14px; text-align:left;
  border-bottom:1px solid #e5e7eb;
}
.quota-table tbody td { padding:14px; border-bottom:1px solid #f3f4f6; vertical-align:middle; }
.quota-table tbody tr:last-child td { border-bottom:none; }
.quota-table tbody tr:hover { background:#fafafa; }

.quota-rank { color:#9ca3af; font-variant-numeric:tabular-nums; width:40px; }
.quota-name { font-weight:600; color:#111827; }
.quota-tid  { font-family:ui-monospace,Menlo,monospace; font-size:12px; color:#6b7280; margin-top:2px; }
.quota-num  { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; }

.quota-pct-cell { display:flex; align-items:center; gap:8px; min-width:180px; }
.quota-pct-bar  { flex:1; height:8px; background:#f3f4f6; border-radius:4px; overflow:hidden; position:relative; }
.quota-pct-fill { height:100%; transition:width .3s ease; }
.quota-pct-green .quota-pct-fill { background:#10b981; }
.quota-pct-amber .quota-pct-fill { background:#f59e0b; }
.quota-pct-red   .quota-pct-fill { background:#ef4444; }
.quota-pct-num  { font-variant-numeric:tabular-nums; font-size:13px; color:#374151; min-width:38px; text-align:right; }

.quota-empty { text-align:center; padding:32px; color:#9ca3af; }
.admin-btn-sm { padding:4px 10px; font-size:12px; }
```

- [ ] **Step 4: Smoke test trong browser**

Server đã chạy. Reload http://localhost:5080/admin-trav-ai/ (đã login) → click vào nav item "Quota" (icon 💎). Expected:
- Bảng hiển thị danh sách tenant (sort by Used desc).
- Pct bar màu: xanh tenant < 70%, vàng 70-90%, đỏ ≥ 90%.
- Click "+ Top-up" trên 1 tenant → prompt hiện ra → nhập `5` → bảng reload, cột `Used/Limit` của tenant đó tăng 5.
- Refresh button reload toàn bộ list.

Nếu chart issue cũ (Chart.js race) phát hiện ở Quota → KHÔNG sửa trong task này, page Quota không dùng chart.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): trang Quota — list tenant + pct bar + top-up qua prompt()"
```

---

### Task 4: Docs — `CLAUDE.md` API table

**Files:**
- Modify: `CLAUDE.md` (2 row mới ở bảng API surface)

- [ ] **Step 1: Thêm 2 row vào bảng API trong `CLAUDE.md`**

Tìm dòng có `/api/v1/admin/ui/ai-usage` trong `CLAUDE.md`. Ngay sau dòng đó, thêm:

```markdown
| GET    | `/api/v1/admin/ui/quota`          | list quota mọi tenant `{items[{tenantId, displayName, limit, used, remaining, usedPct, warn, exhausted, updatedAtUtc}]}` (require X-Admin-Session) |
| POST   | `/api/v1/admin/ui/quota/{tenant}/topup` | cộng `{amount}` (1..100000) lượt cho tenant → snapshot mới (require X-Admin-Session) |
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(admin): 2 API row mới cho /admin/ui/quota + /topup"
```

---

## Self-Review

**1. Spec coverage:** Yêu cầu = "trang quản lý quota tenant với top-up". Task 1 = GET list, Task 2 = POST topup, Task 3 = UI + CSS, Task 4 = docs. ✅

**2. Placeholder scan:** Không có "TBD/TODO/similar to". Tất cả code block đều complete.

**3. Type consistency:**
- `QuotaSnapshot` field naming: backend trả `Tenant`/`Limit`/`Used`/`Remaining`/`UsedPct`/`Warn`/`Exhausted`/`UpdatedAt`. Endpoint mapping → `tenantId`/`limit`/`used`/`remaining`/`usedPct`/`warn`/`exhausted`/`updatedAtUtc`. Frontend đọc đúng các field này (`t.tenantId`, `t.usedPct`, etc.). ✅
- `adminFetch` helper: assumed có sẵn từ Task 6 plan trước (admin shell). Verify bằng cách check `wwwroot/pages/admin.jsx` đã có `window.adminFetch` hoặc local function `adminFetch` trước khi implement Task 3.
- `fmtNum`: helper đã có trong `admin.jsx` (dùng trong AiUsagePage table). Reuse được.
- `ADMIN_NAV` shape: verify ở Step 2 Task 3 — nếu khác, copy literal.
