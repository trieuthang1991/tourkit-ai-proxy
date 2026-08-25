# Vận hành: quản trị, log, CodeGraph

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Admin governance (`/admin-trav-ai/`)

Hệ quản trị admin riêng biệt với user-facing app. Entry HTML `wwwroot/admin-trav-ai.html` (KHÔNG share `index.html`). Toàn bộ shell + page components nằm trong 1 file `wwwroot/pages/admin.jsx`.

- **Auth**: cấu hình `Admin:Users` JSON trong `appsettings.json` (plain text password — admin pool nhỏ, self-host, file gitignore). `AdminUserStore.Authenticate` string-compare. Session in-mem `AdminSessionStore` (token GUID, 12h idle, KHÔNG persist). Client gửi `X-Admin-Session` header. Endpoint require qua extension `.RequireAdminSession()`.
- **Compatibility**: `/api/v1/admin/quota/*` (webhook ops) GIỮ NGUYÊN `Admin:Token` cũ — KHÔNG đụng. Mọi endpoint admin UI mới dùng `/api/v1/admin/ui/*` với `RequireAdminSession()`.
- **Cross-tenant digest**: `TourkitAiProxy.Infrastructure/Admin/AdminDigestRepository.cs` — JOIN `dbo.DigestSubscriptions` với `dbo.UserWorkflows` (`Username=''` vì 2 tác vụ bản tin đều PerTenant) để biết đăng ký nào đang "chết lặng". Mask chỉ đọc khi `LastSentLocalDate` ĐÚNG là hôm nay — sang ngày mới mask chưa reset tới lượt gửi đầu, đọc nhầm sẽ báo "đã gửi" cho hôm nay.
- **Cross-tenant usage**: `TourkitAiProxy.Infrastructure/Admin/AdminUsageRepository.cs` aggregate trên `dbo.AiUsageHistory` (4 query: totals/byModel/byTenant/byDay). Filter `Status='ok'` để khỏi double-count retry. `Tenant IS NULL` group thành `(system)`. Tenant name resolve qua `TkSessionRepository.GetTenantNamesAsync` (SELECT TOP 1 per tenant ORDER BY LastUsedUtc DESC, fallback `tenantId`).

### Thêm trang admin mới — 3 dòng

1. **Backend** — endpoint mới trong `TourkitAiProxy.Endpoints/AdminUiEndpoints.cs`:
   ```csharp
   g.MapGet("/orders", async (...) => { ... });
   // Filter `.RequireAdminSession()` đã apply ở group level — KHÔNG cần lặp.
   ```
2. **Frontend** — component mới trong `wwwroot/pages/admin.jsx`:
   ```jsx
   function OrdersPage() { /* ... */ }
   ```
3. **Nav** — push 1 entry vào `ADMIN_NAV`:
   ```js
   { path: "orders", label: "Đơn nạp quota", icon: "💳", component: OrdersPage }
   ```

Sidebar, sub-router, auth gate tự pick up. KHÔNG cần đụng `Program.cs`, không cần đụng `admin.css` (trừ khi page mới có style riêng → namespace `.admin-orders-*`).

## Logging (log4net + middleware)

**Sink chính:** log4net qua bridge `Microsoft.Extensions.Logging.Log4Net.AspNetCore` — mọi `ILogger<T>` của app + ASP.NET Core routing đều chảy qua log4net. Wire ở `Program.cs` (web) và `TourkitAiProxy.Worker/Program.cs`.

**Config**: [`log4net.config`](../log4net.config) ở root (worker link vào bin qua csproj), `Watch=true` → hot reload khi sửa level/appender, không cần restart.

**3 appender**:
- `RollingFileAppender` → `logs/app-YYYY-MM-DD.log` (giữ 30 file/~1 tháng)
- `ErrorFileAppender` → `logs/error-YYYY-MM-DD.log` (chỉ ERROR/FATAL, giữ 90 file/~3 tháng — tách để audit nhanh)
- `ConsoleAppender` → stdout (dev + Docker)

**Layout kèm 2 property**: `[req=%property{RequestId}|tenant=%property{TenantId}]` — nghĩa là mọi log trong 1 request có cùng `RequestId` (12-char GUID), grep 1 lần ra full flow.

**3 middleware bọc pipeline** (thứ tự ngoài → trong, đăng ký sớm nhất trong `Program.cs`):
1. `CorrelationIdMiddleware` ([Services/Logging/CorrelationIdMiddleware.cs](../TourkitAiProxy.Services/Logging/CorrelationIdMiddleware.cs)) — reuse `X-Request-Id` header hoặc sinh mới, push vào `log4net.LogicalThreadContext`, echo response header
2. `RequestLoggingMiddleware` ([Services/Logging/RequestLoggingMiddleware.cs](../TourkitAiProxy.Services/Logging/RequestLoggingMiddleware.cs)) — log 1 line/request `{Method} {Path} → {Status} ({Ms}ms) tenant={T} ip={IP}`. 2xx/3xx=Info · 4xx=Warn · 5xx=Error. Skip static asset (`.js`/`.jsx`/`.css`/`.png`/`/dist/`/`/lib/`/`/pages/`) để tránh spam
3. `UseExceptionHandler()` với `GlobalExceptionHandler` (`IExceptionHandler`, [Services/Logging/GlobalExceptionHandler.cs](../TourkitAiProxy.Services/Logging/GlobalExceptionHandler.cs)) — bắt exception KHÔNG được endpoint handle → log ERROR có full stack + trả JSON `{error, detail, type, requestId}` 500

