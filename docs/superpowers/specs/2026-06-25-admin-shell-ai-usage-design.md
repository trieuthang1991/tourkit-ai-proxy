# Admin Shell + AI Usage cross-tenant — Design Spec

> **Phase 1** của hệ quản trị admin `/admin-trav-ai/`. Mục tiêu: tạo shell + 1 trang đầu tiên (AI Usage cross-tenant), đặt nền pattern để thêm trang admin mới (đơn nạp quota, yêu cầu tư vấn…) sau này dễ.

**Date:** 2026-06-25
**Branch dự kiến:** `feat/admin-shell-ai-usage`
**Status:** Draft — chờ user review

## 1. Goal

- Có 1 URL admin riêng `/admin-trav-ai/` (KHÔNG share `index.html` user-facing) với login user/pass + sidebar nav + content area.
- Trang đầu tiên `/admin-trav-ai/ai-usage`: thấy **cross-tenant AI usage** (tổng tiền, top tenants ngốn AI nhất, drill-down 1 tenant cụ thể, by model, by day chart).
- Pattern thêm trang admin mới = 3 dòng code (component + nav entry + endpoint).

## 2. Non-goals (Phase 1)

- KHÔNG làm trang đơn nạp quota (`QuotaOrders`) — Phase 2.
- KHÔNG làm trang yêu cầu tư vấn (`ConsultLead`) — Phase 2.
- KHÔNG làm dashboard widget / drag-drop — không cần.
- KHÔNG role-based access (chỉ 1 cấp "admin", mọi user trong `Admin:Users` đều full quyền).
- KHÔNG audit log "ai đã xem gì" — đơn giản trước.
- KHÔNG persist admin session sang restart (admin login lại — chấp nhận, hiếm khi restart).

## 3. Auth

### 3.1 Config

`appsettings.json` thêm:
```json
"Admin": {
  "Token": "<giữ nguyên cho /api/v1/admin/quota webhook>",
  "Users": [
    { "Username": "thang", "Password": "<plaintext>" },
    { "Username": "admin", "Password": "<plaintext>" }
  ]
}
```

`appsettings.example.json` đồng bộ template (password = `REPLACE_WITH_ADMIN_PASSWORD`).

### 3.2 Services mới (`Services/Admin/`)

- **`AdminUserStore`** — đọc `Admin:Users` từ `IConfiguration` lúc start, expose `bool Authenticate(string user, string pass)`. Plain string compare (KHÔNG hash — admin pool nhỏ, single-tenant infra; documented as such trong code comment).
- **`AdminSessionStore`** — in-mem `ConcurrentDictionary<string, AdminSession>` (key = `Guid.NewGuid().ToString("N")`). Record: `AdminSession(Username, CreatedAt, LastAccessAt)`. Idle timeout 12h (check ở `Get()`, expired → remove + return null). KHÔNG persist.
- **`RequireAdminSessionExtensions`** — extension trên `RouteHandlerBuilder`: `.RequireAdminSession()` → wrap endpoint kiểm header `X-Admin-Session`, resolve session, attach `Username` vào `HttpContext.Items["AdminUser"]`. Miss/expired → `Results.Json({error:"unauthorized"}, 401)`.

### 3.3 Endpoints (`Endpoints/AdminAuthEndpoints.cs`)

| Method | Path | Body / Header | Response |
|--------|------|---------------|----------|
| POST | `/api/v1/admin/auth/login` | `{username, password}` | `{token, username, expiresAt}` hoặc 401 |
| POST | `/api/v1/admin/auth/logout` | header `X-Admin-Session` | `{ok:true}` |
| GET | `/api/v1/admin/auth/me` | header `X-Admin-Session` | `{username}` hoặc 401 |

### 3.4 Compatibility

- `/api/v1/admin/quota/*` (webhook ops) GIỮ NGUYÊN `X-Admin-Token` cũ — đừng đụng để khỏi vỡ Tingee/scripts.
- Mọi endpoint mới `/api/v1/admin/ui/*` dùng `RequireAdminSession()`.

## 4. Frontend shell

### 4.1 Entry riêng

- File mới `wwwroot/admin-trav-ai.html` — load Babel standalone + `pages/admin.jsx` + `admin.css` + Chart.js CDN.
- SPA fallback `app.MapFallback("/admin-trav-ai/{**path}", ...)` (hoặc explicit `MapGet` + `Path.StartsWithSegments`) → serve `admin-trav-ai.html`.
- KHÔNG share `index.html` user-facing → tránh nav/styles/global state user-facing leak vào admin.

### 4.2 Cấu trúc `pages/admin.jsx`

