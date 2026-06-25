# Admin Shell + AI Usage cross-tenant — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tạo admin governance shell `/admin-trav-ai/` + trang đầu tiên cross-tenant AI usage, với pattern thêm trang admin mới = 3 dòng code.

**Architecture:** Auth qua `Admin:Users` JSON config + in-mem `AdminSessionStore` (12h idle, `X-Admin-Session` header). Backend: 3 service mới (`AdminUserStore`/`AdminSessionStore`/`RequireAdminSession` extension) + 2 endpoint file (`AdminAuthEndpoints`/`AdminUiEndpoints`) + 1 repo (`AdminUsageRepository` đọc `dbo.AiUsageHistory`). Frontend: entry HTML riêng `/admin-trav-ai.html` (KHÔNG share `index.html` user-facing), 1 page Babel `pages/admin.jsx` chứa toàn shell + pages, sub-router pushState đọc `location.pathname`.

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper, SQL Server (`dbo.AiUsageHistory`/`dbo.TkSessions`), React 18 via Babel standalone (no build), Chart.js (lazy via existing `lib/chart-loader.js`).

**Spec:** `docs/superpowers/specs/2026-06-25-admin-shell-ai-usage-design.md`

**Manual testing only.** Repo chưa có test runner cho backend nhỏ — spec section 8 chấp nhận manual + smoke test. Mỗi task end-of-step có lệnh kiểm chứng (curl / browser).

**Frequent commits:** mỗi task kết thúc bằng 1 commit.

---

## Files map (tạo & sửa)

**Tạo mới (9 file):**

| File | Trách nhiệm |
|------|-------------|
| `Services/Admin/AdminUserStore.cs` | Đọc `Admin:Users` từ `IConfiguration`, `Authenticate(user, pass)` |
| `Services/Admin/AdminSessionStore.cs` | `ConcurrentDictionary<token, AdminSession>`, 12h idle, in-mem |
| `Services/Admin/RequireAdminSessionExtensions.cs` | `.RequireAdminSession()` route filter |
| `Services/Admin/AdminUsageRepository.cs` | 4 query aggregate trên `dbo.AiUsageHistory` |
| `Endpoints/AdminAuthEndpoints.cs` | `/api/v1/admin/auth/{login,logout,me}` |
| `Endpoints/AdminUiEndpoints.cs` | `/api/v1/admin/ui/ai-usage` (+ nơi thêm endpoint admin mới sau) |
| `wwwroot/admin-trav-ai.html` | Entry HTML riêng admin (KHÔNG share `index.html`) |
| `wwwroot/pages/admin.jsx` | Toàn bộ admin shell + AdminLogin + AiUsagePage |
| `wwwroot/admin.css` | CSS namespace `.admin-*` (không leak `styles.css`) |

**Sửa (4 file):**

| File | Sửa gì |
|------|--------|
| `appsettings.example.json` | Thêm template `Admin:Users` |
| `Program.cs` | DI 3 service Admin + 1 repo + Map 2 endpoint group + 2 MapGet cho admin HTML |
| `Services/TourKit/TkSessionRepository.cs` | Thêm method `GetTenantNamesAsync(tenantIds)` |
| `CLAUDE.md` | Section mới "Admin governance" + API table rows |
| `docs/database-schema.md` | Note `AiUsageHistory` là source cho admin cross-tenant |

---

## Task 1: Config template + AdminUserStore

**Files:**
- Modify: `appsettings.example.json`
- Create: `Services/Admin/AdminUserStore.cs`
- Modify: `Program.cs` (DI)

- [ ] **Step 1: Thêm Admin:Users template vào `appsettings.example.json`**

Mở `appsettings.example.json`, tìm key `"Admin"` (đã có `Token`), thêm `Users`:

```json
"Admin": {
  "Token": "REPLACE_WITH_ADMIN_TOKEN_FOR_WEBHOOK",
  "Users": [
    { "Username": "admin", "Password": "REPLACE_WITH_ADMIN_PASSWORD" }
  ]
},
```

Nếu chưa có `Admin` block → thêm nguyên cụm vào root.

- [ ] **Step 2: Tạo `Services/Admin/AdminUserStore.cs`**

```csharp
using Microsoft.Extensions.Configuration;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Đọc Admin:Users từ appsettings.json (plain text password — admin pool ≤5 user, self-host,
/// appsettings.json đã gitignore). Authenticate bằng string compare ordinal.
/// </summary>
public class AdminUserStore
{
    private readonly List<AdminUser> _users;

    public AdminUserStore(IConfiguration cfg)
    {
        _users = cfg.GetSection("Admin:Users").Get<List<AdminUser>>() ?? new();
    }

    public bool Authenticate(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        foreach (var u in _users)
        {
            if (string.Equals(u.Username, username, StringComparison.Ordinal) &&
                string.Equals(u.Password, password, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public sealed class AdminUser
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
```

- [ ] **Step 3: Đăng ký DI ở `Program.cs`**

Tìm dòng có `builder.Services.AddSingleton<ProviderKeyStore>();` (≈line 117), thêm phía trên hoặc sau:

```csharp
// Admin governance — auth qua Admin:Users (JSON config) + in-mem session.
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUserStore>();
```

- [ ] **Step 4: Build kiểm cú pháp**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add appsettings.example.json Services/Admin/AdminUserStore.cs Program.cs
git commit -m "feat(admin): config template Admin:Users + AdminUserStore plain-compare"
```

---

## Task 2: AdminSessionStore + Auth endpoints

**Files:**
- Create: `Services/Admin/AdminSessionStore.cs`
- Create: `Endpoints/AdminAuthEndpoints.cs`
- Modify: `Program.cs` (DI + MapAdminAuthEndpoints)

- [ ] **Step 1: Tạo `Services/Admin/AdminSessionStore.cs`**

```csharp
using System.Collections.Concurrent;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// In-mem session store cho admin. Key = random GUID. Idle timeout 12h (check ở Get).
/// KHÔNG persist — admin login lại sau restart (admin pool nhỏ, restart hiếm).
/// </summary>
public class AdminSessionStore
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new();

    public AdminSession Create(string username)
    {
        var token = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var s = new AdminSession(token, username, now, now);
        _sessions[token] = s;
        return s;
    }

    /// Lookup + touch LastAccessAt. Expired → remove + null.
    public AdminSession? Get(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var s)) return null;
        if (DateTime.UtcNow - s.LastAccessAt > IdleTimeout)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        var touched = s with { LastAccessAt = DateTime.UtcNow };
        _sessions[token] = touched;
        return touched;
    }

    public bool Remove(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return _sessions.TryRemove(token, out _);
    }

    public DateTime ExpiresAt(AdminSession s) => s.LastAccessAt + IdleTimeout;
}