**DB logging** (song song với log4net — độc lập): `dbo.AppLogs` bật qua `Logging:Database:Enabled=true` cho cross-instance search bằng SQL. Đã có sẵn `DbLoggerProvider` + `DbLogWriter`. Web mặc định OFF (log ra file đủ dùng); worker khuyến nghị ON khi scale nhiều instance.

**Level tuning**: đổi `<level value="INFO"/>` trong log4net.config, hot reload trong 60s. Namespace-specific: uncomment block `<logger name="TourkitAiProxy.Services.Workflows">` để bật `DEBUG` cho workflow mà không đụng root.

**Nội dung log workflow** (áp dụng cho `CustomerAutoReviewWorkflow` + `DealAutoReviewWorkflow`):
- START — kèm option đầy đủ, tenantId
- Login OK/FAIL + duration
- Mỗi phase (Pass 1/Pass 2/Cooling) — fetch count + bulk pre-fetch count + duration
- Per-page (Customer) hoặc per-pass (Deal) — breakdown counter (reviewed/skipped/…)
- TIMEOUT (`OperationCanceledException`) → Warning (không fail run)
- QUOTA hit → Warning (không fail run, không auto-pause)
- FINISH — tổng duration + full counter breakdown

**Upstream call log** ([Services/TourKit/TourKitApiClient.cs](../TourkitAiProxy.Infrastructure/TourKit/TourKitApiClient.cs)):
- LOGIN OK/FAIL kèm tenantId + username + duration
- GET/POST duration + status + bytes trên success (Debug); Warning cho 401/non-2xx/network error

**Không log**: JWT (có trong session raw), password, email body, phone number đầy đủ.

## Code lookup (CodeGraph MCP)

Khi câu hỏi liên quan đến **cấu trúc code** (callers/callees, "X dùng ở đâu", flow nghiệp vụ, blast-radius trước khi đổi tên), **PHẢI dùng CodeGraph trước** `Grep`/`Glob`. CodeGraph chạy trên knowledge graph SQLite build sẵn (auto-sync khi sửa file) → nhanh hơn nhiều lần so với re-scan file, bắt đúng symbol thay vì khớp text mù, và trả luôn source code kèm số dòng.

**3 repo đều đã index bằng CodeGraph** — mỗi repo 1 `.codegraph/` RIÊNG (CodeGraph là per-project, KHÔNG gộp nhiều repo trong 1 query như GitNexus cũ):
- `tourkit-ai-proxy` — project này (proxy + `wwwroot/`).
- `toutkit-app` — TourKit.Api mobile (upstream CRM mà proxy gọi qua `/api/ai/*`). Hỏi bằng cách chạy `codegraph` trong thư mục đó, hoặc truyền `projectPath` cho `codegraph_explore`.
- `tourkit` — CMS Web KojiCRM (ASP.NET WebForms; nghiệp vụ gốc TourKit).

**Chọn lệnh:**
- `codegraph explore "<concept>"` — concept search ("deal scoring flow?", "mail classification owner?"). Trả symbols liên quan + source + call paths + blast-radius trong 1 lần. Lệnh dùng nhiều nhất.
- `codegraph node <Symbol>` — 360° view 1 symbol (source + callers/callees). Dùng sau khi `explore` thu hẹp.
- `codegraph impact <Symbol>` — blast-radius TRƯỚC khi rename/sửa method/field. Liệt kê mọi caller + dependent (symbol-level, kèm số dòng).
- `codegraph callers <Symbol>` / `codegraph callees <Symbol>` — đi call graph 2 chiều.
- MCP: `mcp__codegraph__codegraph_explore` (tương đương `codegraph explore`; dùng `projectPath` để hỏi repo khác).

**KHI vẫn dùng Grep/Glob:** tìm chuỗi text trong comment / config / JSON / Markdown (graph chỉ index code symbol); list file theo glob.

**Re-index:** CodeGraph có watcher tự đồng bộ, NHƯNG ⚠️ **daemon của nó tự tắt sau 5 phút không có
truy vấn nào** (`inactivity backstop`, xem `.codegraph/daemon.log`). Nghĩa là một đợt sửa code dài mà
không tra cứu gì thì watcher đã chết từ lâu và index lạc hậu **trong im lặng** — `codegraph node X` vẫn
trả kết quả trông bình thường, chỉ thiếu đúng phần vừa sửa. Đã dính thật 14/08: lệch 21 file, chỉ phát
hiện khi tra một symbol mới thêm.

Vì thế repo này cài 3 git hook (`.git/hooks/post-commit|post-merge|post-checkout`) chạy `codegraph sync -q`.
Hook nằm trong `.git/` nên KHÔNG theo repo — máy mới phải tự cài lại (3 file, mỗi file gọi
`codegraph sync -q` rồi `exit 0`). Ép tay khi cần: `codegraph sync` (incremental) / `codegraph index`
(full). Cross-repo question (proxy ↔ TourKit.Api) → chạy `codegraph` trong `toutkit-app/` cho signature
upstream.