```
AdminApp
  ├─ AdminAuthGate
  │   ├─ chưa login → <AdminLogin>
  │   └─ đã login → <AdminShell>
  └─ AdminShell
      ├─ AdminSidebar (logo, nav từ ADMIN_NAV, active highlight)
      ├─ AdminTopbar (username + nút Logout)
      └─ <route content>  // path-based, sub-router nội bộ
          └─ AiUsagePage (default + chỉ route Phase 1)
```

### 4.3 Sub-router

Đọc `location.pathname` rồi match prefix `/admin-trav-ai/`. Path còn lại lookup trong `ADMIN_NAV` để chọn component. Nav config:
```js
const ADMIN_NAV = [
  { path: 'ai-usage', label: 'AI Usage', icon: '📊', component: AiUsagePage },
];
```
Navigate bằng `history.pushState` + `popstate` listener (KHÔNG dùng HashRouter của user-facing app — admin có URL clean hơn).

### 4.4 LocalStorage

- `localStorage["tkai_admin_session"] = {token, username}` để khỏi login lại mỗi refresh.
- Khi backend trả 401 → clear localStorage + render `<AdminLogin>`.

### 4.5 CSS

`wwwroot/admin.css` — style riêng, namespace prefix `.admin-*` để khỏi conflict với `styles.css` (user-facing).

## 5. AI Usage cross-tenant

### 5.1 Backend

**Endpoint mới** (`Endpoints/AdminUiEndpoints.cs`):
```
GET /api/v1/admin/ui/ai-usage?days=30&tenantId=<optional>
  .RequireAdminSession()
```

**Response shape** (cost dùng VND vì đơn vị gốc lưu trong DB là `CostVnd BIGINT`):
```json
{
  "range": { "from": "2026-05-26", "to": "2026-06-25", "days": 30 },
  "totals": {
    "calls": 1234,
    "inTokens": 5000000,
    "outTokens": 2000000,
    "costVnd": 308000
  },
  "byModel": [
    { "model": "deepseek-v4-flash", "calls": 100, "inTokens": 10000, "outTokens": 5000, "costVnd": 30000 }
  ],
  "byTenant": [
    { "tenantId": "abc", "tenantName": "Công ty XYZ", "calls": 50, "inTokens": 5000, "outTokens": 2000, "costVnd": 141750, "lastCallAt": "2026-06-24T10:00:00Z", "sharePct": 45.9 }
  ],
  "byDay": [
    { "date": "2026-06-24", "calls": 100, "costVnd": 25000 }
  ]
}
```

**Data source:**
- `dbo.AiUsageHistory` (granular per-request) — schema thực tế: `Id BIGINT IDENTITY`, `Ts DATETIME2`, `Feature NVARCHAR(64)`, `SessionId NVARCHAR(32) NULL`, `Tenant NVARCHAR(128) NULL`, `Provider NVARCHAR(64)`, `Model NVARCHAR(128)`, `InTok INT`, `OutTok INT`, `LatencyMs BIGINT`, `CostVnd BIGINT`, `Cached BIT`, `Status NVARCHAR(32)`. Index `IX_AiUsageHistory_Tenant_Ts(Tenant, Ts DESC)` đã có sẵn → query per-tenant range rẻ. KHÔNG dùng `AiUsageCounters` (không có Tenant).
- Aggregate qua SQL — 4 query (totals, byModel, byTenant, byDay) trong 1 connection. KHÔNG cache (admin xem ad-hoc, dataset nhỏ ≤ 90 ngày).
- Filter:
  - Time range: `WHERE Ts >= @from AND Ts < @to`
  - `Status = 'ok'` (loại bỏ failed call để khỏi double-count nếu retry).
  - `Tenant` apply khi `tenantId` query param có giá trị.
  - `byTenant` rows có `Tenant IS NULL` → group thành nhóm `(system)` (call không có session — system task).
- `tenantName` resolve qua `TkSessionRepository`: thêm method mới `GetTenantNamesAsync(IEnumerable<string> tenantIds)` → SELECT TOP 1 `CompanyName, FullName` per tenant ORDER BY `LastUsedUtc DESC`. Trả `Dictionary<tenantId, name>`. Display = `CompanyName ?? FullName ?? tenantId`.

**Repository mới** `Services/Admin/AdminUsageRepository.cs` — chứa 4 method aggregate. KHÔNG đụng `UsageRepository.cs` cũ (đó là single-tenant snapshot).

### 5.2 Frontend `AiUsagePage`

Layout 4 section (single column, scroll dọc):