public sealed record AdminSession(
    string Token,
    string Username,
    DateTime CreatedAt,
    DateTime LastAccessAt);
```

- [ ] **Step 2: Tạo `Endpoints/AdminAuthEndpoints.cs`**

```csharp
using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin governance — login user/pass → in-mem session token.
///   POST /api/v1/admin/auth/login   {username, password}            → {token, username, expiresAt}
///   POST /api/v1/admin/auth/logout  header X-Admin-Session           → {ok}
///   GET  /api/v1/admin/auth/me      header X-Admin-Session           → {username, expiresAt}
///
/// Phân biệt với /api/v1/admin/quota/* (giữ Admin:Token cũ cho webhook Tingee).
/// </summary>
public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/auth");

        g.MapPost("/login", (LoginReq req, AdminUserStore users, AdminSessionStore sessions) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new { error = "Thiếu username/password" }, statusCode: 400);
            if (!users.Authenticate(req.Username.Trim(), req.Password))
                return Results.Json(new { error = "Sai username hoặc password" }, statusCode: 401);
            var s = sessions.Create(req.Username.Trim());
            return Results.Json(new { token = s.Token, username = s.Username, expiresAt = sessions.ExpiresAt(s) });
        });

        g.MapPost("/logout", (HttpContext ctx, AdminSessionStore sessions) =>
        {
            var token = ctx.Request.Headers["X-Admin-Session"].FirstOrDefault();
            sessions.Remove(token);
            return Results.Json(new { ok = true });
        });

        g.MapGet("/me", (HttpContext ctx, AdminSessionStore sessions) =>
        {
            var token = ctx.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            return Results.Json(new { username = s.Username, expiresAt = sessions.ExpiresAt(s) });
        });

        return routes;
    }

    public record LoginReq(string Username, string Password);
}
```

- [ ] **Step 3: Đăng ký DI + Map endpoint trong `Program.cs`**

Sau dòng `builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUserStore>();` (Task 1 Step 3):

```csharp
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminSessionStore>();
```

Trong khối `// ─── Routes ───` (cuối `Program.cs`, sau `app.MapSystemEndpoints();`), thêm:

```csharp
app.MapAdminAuthEndpoints();    // /api/v1/admin/auth/{login,logout,me}
```

- [ ] **Step 4: Build**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Smoke test thủ công**

Tạm thêm vào `appsettings.json` (KHÔNG commit):
```json
"Admin": { "Users": [ { "Username": "test", "Password": "abc123" } ] }
```

Chạy `dotnet run --project TourkitAiProxy.csproj` (background), sau đó:
```bash
# Login OK
curl -s -X POST http://localhost:5080/api/v1/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"abc123"}'
# Expected: {"token":"...","username":"test","expiresAt":"..."}

# Login fail
curl -s -X POST http://localhost:5080/api/v1/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"WRONG"}' -w "\nHTTP:%{http_code}\n"
# Expected: {"error":"Sai username hoặc password"} HTTP:401

# Me OK (thay TOKEN từ login trên)
curl -s http://localhost:5080/api/v1/admin/auth/me -H "X-Admin-Session: TOKEN"
# Expected: {"username":"test","expiresAt":"..."}

# Me fail
curl -s http://localhost:5080/api/v1/admin/auth/me -w "\nHTTP:%{http_code}\n"
# Expected: {"error":"unauthorized"} HTTP:401
```

Stop server. Xoá block `Admin:Users` test khỏi `appsettings.json`.

- [ ] **Step 6: Commit**

```bash
git add Services/Admin/AdminSessionStore.cs Endpoints/AdminAuthEndpoints.cs Program.cs
git commit -m "feat(admin): session store in-mem + endpoint /api/v1/admin/auth/{login,logout,me}"
```

---

## Task 3: RequireAdminSession filter + AdminUiEndpoints scaffold

**Files:**
- Create: `Services/Admin/RequireAdminSessionExtensions.cs`
- Create: `Endpoints/AdminUiEndpoints.cs`
- Modify: `Program.cs` (MapAdminUiEndpoints)

- [ ] **Step 1: Tạo `Services/Admin/RequireAdminSessionExtensions.cs`**

```csharp
using Microsoft.AspNetCore.Http;
using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Extension cho route builder: .RequireAdminSession() → bọc endpoint kiểm header
/// X-Admin-Session, resolve AdminSession qua AdminSessionStore. Miss/expired → 401.
/// Khi pass: attach Username vào HttpContext.Items["AdminUser"] cho handler đọc.
/// </summary>
public static class RequireAdminSessionExtensions
{
    public const string HttpItemKey = "AdminUser";

    public static RouteHandlerBuilder RequireAdminSession(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
            var token = ctx.HttpContext.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            ctx.HttpContext.Items[HttpItemKey] = s.Username;
            return await next(ctx);
        });
    }

    public static RouteGroupBuilder RequireAdminSession(this RouteGroupBuilder builder)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
            var token = ctx.HttpContext.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            ctx.HttpContext.Items[HttpItemKey] = s.Username;
            return await next(ctx);
        });
    }
}
```

- [ ] **Step 2: Tạo `Endpoints/AdminUiEndpoints.cs` (scaffold; query thật ở Task 4)**

```csharp
using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin UI endpoints — backing cho /admin-trav-ai/* pages. Tất cả require X-Admin-Session.
///
/// Thêm trang admin mới = thêm route ở đây + 1 component trong wwwroot/pages/admin.jsx
/// + 1 entry vào ADMIN_NAV (xem "Admin governance" trong CLAUDE.md).
///
///   GET /api/v1/admin/ui/ai-usage?days=30&tenantId=  — Task 4
/// </summary>
public static class AdminUiEndpoints
{
    public static IEndpointRouteBuilder MapAdminUiEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/ui").RequireAdminSession();

        // Placeholder ping — verify filter chạy. Sẽ xoá khi /ai-usage hoàn thiện.
        g.MapGet("/ping", (HttpContext ctx) =>
        {
            var user = ctx.Items[RequireAdminSessionExtensions.HttpItemKey] as string ?? "?";
            return Results.Json(new { ok = true, user });
        });

        return routes;
    }
}
```

- [ ] **Step 3: Wire `MapAdminUiEndpoints` trong `Program.cs`**

Ngay sau `app.MapAdminAuthEndpoints();` (Task 2 Step 3):

```csharp
app.MapAdminUiEndpoints();      // /api/v1/admin/ui/* (require X-Admin-Session)
```

- [ ] **Step 4: Build**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Smoke test filter**

Restart server. Dùng lại setup test ở Task 2 Step 5.

```bash
# Không có session → 401
curl -s http://localhost:5080/api/v1/admin/ui/ping -w "\nHTTP:%{http_code}\n"
# Expected: {"error":"unauthorized"} HTTP:401

# Có session → 200
curl -s http://localhost:5080/api/v1/admin/ui/ping -H "X-Admin-Session: TOKEN"
# Expected: {"ok":true,"user":"test"}
```

Stop server.

- [ ] **Step 6: Commit**

```bash
git add Services/Admin/RequireAdminSessionExtensions.cs Endpoints/AdminUiEndpoints.cs Program.cs
git commit -m "feat(admin): RequireAdminSession filter + AdminUi scaffold /admin/ui/ping"
```

---

## Task 4: AdminUsageRepository (cross-tenant aggregate)

**Files:**
- Create: `Services/Admin/AdminUsageRepository.cs`
- Modify: `Services/TourKit/TkSessionRepository.cs` (thêm `GetTenantNamesAsync`)
- Modify: `Endpoints/AdminUiEndpoints.cs` (replace `/ping` bằng `/ai-usage`)
- Modify: `Program.cs` (DI)

- [ ] **Step 1: Thêm `GetTenantNamesAsync` vào `Services/TourKit/TkSessionRepository.cs`**

Đặt phía sau method `GetByUserAsync` (≈line 106), thêm:

```csharp
    /// <summary>
    /// Resolve display name cho 1 batch tenantId. Trả Dictionary<tenantId, displayName>.
    /// SELECT TOP 1 CompanyName/FullName per tenant ORDER BY LastUsedUtc DESC.
    /// Display = CompanyName ?? FullName ?? tenantId (caller tự fallback).
    /// Tenant nào không có session → KHÔNG có entry trong dict.
    /// </summary>
    public async Task<Dictionary<string, string>> GetTenantNamesAsync(
        IEnumerable<string> tenantIds, CancellationToken ct = default)
    {
        var ids = tenantIds.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (ids.Count == 0) return new();
        try
        {
            await using var c = await _db.OpenAsync(ct);
            // Subquery ROW_NUMBER để lấy row mới nhất per TenantId (idiom SQL Server)
            var rows = await c.QueryAsync<(string TenantId, string? CompanyName, string? FullName)>(@"
SELECT TenantId, CompanyName, FullName FROM (
    SELECT TenantId, CompanyName, FullName,
           ROW_NUMBER() OVER (PARTITION BY TenantId ORDER BY LastUsedUtc DESC) AS rn
    FROM dbo.TkSessions
    WHERE TenantId IN @ids
) t WHERE rn = 1;",
                new { ids });
            var dict = new Dictionary<string, string>();
            foreach (var r in rows)
            {
                var name = !string.IsNullOrWhiteSpace(r.CompanyName) ? r.CompanyName!
                         : !string.IsNullOrWhiteSpace(r.FullName)    ? r.FullName!
                         : r.TenantId;
                dict[r.TenantId] = name;
            }
            return dict;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] GetTenantNames lỗi");
            return new();
        }
    }
```

- [ ] **Step 2: Tạo `Services/Admin/AdminUsageRepository.cs`**

```csharp
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Aggregate cross-tenant trên dbo.AiUsageHistory (granular per-request).
/// Schema: Ts DATETIME2, Feature, SessionId, Tenant NVARCHAR(128) NULL, Provider, Model,
///         InTok INT, OutTok INT, LatencyMs BIGINT, CostVnd BIGINT, Cached BIT, Status NVARCHAR(32).
/// Index sẵn: IX_AiUsageHistory_Ts(DESC) + IX_AiUsageHistory_Tenant_Ts.
///
/// 4 query: totals / byModel / byTenant / byDay. Tất cả filter Status='ok' + Ts trong range.
/// Tenant IS NULL → group thành '(system)' (call không có session — system task).
/// </summary>
public class AdminUsageRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<AdminUsageRepository> _log;
    private const string SystemTenantKey = "(system)";

    public AdminUsageRepository(TourkitAiDb db, ILogger<AdminUsageRepository> log)
    {
        _db = db; _log = log;
    }

    public sealed record TotalsRow(long Calls, long InTokens, long OutTokens, long CostVnd);
    public sealed record ModelRow(string Model, long Calls, long InTokens, long OutTokens, long CostVnd);
    public sealed record TenantRow(string TenantId, long Calls, long InTokens, long OutTokens, long CostVnd, DateTime? LastCallAt);
    public sealed record DayRow(DateTime Date, long Calls, long CostVnd);

    public async Task<TotalsRow> GetTotalsAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var row = await c.QueryFirstOrDefaultAsync<TotalsRow>($@"
SELECT COUNT_BIG(*) AS Calls,
       ISNULL(SUM(CAST(InTok  AS BIGINT)), 0) AS InTokens,
       ISNULL(SUM(CAST(OutTok AS BIGINT)), 0) AS OutTokens,
       ISNULL(SUM(CostVnd), 0)                AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where};", parms);
            return row ?? new TotalsRow(0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetTotals lỗi");
            return new TotalsRow(0, 0, 0, 0);
        }
    }

    public async Task<List<ModelRow>> GetByModelAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<ModelRow>($@"
SELECT Model,
       COUNT_BIG(*) AS Calls,
       SUM(CAST(InTok  AS BIGINT)) AS InTokens,
       SUM(CAST(OutTok AS BIGINT)) AS OutTokens,
       SUM(CostVnd)                AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY Model
ORDER BY CostVnd DESC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByModel lỗi");
            return new();
        }
    }

    public async Task<List<TenantRow>> GetByTenantAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        // tenantId filter: nếu có → chỉ group 1 tenant (TenantRow đơn lẻ).
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<TenantRow>($@"
SELECT ISNULL(Tenant, '{SystemTenantKey}') AS TenantId,
       COUNT_BIG(*) AS Calls,
       SUM(CAST(InTok  AS BIGINT)) AS InTokens,
       SUM(CAST(OutTok AS BIGINT)) AS OutTokens,
       SUM(CostVnd)                AS CostVnd,
       MAX(Ts)                     AS LastCallAt
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY ISNULL(Tenant, '{SystemTenantKey}')
ORDER BY CostVnd DESC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByTenant lỗi");
            return new();
        }
    }

    public async Task<List<DayRow>> GetByDayAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<DayRow>($@"
SELECT CAST(Ts AS DATE) AS [Date],
       COUNT_BIG(*)     AS Calls,
       SUM(CostVnd)     AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY CAST(Ts AS DATE)
ORDER BY [Date] ASC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByDay lỗi");
            return new();
        }
    }

    private static (string Where, object Parms) BuildFilter(DateTime fromUtc, DateTime toUtc, string? tenantId)
    {
        // Status='ok' để khỏi double-count nếu provider retry (failed call cũng append vào history).
        var where = "Ts >= @from AND Ts < @to AND Status = 'ok'";
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            // tenantId='(system)' → match Tenant IS NULL
            if (string.Equals(tenantId, SystemTenantKey, StringComparison.Ordinal))
                where += " AND Tenant IS NULL";
            else
                where += " AND Tenant = @tenant";
        }
        return (where, new { from = fromUtc, to = toUtc, tenant = tenantId });
    }
}
```