1. **Header filter** — segmented tabs `7 ngày | 30 ngày | 90 ngày` + dropdown "Tenant: Tất cả ▾" (populated từ `byTenant` result). Đổi → re-fetch.
2. **4 stat cards** (grid 4 col) — Calls / Input tokens / Output tokens / Cost VND (format `1.234.567 ₫`). Số to, label nhỏ.
3. **Top tenants table** — columns: `# | Tenant | Calls | Cost VND | Share % | Last call`. Sort theo cost DESC. Row clickable → set `tenantId` filter → page tự re-render filter (highlight row đang select). Khi đang filter 1 tenant → section này hiển thị nhãn "Đang xem: <tenant>" + nút "Xem tất cả".
4. **By model table** — columns: `Model | Calls | In tokens | Out tokens | Cost VND`. Sort theo cost DESC.
5. **Daily chart** — Chart.js line chart, x = ngày, y = `costVnd`. Tooltip hiển thị calls + cost.

### 5.3 UX states

- Loading → skeleton 4 card + 2 table.
- Error → banner đỏ + nút Retry.
- Empty → "Chưa có dữ liệu AI usage trong khoảng này."

## 6. Pattern thêm trang admin mới ("dễ tùy biến")

Document trong `CLAUDE.md` section mới **"Admin governance"**:

1. **Backend endpoint mới** trong `AdminUiEndpoints.cs`:
   ```csharp
   group.MapGet("/orders", async (...) => {...}).RequireAdminSession();
   ```
2. **Component mới** trong `admin.jsx`:
   ```jsx
   function OrdersPage() { /* ... */ }
   ```
3. **Push 1 entry vào `ADMIN_NAV`:**
   ```js
   { path: 'orders', label: 'Đơn nạp quota', icon: '💳', component: OrdersPage }
   ```

KHÔNG cần đụng routing, login gate, sidebar — tất cả tự pick up.

## 7. Files affected

### Tạo mới

| File | Vai trò |
|------|---------|
| `Services/Admin/AdminUserStore.cs` | Đọc `Admin:Users`, authenticate |
| `Services/Admin/AdminSessionStore.cs` | In-mem session, 12h idle |
| `Services/Admin/RequireAdminSessionExtensions.cs` | `.RequireAdminSession()` filter |
| `Services/Admin/AdminUsageRepository.cs` | 4 query aggregate cross-tenant |
| `Endpoints/AdminAuthEndpoints.cs` | login / logout / me |
| `Endpoints/AdminUiEndpoints.cs` | `/api/v1/admin/ui/ai-usage` (+ chỗ thêm endpoint mới sau) |
| `wwwroot/admin-trav-ai.html` | Entry HTML riêng admin |
| `wwwroot/pages/admin.jsx` | Toàn bộ admin shell + AiUsagePage |
| `wwwroot/admin.css` | Style admin (namespace `.admin-*`) |

### Sửa

| File | Sửa gì |
|------|--------|
| `appsettings.example.json` | Thêm `Admin:Users` template |
| `Program.cs` | DI 3 service Admin + Map 2 endpoint group + SPA fallback `/admin-trav-ai/{**path}` |
| `CLAUDE.md` | Thêm section "Admin governance" (pattern + API table rows) |
| `docs/database-schema.md` | Note `AiUsageHistory` giờ là source của admin cross-tenant view |

## 8. Testing

- **Manual** (đủ cho Phase 1):
  - Login OK với user trong config.
  - Login fail với user/pass sai → 401.
  - Open `/admin-trav-ai/ai-usage` không có session → redirect login.
  - Filter 7/30/90 ngày → số đổi.
  - Click row tenant → drill-down, "Xem tất cả" reset.
  - Logout → clear localStorage + về login.
- **KHÔNG** viết test project mới (lệch khỏi scope; codebase chỉ có `TourkitAiProxy.Tests` cho Mail).

## 9. Risk / open questions

- **Plain text password**: chấp nhận — admin pool nhỏ (≤5 user), self-host, `appsettings.json` đã gitignore. Document trong code comment.
- **In-mem session restart loss**: chấp nhận — admin login lại sau deploy, ≤ 5 user.
- **Cross-tenant query performance**: `AiUsageHistory` đã có sẵn index `IX_AiUsageHistory_Tenant_Ts(Tenant, Ts DESC)` + `IX_AiUsageHistory_Ts(Ts DESC)` → query 30/90 ngày trên ≤ vài chục K row chạy nhanh. Phase 1 không cần optimize thêm.
- **No CSRF protection**: admin gọi qua `X-Admin-Session` custom header, KHÔNG dùng cookie → CSRF không exploitable. OK.

## 10. Out of scope / Phase 2 roadmap

- Trang `/admin-trav-ai/orders` — quản lý `QuotaOrders` (list pending/paid, mark paid manual, refund).
- Trang `/admin-trav-ai/requests` — quản lý `ConsultLead` (xem leads, đổi status).
- Trang `/admin-trav-ai/tenants` — list tenants (từ `TkSessions`), top up quota từ UI thay vì curl.
- Audit log "ai làm gì khi nào".
- Role-based access (super-admin / view-only).

Tất cả follow pattern section 6 — không cần đụng shell.