- [ ] **Step 3: Đăng ký DI `AdminUsageRepository` trong `Program.cs`**

Sau dòng `builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminSessionStore>();` (Task 2 Step 3):

```csharp
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUsageRepository>();
```

- [ ] **Step 4: Replace `/ping` bằng `/ai-usage` trong `Endpoints/AdminUiEndpoints.cs`**

Thay TOÀN BỘ method `MapAdminUiEndpoints` bằng:

```csharp
    public static IEndpointRouteBuilder MapAdminUiEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/ui").RequireAdminSession();

        // GET /api/v1/admin/ui/ai-usage?days=30&tenantId=
        g.MapGet("/ai-usage", async (
            int? days, string? tenantId,
            TourkitAiProxy.Services.Admin.AdminUsageRepository usage,
            TourkitAiProxy.Services.TourKit.TkSessionRepository tkRepo,
            CancellationToken ct) =>
        {
            var d = Math.Clamp(days ?? 30, 1, 365);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddDays(-d);

            var totalsTask = usage.GetTotalsAsync(fromUtc, toUtc, tenantId, ct);
            var byModelTask = usage.GetByModelAsync(fromUtc, toUtc, tenantId, ct);
            var byTenantTask = usage.GetByTenantAsync(fromUtc, toUtc, tenantId, ct);
            var byDayTask = usage.GetByDayAsync(fromUtc, toUtc, tenantId, ct);
            await Task.WhenAll(totalsTask, byModelTask, byTenantTask, byDayTask);

            var totals = await totalsTask;
            var byModel = await byModelTask;
            var byTenant = await byTenantTask;
            var byDay = await byDayTask;

            // Resolve tenantName cho từng row (skip "(system)" sentinel).
            var realTenantIds = byTenant.Where(t => t.TenantId != "(system)").Select(t => t.TenantId);
            var names = await tkRepo.GetTenantNamesAsync(realTenantIds, ct);

            long totalCost = totals.CostVnd;
            return Results.Json(new
            {
                range = new
                {
                    from = fromUtc,
                    to = toUtc,
                    days = d
                },
                totals = new
                {
                    calls = totals.Calls,
                    inTokens = totals.InTokens,
                    outTokens = totals.OutTokens,
                    costVnd = totals.CostVnd
                },
                byModel = byModel.Select(m => new
                {
                    model = m.Model,
                    calls = m.Calls,
                    inTokens = m.InTokens,
                    outTokens = m.OutTokens,
                    costVnd = m.CostVnd
                }).ToList(),
                byTenant = byTenant.Select(t => new
                {
                    tenantId = t.TenantId,
                    tenantName = t.TenantId == "(system)" ? "(System tasks)"
                              : names.TryGetValue(t.TenantId, out var n) ? n
                              : t.TenantId,
                    calls = t.Calls,
                    inTokens = t.InTokens,
                    outTokens = t.OutTokens,
                    costVnd = t.CostVnd,
                    lastCallAt = t.LastCallAt,
                    sharePct = totalCost > 0 ? Math.Round((double)t.CostVnd * 100.0 / totalCost, 2) : 0.0
                }).ToList(),
                byDay = byDay.Select(d => new
                {
                    date = d.Date.ToString("yyyy-MM-dd"),
                    calls = d.Calls,
                    costVnd = d.CostVnd
                }).ToList()
            });
        });

        return routes;
    }
```

- [ ] **Step 5: Build**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Smoke test endpoint trả JSON shape**

Restart server (giữ `Admin:Users` test trong appsettings.json local).

```bash
# Login lấy token
TOKEN=$(curl -s -X POST http://localhost:5080/api/v1/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"abc123"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")

# Get usage 30 ngày
curl -s "http://localhost:5080/api/v1/admin/ui/ai-usage?days=30" -H "X-Admin-Session: $TOKEN" | python -m json.tool | head -50
```

Expected: JSON với keys `range`, `totals`, `byModel`, `byTenant`, `byDay`. `totals.costVnd` là số BIGINT. `byTenant[].sharePct` ≤ 100.

Stop server.

- [ ] **Step 7: Commit**

```bash
git add Services/Admin/AdminUsageRepository.cs Services/TourKit/TkSessionRepository.cs Endpoints/AdminUiEndpoints.cs Program.cs
git commit -m "feat(admin): /admin/ui/ai-usage cross-tenant aggregate (totals/byModel/byTenant/byDay)"
```

---

## Task 5: HTML entry + SPA routing + admin.jsx skeleton

**Files:**
- Create: `wwwroot/admin-trav-ai.html`
- Create: `wwwroot/admin.css` (empty + placeholder rule)
- Create: `wwwroot/pages/admin.jsx` (skeleton: chỉ render "Admin shell loading…")
- Modify: `Program.cs` (MapGet routes cho admin HTML — TRƯỚC `app.MapFallback`)

- [ ] **Step 1: Tạo `wwwroot/admin-trav-ai.html`**

```html
<!DOCTYPE html>
<html lang="vi">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<base href="/" />
<title>TRAV-AI · Admin</title>
<link rel="icon" type="image/svg+xml" href="/favicon.svg" />
<link rel="stylesheet" href="/admin.css" />
</head>
<body>
<div id="admin-root"></div>

<!-- React + Babel (cùng nguồn user-facing index.html) -->
<script src="/lib/vendor/react-18.3.1.min.js"></script>
<script src="/lib/vendor/react-dom-18.3.1.min.js"></script>
<script src="https://unpkg.com/@babel/standalone@7.29.0/babel.min.js" integrity="sha384-m08KidiNqLdpJqLq95G/LEi8Qvjl/xUYll3QILypMoQ65QorJ9Lvtp2RXYGBFj1y" crossorigin="anonymous"></script>

<!-- Chart.js lazy loader (đã có sẵn — AiUsagePage tự gọi window.ensureChart()) -->
<script src="/lib/chart-loader.js"></script>

<!-- Admin shell — toàn bộ shell + pages trong 1 file -->
<script type="text/babel" src="/pages/admin.jsx"></script>
</body>
</html>
```

- [ ] **Step 2: Tạo `wwwroot/admin.css` placeholder**

```css
/* Admin shell — namespace prefix .admin-* để KHÔNG leak styles.css user-facing. */
.admin-loading {
  font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
  display: grid; place-items: center;
  min-height: 100vh;
  color: #4b5563;
}
```

- [ ] **Step 3: Tạo `wwwroot/pages/admin.jsx` skeleton**

```jsx
// Admin shell — /admin-trav-ai/*
// Single-file React app: shell + login + sub-router + tất cả page components.
// KHÔNG share window.tourkit* với user-facing app — admin tự isolation.
(function () {
  const { useState, useEffect } = React;

  function AdminApp() {
    return (
      <div className="admin-loading">
        Admin shell loading… <br/>
        <small>{location.pathname}</small>
      </div>
    );
  }

  const root = ReactDOM.createRoot(document.getElementById("admin-root"));
  root.render(<AdminApp />);
})();
```

- [ ] **Step 4: Thêm MapGet routes vào `Program.cs` — TRƯỚC `app.Run()`**

Tìm khối `// ─── Routes ──────────` (≈line 370). Sau `app.MapWidgetEndpoints();` (line cuối cùng trong Routes), thêm:

```csharp
// Admin shell — entry HTML riêng /admin-trav-ai.html, KHÔNG share index.html user-facing.
// MapGet explicit thắng MapFallback (StaticFilesSetup) → /admin-trav-ai/{**path} serve đúng file admin.
app.MapGet("/admin-trav-ai", (HttpContext ctx) => ServeAdminHtml(ctx, app.Environment.ContentRootPath));
app.MapGet("/admin-trav-ai/{**path}", (HttpContext ctx) => ServeAdminHtml(ctx, app.Environment.ContentRootPath));

static IResult ServeAdminHtml(HttpContext ctx, string contentRoot)
{
    var path = Path.Combine(contentRoot, "wwwroot", "admin-trav-ai.html");
    if (!File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
    return Results.Content(File.ReadAllText(path), "text/html; charset=utf-8");
}
```

- [ ] **Step 5: Build**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Smoke test browser**

Restart `dotnet run`. Mở browser → `http://localhost:5080/admin-trav-ai/`. Kỳ vọng:
- Page hiển thị "Admin shell loading… /admin-trav-ai/"
- DevTools Console KHÔNG có lỗi React/Babel.
- Refresh → vẫn render (no 404).
- Sub-path: `http://localhost:5080/admin-trav-ai/login` → render "Admin shell loading… /admin-trav-ai/login" (cùng HTML, pathname khác).

Stop server.

- [ ] **Step 7: Commit**

```bash
git add wwwroot/admin-trav-ai.html wwwroot/admin.css wwwroot/pages/admin.jsx Program.cs
git commit -m "feat(admin): HTML entry + SPA fallback /admin-trav-ai/* + admin.jsx skeleton"
```

---

## Task 6: AdminLogin + AdminAuthGate + session persistence

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (thêm AdminLogin + AdminAuthGate + adminFetch helper)
- Modify: `wwwroot/admin.css` (thêm CSS login form)

- [ ] **Step 1: Thay TOÀN BỘ `wwwroot/pages/admin.jsx` bằng**

```jsx
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
```

- [ ] **Step 2: Thêm CSS login vào `wwwroot/admin.css`**

Append:

```css
/* Login */
.admin-login-wrap {
  min-height: 100vh;
  display: grid; place-items: center;
  background: radial-gradient(circle at 30% 20%, #FFF7ED 0%, #FFE4CF 45%, #FED7AA 100%);
  font-family: 'Be Vietnam Pro', system-ui, -apple-system, 'Segoe UI', sans-serif;
}
.admin-login-card {
  width: 360px; max-width: 92vw;
  background: #fff; border-radius: 16px;
  box-shadow: 0 20px 40px rgba(124, 45, 18, 0.18);
  padding: 28px 28px 24px;
  display: flex; flex-direction: column; gap: 10px;
}
.admin-login-card h1 { margin: 0; font-size: 22px; color: #9A3412; }
.admin-login-sub { margin: 0 0 12px; color: #6b7280; font-size: 13px; }
.admin-login-card label { font-size: 12px; font-weight: 600; color: #374151; margin-top: 4px; }
.admin-login-card input {
  border: 1px solid #d1d5db; border-radius: 8px; padding: 10px 12px;
  font-size: 14px; font: inherit;
}
.admin-login-card input:focus { outline: none; border-color: #F97316; box-shadow: 0 0 0 3px rgba(249,115,22,0.15); }
.admin-login-card button {
  margin-top: 12px; padding: 10px 16px;
  background: #F97316; color: #fff; border: none; border-radius: 8px;
  font-weight: 600; font-size: 14px; cursor: pointer;
}
.admin-login-card button:disabled { opacity: 0.5; cursor: not-allowed; }
.admin-login-error { color: #b91c1c; font-size: 13px; background: #fef2f2; padding: 8px 10px; border-radius: 6px; }
```

- [ ] **Step 3: Smoke test login flow**

`dotnet run` (giữ `Admin:Users` test). Mở `http://localhost:5080/admin-trav-ai/`. Kỳ vọng:
- Hiện form login (orange gradient).
- Username `test` + password sai → hiện "Sai username hoặc password".
- Password đúng → render "Logged in as test" + nút Logout.
- F5 → vẫn logged in (localStorage).
- Click Logout → quay lại login form.
- DevTools Application → Local Storage → `tkai_admin_session` xuất hiện khi login OK, mất khi logout.

Stop server.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): AdminLogin form + AuthGate + adminFetch wrapper + localStorage persist"
```

---

## Task 7: AdminShell — sidebar + topbar + sub-router

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (replace `AdminShellPlaceholder` bằng `AdminShell` thật)
- Modify: `wwwroot/admin.css` (thêm CSS shell)

- [ ] **Step 1: Trong `wwwroot/pages/admin.jsx`, XOÁ `AdminShellPlaceholder` và THÊM (cùng vị trí, sau `AdminAuthGate`)**

```jsx
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

  // ── Page stub (Task 8 implement thật) ─────────────────────────────────────
  function AiUsagePage() {
    return <div>AI Usage page — Task 8 sẽ implement.</div>;
  }
```

Replace dòng `return <AdminShellPlaceholder username={state.username} onLogout={...} />;` trong `AdminAuthGate` bằng:

```jsx
    return <AdminShell username={state.username} onLogout={() => { clearSession(); check(); }} />;
```

XOÁ function `AdminShellPlaceholder`.

- [ ] **Step 2: Thêm CSS shell vào `wwwroot/admin.css`**

Append:

```css
/* Shell */
.admin-shell {
  display: grid;
  grid-template-columns: 240px 1fr;
  min-height: 100vh;
  font-family: 'Be Vietnam Pro', system-ui, -apple-system, 'Segoe UI', sans-serif;
  background: #f9fafb;
}
.admin-sidebar {
  background: #1f2937; color: #f3f4f6;
  padding: 20px 14px;
  display: flex; flex-direction: column; gap: 18px;
}
.admin-brand {
  font-size: 18px; font-weight: 800; letter-spacing: 0.5px; padding: 0 8px;
  color: #fed7aa;
}
.admin-brand small { font-weight: 400; color: #9ca3af; font-size: 11px; letter-spacing: 1px; }
.admin-sidebar nav { display: flex; flex-direction: column; gap: 4px; }
.admin-nav-item {
  display: flex; align-items: center; gap: 10px;
  padding: 10px 12px; border-radius: 8px;
  color: #d1d5db; text-decoration: none; font-size: 14px;
  transition: background 0.15s;
}
.admin-nav-item:hover { background: rgba(255,255,255,0.06); color: #fff; }
.admin-nav-item.active { background: rgba(249,115,22,0.18); color: #fed7aa; }
.admin-nav-icon { font-size: 16px; width: 20px; text-align: center; }

.admin-main { display: flex; flex-direction: column; min-width: 0; }
.admin-topbar {
  background: #fff; border-bottom: 1px solid #e5e7eb;
  padding: 14px 24px;
  display: flex; align-items: center; justify-content: space-between;
}
.admin-topbar-title { font-size: 18px; font-weight: 700; color: #111827; }
.admin-topbar-right { display: flex; align-items: center; gap: 12px; }
.admin-topbar-user { font-size: 13px; color: #6b7280; }
.admin-topbar-logout {
  background: transparent; border: 1px solid #d1d5db; color: #374151;
  padding: 6px 12px; border-radius: 6px; font-size: 13px; cursor: pointer;
}
.admin-topbar-logout:hover { background: #f3f4f6; }

.admin-content { padding: 24px; flex: 1; min-width: 0; }
```

- [ ] **Step 3: Smoke test shell**

`dotnet run`. Login. Kỳ vọng:
- Sidebar trái, dark, item "AI Usage" highlighted.
- Topbar title "AI Usage", có username + nút "Đăng xuất".
- Content area hiện "AI Usage page — Task 8 sẽ implement."
- Click `/admin-trav-ai/ai-usage` từ URL bar → cùng view.
- Click nav item "AI Usage" → URL change `/admin-trav-ai/ai-usage` (pushState, không reload).
- Browser Back/Forward → URL + active highlight update đồng bộ.
- Logout → về login.

Stop server.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): AdminShell sidebar + topbar + pushState sub-router + ADMIN_NAV"
```

---

## Task 8: AiUsagePage — stat cards + time range filter

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (replace `AiUsagePage` stub bằng impl: fetch + stat cards + time range)
- Modify: `wwwroot/admin.css` (CSS cards + range tabs)

- [ ] **Step 1: Trong `wwwroot/pages/admin.jsx`, replace function `AiUsagePage` bằng**

```jsx
  // ── AI Usage page ─────────────────────────────────────────────────────────
  function fmtVnd(n) {
    if (n == null) return "—";
    return new Intl.NumberFormat("vi-VN").format(Math.round(n)) + " ₫";
  }
  function fmtNum(n) {
    if (n == null) return "0";
    return new Intl.NumberFormat("vi-VN").format(n);
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
            {/* Task 9: tenants table; Task 10: model table + chart */}
            <div className="ai-usage-placeholder">
              <p>📦 Task 9 sẽ thêm Top tenants table.</p>
              <p>📦 Task 10 sẽ thêm By model table + daily chart.</p>
            </div>
          </>
        )}
      </div>
    );
  }
```

- [ ] **Step 2: Thêm CSS vào `wwwroot/admin.css`**

Append:

```css
/* AI Usage page */
.ai-usage-page { display: flex; flex-direction: column; gap: 18px; }
.ai-usage-filters { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
.ai-usage-range { display: inline-flex; background: #fff; border: 1px solid #e5e7eb; border-radius: 8px; overflow: hidden; }
.ai-usage-tab {
  background: transparent; border: none; padding: 8px 16px;
  font-size: 13px; color: #4b5563; cursor: pointer; font-weight: 500;
}
.ai-usage-tab:hover { background: #f9fafb; }
.ai-usage-tab.active { background: #F97316; color: #fff; }
.ai-usage-tenant-pill {
  background: #fef3c7; color: #92400e; border-radius: 999px;
  padding: 6px 14px; font-size: 13px; display: inline-flex; align-items: center; gap: 8px;
}
.ai-usage-tenant-pill button {
  background: transparent; border: none; color: #92400e; cursor: pointer; font-weight: 700;
}

.ai-usage-loading, .ai-usage-error, .ai-usage-placeholder {
  background: #fff; border: 1px solid #e5e7eb; border-radius: 8px; padding: 16px;
  color: #6b7280; font-size: 14px;
}
.ai-usage-error { color: #b91c1c; background: #fef2f2; border-color: #fecaca; }
.ai-usage-error button { margin-left: 12px; background: #b91c1c; color: #fff; border: none; padding: 4px 10px; border-radius: 4px; cursor: pointer; }

.ai-usage-stats { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; }
.ai-usage-stat {
  background: #fff; border: 1px solid #e5e7eb; border-radius: 10px; padding: 16px 18px;
}
.ai-usage-stat-label { font-size: 12px; color: #6b7280; font-weight: 500; text-transform: uppercase; letter-spacing: 0.5px; }
.ai-usage-stat-value { font-size: 26px; font-weight: 700; color: #111827; margin-top: 4px; }

@media (max-width: 900px) {
  .ai-usage-stats { grid-template-columns: repeat(2, 1fr); }
}
```

- [ ] **Step 3: Smoke test**

`dotnet run`, login, mở `/admin-trav-ai/ai-usage`. Kỳ vọng:
- 4 stat card hiện số (có thể 0 nếu DB chưa có call).
- Click tab `7 ngày` / `30 ngày` / `90 ngày` → re-fetch (Network tab thấy request mới).
- Active tab highlight orange.
- Nếu DB lỗi → banner đỏ + nút "Thử lại".

Stop server.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): AiUsagePage stat cards (calls/in/out/cost VND) + range tabs 7/30/90"
```

---

## Task 9: Top tenants table + drill-down

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (thêm component `TenantsTable` + render trong `AiUsagePage`)
- Modify: `wwwroot/admin.css` (CSS table)

- [ ] **Step 1: Trong `wwwroot/pages/admin.jsx`, thêm component `TenantsTable` ngay TRƯỚC `AiUsagePage`**

```jsx
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
                  <td className="num">{t.sharePct.toFixed(1)}%</td>
                  <td>{fmtDate(t.lastCallAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    );
  }
```

- [ ] **Step 2: Trong `AiUsagePage`, replace block `<div className="ai-usage-placeholder">…📦 Task 9…📦 Task 10…</div>` bằng**

```jsx
            <TenantsTable
              rows={data.byTenant}
              activeTenant={tenantFilter}
              onPick={setTenantFilter} />
            {/* Task 10: By model table + daily chart */}
            <div className="ai-usage-placeholder">
              <p>📦 Task 10 sẽ thêm By model table + daily chart.</p>
            </div>
```

- [ ] **Step 3: Thêm CSS table vào `wwwroot/admin.css`**

Append:

```css
.ai-usage-section { background: #fff; border: 1px solid #e5e7eb; border-radius: 10px; padding: 16px 18px; }
.ai-usage-section-title { margin: 0 0 12px; font-size: 15px; font-weight: 700; color: #111827; }

.ai-usage-table-wrap { overflow-x: auto; }
.ai-usage-table {
  width: 100%; border-collapse: collapse; font-size: 13px;
}
.ai-usage-table th, .ai-usage-table td {
  text-align: left; padding: 10px 12px; border-bottom: 1px solid #f3f4f6;
}
.ai-usage-table th { font-weight: 600; color: #6b7280; font-size: 12px; text-transform: uppercase; letter-spacing: 0.3px; }
.ai-usage-table th.num, .ai-usage-table td.num { text-align: right; font-variant-numeric: tabular-nums; }
.ai-usage-table tr.clickable { cursor: pointer; }
.ai-usage-table tr.clickable:hover { background: #fffbeb; }
.ai-usage-table tr.active { background: #fef3c7; }
.ai-usage-table tr.active:hover { background: #fef3c7; }

.tenant-name { font-weight: 600; color: #111827; }
.tenant-id { font-size: 11px; color: #9ca3af; font-family: ui-monospace, SFMono-Regular, monospace; }
```

- [ ] **Step 4: Smoke test**

`dotnet run`, login, mở `/admin-trav-ai/ai-usage`. Kỳ vọng:
- Section "🏢 Top tenants" hiện table với # + tenant name + tenant ID nhỏ + calls + cost + % share + last call.
- Sort theo cost DESC (row đầu có cost cao nhất).
- Click row → pill vàng "Đang xem tenant: ..." xuất hiện, row highlight, các section re-fetch chỉ tenant đó.
- Click row đang active → bỏ filter. Hoặc click "Xem tất cả ×" → reset.
- DB không có data → "Không có dữ liệu tenant."

Stop server.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): top tenants table + drill-down filter (click row → page filter)"
```

---

## Task 10: By model table + daily chart

**Files:**
- Modify: `wwwroot/pages/admin.jsx` (thêm `ModelTable` + `DailyChart` components)
- Modify: `wwwroot/admin.css` (CSS chart container)

- [ ] **Step 1: Thêm `ModelTable` ngay sau `TenantsTable` trong `wwwroot/pages/admin.jsx`**

```jsx
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
```

- [ ] **Step 2: Thêm `DailyChart` ngay sau `ModelTable`**

```jsx
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
```

- [ ] **Step 3: Trong `AiUsagePage`, REPLACE block placeholder "📦 Task 10…" bằng**

```jsx
            <ModelTable rows={data.byModel} />
            <DailyChart rows={data.byDay} />
```

- [ ] **Step 4: Thêm CSS chart vào `wwwroot/admin.css`**

Append:

```css
.ai-usage-chart { height: 280px; position: relative; }
.ai-usage-table code { font-size: 12px; color: #4b5563; background: #f9fafb; padding: 2px 6px; border-radius: 4px; }
```

- [ ] **Step 5: Smoke test**

`dotnet run`, login, mở `/admin-trav-ai/ai-usage`. Kỳ vọng:
- Section "🤖 By model" — table với model + calls + tokens + cost.
- Section "📈 Chi phí theo ngày" — line chart orange, hover tooltip show "X ₫ · N call".
- Đổi range 7/30/90 → chart re-render.
- Click tenant row → chart re-fetch theo tenant đó.
- Chart không lỗi "Chart is not a constructor" (chart-loader đã lazy load).

Stop server.

- [ ] **Step 6: Commit**

```bash
git add wwwroot/pages/admin.jsx wwwroot/admin.css
git commit -m "feat(admin): by-model table + daily chart (Chart.js line, lazy via chart-loader)"
```

---

## Task 11: Docs (CLAUDE.md + database-schema.md)

**Files:**
- Modify: `CLAUDE.md` (thêm section "Admin governance" + API table rows)
- Modify: `docs/database-schema.md` (note `AiUsageHistory` cross-tenant role)

- [ ] **Step 1: Thêm rows vào API table `CLAUDE.md`**

Tìm bảng API trong section "API surface" (≈line `| GET | /healthz |`). Append vào cuối bảng (giữ format Markdown):

```markdown
| POST | `/api/v1/admin/auth/login`         | Admin login `{username,password}` → `{token,username,expiresAt}` |
| POST | `/api/v1/admin/auth/logout`        | header `X-Admin-Session` → `{ok}` |
| GET  | `/api/v1/admin/auth/me`            | header `X-Admin-Session` → `{username,expiresAt}` |
| GET  | `/api/v1/admin/ui/ai-usage`        | cross-tenant AI usage `?days=30&tenantId=` (require X-Admin-Session) |
```

- [ ] **Step 2: Thêm section mới "Admin governance" vào `CLAUDE.md`**

Tìm dòng kết section gần cuối (vd cuối "SmartMail AI feature"). Thêm section mới TRƯỚC "## Frontend layout":

```markdown
## Admin governance (`/admin-trav-ai/`)

Hệ quản trị admin riêng biệt với user-facing app. Entry HTML `wwwroot/admin-trav-ai.html` (KHÔNG share `index.html`). Toàn bộ shell + page components nằm trong 1 file `wwwroot/pages/admin.jsx`.

- **Auth**: cấu hình `Admin:Users` JSON trong `appsettings.json` (plain text password — admin pool nhỏ, self-host, file gitignore). `AdminUserStore.Authenticate` string-compare. Session in-mem `AdminSessionStore` (token GUID, 12h idle, KHÔNG persist). Client gửi `X-Admin-Session` header. Endpoint require qua extension `.RequireAdminSession()`.
- **Compatibility**: `/api/v1/admin/quota/*` (webhook ops) GIỮ NGUYÊN `Admin:Token` cũ — KHÔNG đụng. Mọi endpoint admin UI mới dùng `/api/v1/admin/ui/*` với `RequireAdminSession()`.
- **Cross-tenant usage**: `Services/Admin/AdminUsageRepository.cs` aggregate trên `dbo.AiUsageHistory` (4 query: totals/byModel/byTenant/byDay). Filter `Status='ok'` để khỏi double-count retry. `Tenant IS NULL` group thành `(system)`. Tenant name resolve qua `TkSessionRepository.GetTenantNamesAsync` (SELECT TOP 1 per tenant ORDER BY LastUsedUtc DESC, fallback `tenantId`).

### Thêm trang admin mới — 3 dòng

1. **Backend** — endpoint mới trong `Endpoints/AdminUiEndpoints.cs`:
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
```

- [ ] **Step 3: Update `docs/database-schema.md`**

Tìm row `AiUsageHistory` trong bảng "Bảng đang sử dụng" (≈row 14). Cập nhật cột "Mục đích" thêm note (insert sentence sau "...migrate sang đây 2026-06-24."):

Tìm:
```
| 14 | `dbo.AiUsageHistory` | **Granular** per-request AI usage history (mỗi AI call = 1 row). Bổ sung cho `AiUsageCounters` khi cần breakdown theo feature/session/tenant. Trước đây file `data/ai-usage.jsonl` → mất khi deploy → đã migrate sang đây 2026-06-24. |
```

Replace bằng:
```
| 14 | `dbo.AiUsageHistory` | **Granular** per-request AI usage history (mỗi AI call = 1 row). Bổ sung cho `AiUsageCounters` khi cần breakdown theo feature/session/tenant. Trước đây file `data/ai-usage.jsonl` → mất khi deploy → đã migrate sang đây 2026-06-24. **Source của admin cross-tenant view `/admin-trav-ai/ai-usage`** (xem [AdminUsageRepository](../Services/Admin/AdminUsageRepository.cs)). |
```

- [ ] **Step 4: Smoke test docs render OK**

```bash
git diff CLAUDE.md docs/database-schema.md
```

Verify: 4 row mới trong API table, section "Admin governance" có nội dung, row `AiUsageHistory` đã update.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md docs/database-schema.md
git commit -m "docs(admin): CLAUDE.md section + API rows + database-schema note for cross-tenant view"
```

---

## Verification end-to-end

Sau Task 11, manual full flow:

1. Thêm `Admin:Users` vào `appsettings.json` (đừng commit).
2. `dotnet run --project TourkitAiProxy.csproj`.
3. Mở `http://localhost:5080/admin-trav-ai/` → login form.
4. Login sai → error message.
5. Login đúng → AI Usage page với 4 stat card + 2 table + 1 chart.
6. Click row tenant → drill-down (4 section đều filter theo tenant).
7. "Xem tất cả ×" → reset.
8. Tab 7/30/90 → re-fetch.
9. Logout → về login form, localStorage cleared.
10. F5 trong trạng thái logged in → vẫn ở AI Usage (session persisted).
11. Restart server → F5 admin page → về login (in-mem session lost — đúng spec).

## Branch suggestion

Tạo từ `main`:
```bash
git checkout main
git pull
git checkout -b feat/admin-shell-ai-usage
```

11 commit theo plan; PR title gợi ý: `feat(admin): shell + AI usage cross-tenant page (/admin-trav-ai/)`.
