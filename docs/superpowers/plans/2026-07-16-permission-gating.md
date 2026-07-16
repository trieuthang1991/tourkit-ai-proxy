# Lấy & kiểm phân quyền TourKit trong proxy — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Proxy lấy danh sách quyền (Function_Code) của user từ TourKit.Api, lưu theo phiên, rồi (1) chặn `assign_task`/`create_appointment` khi thiếu `CV_TAOMOI`/`CS_KH_TAOMOI` lúc execute, (2) gate các trang tích hợp (`/widget-admin`, `/visa-config`, `/workflows`) theo `CH_HT_THAOTAC`, (3) cảnh báo khi tài khoản dịch vụ thiếu quyền ghi CRM.

**Architecture:** Quyền lấy 1 lần lúc login/relogin qua `GET /api/auth/permissions` (Bearer JWT), lưu vào `TkSession.Permissions` (in-mem) + cột mới `dbo.TkSessions.PermissionsJson` (persist qua restart). Backend enforce ở 2 điểm: `ActionExecutor.ExecuteCrmQueueAsync` (trước enqueue) và `WorkflowEndpoints` (mutation PerTenant + service-account). Frontend đọc `permissions[]` từ `/session` (đã có sẵn cơ chế `tourkitAuth`) và gate hiển thị trang. Mã quyền proxy-side khai báo trong `TkPermissionCodes` (đồng bộ tay với `toutkit-app/TourKit.Shared/PermissionCodes.cs`).

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper, SQL Server, React (UMD + Babel, no build), xUnit (`TourkitAiProxy.Tests`).

**Nguồn sự thật đã xác minh (upstream `toutkit-app`):**
- `GET /api/auth/permissions` (`[Authorize]`) → envelope `{ departmentId, departmentName, permissions: string[] }` — `TourKit.Api/Controllers/AuthController.cs:38`.
- Login (`POST /api/auth/login`) KHÔNG trả quyền — `TourKit.Shared/DTOs/AuthDtos.cs:12`.
- Mã quyền cần: `CV_TAOMOI` (tạo việc — `TaskingService.cs:545`), `CS_KH_TAOMOI` (tạo lịch/nhắc hẹn — `CustomerCareService.cs:595` throw `UnauthorizedAccessException`), `CH_HT_THAOTAC` (Cấu hình hệ thống — `PermissionCodes.cs:167`).

**Lưu ý test:** codebase chỉ có 1 test project cho pure-logic (`TourkitAiProxy.Tests`). Phần integration (endpoint/DB/frontend) verify thủ công bằng `dotnet build` + chạy + gọi thử, đúng convention hiện tại (IMAP/frontend "verified manually").

---

## File Structure

**Sửa (backend):**
- `Services/TourKit/TourKitApiClient.cs` — thêm `GetPermissionsAsync` + static `ParsePermissions`.
- `Services/TourKit/TkSession*.cs` — `TkSession.Permissions`; repo hydrate/upsert cột `PermissionsJson`; store fetch quyền lúc login/relogin + `HasPermission` + `EnsurePermissionsAsync`.
- `Services/Db/TourkitAiDb.cs` — ALTER thêm cột `PermissionsJson`.
- `Models/ChatModels.cs` — `LoginTokenResponse` thêm `Permissions` + `CanConfigSystem`.
- `Endpoints/ChatEndpoints.cs` — `ToLoginResponse` map quyền; `/session` async + ensure quyền.
- `Endpoints/AssistantActionEndpoints.cs` — không đổi (đã truyền `sessionId`).
- `Services/Chat/ActionExecutor.cs` — inject `TkSessionStore`; gate CRM actions trước enqueue.
- `Endpoints/WorkflowEndpoints.cs` — gate PerTenant mutation + service-account theo `CH_HT_THAOTAC`; crm-queue auto-scope theo user; cảnh báo quyền khi lưu service account.
- `Services/Crm/CrmActionQueueRepository.cs` — `ListForMonitorAsync` thêm lọc `username`.

**Tạo (backend):**
- `Services/TourKit/TkPermissionCodes.cs` — hằng mã quyền proxy dùng.

**Sửa (frontend):**
- `wwwroot/core/auth.jsx` — lưu `permissions` vào user object + `getPermissions()`/`hasPerm()`.
- `wwwroot/pages/widget-admin.jsx`, `wwwroot/pages/visa-config.jsx` — bọc export bằng gate `CH_HT_THAOTAC`.
- `wwwroot/pages/workflows.jsx` — thiếu quyền → chỉ hiện "Theo người dùng"; ẩn service account + PerTenant.
- `wwwroot/index.html` + `wwwroot/bundle-entry.js` — nạp component mới.

**Tạo (frontend):**
- `wwwroot/components/permission-gate.jsx` — `window.NoPermissionBox`.

**Tạo (test):**
- `TourkitAiProxy.Tests/TourKit/PermissionParseTests.cs`.

---

## Task 1: Lấy quyền từ upstream (`TourKitApiClient`)

**Files:**
- Modify: `Services/TourKit/TourKitApiClient.cs`
- Test: `TourkitAiProxy.Tests/TourKit/PermissionParseTests.cs`

- [ ] **Step 1: Viết test parse quyền (fail trước)**

Create `TourkitAiProxy.Tests/TourKit/PermissionParseTests.cs`:

```csharp
using System.Text.Json;
using TourkitAiProxy.Services.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests.TourKit;

public class PermissionParseTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParsePermissions_reads_camelCase_array()
    {
        var el = Parse("""{"departmentId":3,"departmentName":"Sales","permissions":["CV_TAOMOI","CS_KH_TAOMOI"]}""");
        var got = TourKitApiClient.ParsePermissions(el);
        Assert.Equal(new[] { "CV_TAOMOI", "CS_KH_TAOMOI" }, got);
    }

    [Fact]
    public void ParsePermissions_trims_and_skips_blanks()
    {
        var el = Parse("""{"permissions":["  CH_HT_THAOTAC  ","",null,"CV_TAOMOI"]}""");
        var got = TourKitApiClient.ParsePermissions(el);
        Assert.Equal(new[] { "CH_HT_THAOTAC", "CV_TAOMOI" }, got);
    }

    [Fact]
    public void ParsePermissions_missing_field_returns_empty()
    {
        var el = Parse("""{"departmentId":0}""");
        Assert.Empty(TourKitApiClient.ParsePermissions(el));
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL biên dịch**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter PermissionParseTests`
Expected: FAIL — `TourKitApiClient.ParsePermissions` chưa tồn tại (build error).

- [ ] **Step 3: Thêm `ParsePermissions` + `GetPermissionsAsync`**

Trong `Services/TourKit/TourKitApiClient.cs`, thêm ngay sau method `GetAsync(...)` (kết thúc quanh dòng 130). `_log` + `System.Text.Json` đã có sẵn trong file.

```csharp
    /// GET /api/auth/permissions (Bearer JWT) → mã Function_Code của phòng ban user.
    /// Trả:
    ///   • non-null (kể cả LIST RỖNG) = LẤY THÀNH CÔNG — rỗng nghĩa là user thật sự không có quyền nào.
    ///   • null = LẤY LỖI (transient/upstream down) sau khi retry — caller PHẢI thử lại lần sau,
    ///            KHÔNG được cache rỗng như thể authoritative (nếu không sẽ khoá nhầm user vì 1 blip).
    /// Gate xử lý: rỗng/thiếu quyền → chặn (fail-closed); null → giữ trạng thái "chưa loaded" để tự lấy lại.
    public async Task<List<string>?> GetPermissionsAsync(string jwt, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var data = await GetAsync(jwt, "/api/auth/permissions", ct);
                return ParsePermissions(data);   // 200 OK — kể cả mảng rỗng = thành công
            }
            catch (TourKitApiException ex) when (ex.Status is 401 or 403)
            {
                // 401/403 = câu trả lời DỨT KHOÁT của upstream (token/không quyền truy cập endpoint),
                // không phải transient → coi như "không có quyền", KHÔNG retry.
                _log.LogWarning("[TourKit] GetPermissions {Status} — coi như không có quyền", ex.Status);
                return new List<string>();
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning("[TourKit] GetPermissions lỗi (lần {N}/{Max}) — retry: {Err}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(200 * attempt, ct);   // backoff 200ms, 400ms
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TourKit] GetPermissions cạn retry — trả null (sẽ lấy lại sau)");
                return null;
            }
        }
        return null;
    }

    /// Pure: rút mảng `permissions` (camelCase hoặc PascalCase) từ envelope → List trimmed, bỏ rỗng.
    public static List<string> ParsePermissions(System.Text.Json.JsonElement data)
    {
        var list = new List<string>();
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return list;
        if (!data.TryGetProperty("permissions", out var arr) &&
            !data.TryGetProperty("Permissions", out arr)) return list;
        if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
        return list;
    }
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter PermissionParseTests`
Expected: PASS (3 test).

- [ ] **Step 5: Commit**

```bash
git add Services/TourKit/TourKitApiClient.cs TourkitAiProxy.Tests/TourKit/PermissionParseTests.cs
git commit -m "feat(auth): lấy quyền TourKit qua GET /api/auth/permissions"
```

---

## Task 2: Mã quyền proxy-side (`TkPermissionCodes`)

**Files:**
- Create: `Services/TourKit/TkPermissionCodes.cs`

- [ ] **Step 1: Tạo file hằng mã quyền**

Create `Services/TourKit/TkPermissionCodes.cs`:

```csharp
namespace TourkitAiProxy.Services.TourKit;

/// Mã quyền TourKit (Function_Code) mà PROXY thực sự kiểm. Đồng bộ TAY với nguồn gốc
/// toutkit-app/TourKit.Shared/PermissionCodes.cs — CHỈ khai báo mã proxy dùng (không copy hết ~200 mã).
public static class TkPermissionCodes
{
    /// Công việc — tạo mới (assign_task). TaskingService.cs:545.
    public const string TaoViec = "CV_TAOMOI";
    /// Chăm sóc KH — tạo mới nhắc/hẹn (create_appointment). CustomerCareService.cs:595.
    public const string TaoNhacHen = "CS_KH_TAOMOI";
    /// Cấu hình hệ thống — thao tác (gate trang tích hợp). PermissionCodes.cs:167.
    public const string CauHinhHeThong = "CH_HT_THAOTAC";
}
```

- [ ] **Step 2: Build kiểm biên dịch**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Services/TourKit/TkPermissionCodes.cs
git commit -m "feat(auth): khai báo mã quyền proxy dùng (TkPermissionCodes)"
```

---

## Task 3: Lưu quyền theo phiên (`TkSession` + SQL + fetch lúc login)

**Files:**
- Modify: `Services/TourKit/TkSessionStore.cs`
- Modify: `Services/TourKit/TkSessionRepository.cs`
- Modify: `Services/Db/TourkitAiDb.cs:379` (sau block TkSessions)

- [ ] **Step 1: Thêm cột `PermissionsJson`**

Trong `Services/Db/TourkitAiDb.cs`, ngay SAU `END;` của block `dbo.TkSessions` (dòng 379), thêm:

```sql

IF COL_LENGTH('dbo.TkSessions', 'PermissionsJson') IS NULL
    ALTER TABLE dbo.TkSessions ADD PermissionsJson NVARCHAR(MAX) NULL;
```

- [ ] **Step 2: `TkSession.Permissions`**

Trong `Services/TourKit/TkSessionStore.cs`, thêm vào class `TkSession` (sau dòng 26 `ChatMemory`):

```csharp
    // Quyền TourKit (Function_Code) của user — lấy lúc login/relogin, persist cột PermissionsJson.
    public List<string> Permissions { get; set; } = new();
    // true = ĐÃ lấy quyền thành công (kể cả rỗng). false = chưa lấy được (mới tạo / fetch lỗi) → EnsurePermissions retry.
    // Persist gián tiếp: PermissionsJson != null ⇔ Loaded (rỗng lưu "[]", chưa loaded lưu NULL).
    public bool PermissionsLoaded { get; set; }
```

- [ ] **Step 3: Repo — SELECT + hydrate + upsert cột mới**

Trong `Services/TourKit/TkSessionRepository.cs`:

(a) Thêm vào `class Row` (sau `ChatMemoryJson`, dòng 40):

```csharp
        public string? PermissionsJson { get; set; }
```

(b) 3 câu SELECT (dòng 52, 105, 129) — thêm `PermissionsJson` vào danh sách cột. Ví dụ dòng 52 đổi thành:

```csharp
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, PermissionsJson, LastUsedUtc " +
```

Áp dụng y hệt cho SELECT trong `GetAsync` (dòng 105) và `GetByUserAsync` (`SELECT TOP 1 ...`, dòng 129).

(c) `UpsertAsync` — trước `try` (sau dòng 222 `memJson`), thêm serialize. **Lưu `[]` khi đã loaded-rỗng, `null` khi CHƯA loaded** — để phân biệt "user thật sự không quyền" vs "chưa lấy được" qua restart:

```csharp
        var permJson = s.PermissionsLoaded ? JsonSerializer.Serialize(s.Permissions, _jsonOpts) : null;
```

Trong MERGE: thêm `PermissionsJson = @PermissionsJson,` vào khối `WHEN MATCHED THEN UPDATE SET`; thêm `PermissionsJson` vào cột INSERT và `@PermissionsJson` vào VALUES; thêm `PermissionsJson = permJson,` vào anonymous param object.

(d) `TryHydrate` (dòng 296) — sau khi parse `mem`, thêm. `PermissionsJson != null` ⇔ đã loaded:

```csharp
        List<string> perms = new();
        bool permsLoaded = r.PermissionsJson != null;
        if (!string.IsNullOrWhiteSpace(r.PermissionsJson))
        {
            try { perms = JsonSerializer.Deserialize<List<string>>(r.PermissionsJson, _jsonOpts) ?? new(); }
            catch (Exception ex) { _log.LogWarning(ex, "[TkSessionRepo] Permissions parse fail {Id}", r.Id); }
        }
```

Và thêm `Permissions = perms, PermissionsLoaded = permsLoaded,` vào object `new TkSession { ... }` (sau `ChatMemory = ...`).

- [ ] **Step 4: Store — fetch quyền lúc login/relogin + helpers**

Trong `Services/TourKit/TkSessionStore.cs`:

(a) `CreateAsync` — sau dòng 80 `var login = await _api.LoginAsync(...)`, thêm (null = fetch lỗi → để Loaded=false, Ensure sẽ retry):

```csharp
        var permissions = await _api.GetPermissionsAsync(login.Token, ct);   // null nếu upstream lỗi (retry sau)
```

Trong nhánh `existing != null` (sau dòng 94 `existing.LastUsed = ...`) thêm:

```csharp
            if (permissions != null) { existing.Permissions = permissions; existing.PermissionsLoaded = true; }
```

Trong nhánh tạo mới: KHÔNG set trong object initializer (mặc định rỗng/false); sau khi tạo `session`, thêm trước `_cache[session.Id] = session;`:

```csharp
                if (permissions != null) { session.Permissions = permissions; session.PermissionsLoaded = true; }
```

(b) `ReloginAsync` (dòng 227) — sau `var login = await _api.LoginAsync(...)`, thêm:

```csharp
        var perms = await _api.GetPermissionsAsync(login.Token, ct);
        if (perms != null) { s.Permissions = perms; s.PermissionsLoaded = true; }
```

(c) Thêm 2 method public (đặt sau `Get(...)`, quanh dòng 144):

```csharp
    /// True nếu phiên có mã quyền `code` (case-insensitive). Phiên không tồn tại / chưa nạp quyền → false.
    public bool HasPermission(string? sessionId, string code)
    {
        var s = Get(sessionId);
        if (s == null || string.IsNullOrWhiteSpace(code)) return false;
        return s.Permissions.Any(p => string.Equals(p, code, StringComparison.OrdinalIgnoreCase));
    }

    /// Nạp quyền cho phiên CHƯA loaded (mới tạo lỗi fetch / session cũ trước tính năng / restart mà
    /// PermissionsJson NULL). Tự lấy LẠI cho tới khi thành công — điều kiện là `!PermissionsLoaded`, nên
    /// khi đã loaded (kể cả rỗng authoritative) thì NO-OP → bounded, không spam upstream mỗi request.
    public async Task EnsurePermissionsAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        if (s == null || s.PermissionsLoaded) return;
        try
        {
            var jwt = await GetValidJwtAsync(sessionId, ct);   // relogin nếu JWT rỗng/hết hạn
            var perms = await _api.GetPermissionsAsync(jwt, ct);
            if (perms != null)   // null = vẫn lỗi → giữ Loaded=false, thử lại lần sau
            {
                s.Permissions = perms;
                s.PermissionsLoaded = true;
                await _repo.UpsertAsync(s, ct);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[TkSessionStore] EnsurePermissions {Id} lỗi", sessionId); }
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Services/TourKit/TkSessionStore.cs Services/TourKit/TkSessionRepository.cs Services/Db/TourkitAiDb.cs
git commit -m "feat(auth): lưu quyền theo phiên + persist PermissionsJson + HasPermission helper"
```

---

## Task 4: Trả quyền ra client (`/session` + login responses)

**Files:**
- Modify: `Models/ChatModels.cs:22-28`
- Modify: `Endpoints/ChatEndpoints.cs:97-105` (GET /session), `:257-263` (ToLoginResponse)

- [ ] **Step 1: Mở rộng `LoginTokenResponse`**

Trong `Models/ChatModels.cs`, đổi record (dòng 22):

```csharp
public record LoginTokenResponse(
    string SessionId,
    string TenantId,
    string? FullName,
    string? CompanyName,
    long ExpiresAt,
    IReadOnlyList<string> Permissions,
    bool CanConfigSystem
);
```

- [ ] **Step 2: `ToLoginResponse` map quyền**

Trong `Endpoints/ChatEndpoints.cs`, đổi `ToLoginResponse` (dòng 257). `using TourkitAiProxy.Services.TourKit;` đã có ở dòng 5.

```csharp
    private static LoginTokenResponse ToLoginResponse(TourkitAiProxy.Services.TourKit.TkSession s)
        => new(
            SessionId:       s.Id,
            TenantId:        s.TenantId,
            FullName:        s.FullName,
            CompanyName:     s.CompanyName,
            ExpiresAt:       new DateTimeOffset(s.JwtExpiresAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Permissions:     s.Permissions,
            CanConfigSystem: s.Permissions.Any(p => string.Equals(p, TkPermissionCodes.CauHinhHeThong, StringComparison.OrdinalIgnoreCase)));
```

- [ ] **Step 3: `/session` async + ensure quyền**

Trong `Endpoints/ChatEndpoints.cs`, thay handler `GET /session` (dòng 97-105):

```csharp
        // ─── GET /session ─── lấy lại info phiên (tên/công ty/quyền) cho header sau reload/restart ──
        v1.MapGet("/session", async (TkSessionStore sessions, HttpContext ctx) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                      ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var s = sessions.Get(sid);
            if (s == null)
                return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            // Session cũ (trước tính năng) hoặc vừa restart → nạp quyền 1 lần rồi trả.
            await sessions.EnsurePermissionsAsync(sid!, ctx.RequestAborted);
            return Results.Json(ToLoginResponse(sessions.Get(sid)!));
        });
```

- [ ] **Step 4: Build + kiểm thủ công**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

Chạy `dotnet run --project TourkitAiProxy.csproj`, đăng nhập, rồi:
`curl -s http://localhost:5080/api/v1/session -H "X-Session-Id: <sid>"` → JSON có `permissions:[...]` và `canConfigSystem:true|false`.

- [ ] **Step 5: Commit**

```bash
git add Models/ChatModels.cs Endpoints/ChatEndpoints.cs
git commit -m "feat(auth): trả permissions + canConfigSystem trong /session và login"
```

---

## Task 5: Frontend đọc quyền (`tourkitAuth`)

**Files:**
- Modify: `wwwroot/core/auth.jsx:18-24` (setSession), `:49-62` (refresh), `:99-102` (export)

- [ ] **Step 1: `setSession` lưu quyền**

Trong `wwwroot/core/auth.jsx`, đổi `setSession` (dòng 18):

```javascript
  function setSession(data) {
    localStorage.setItem(SESSION_KEY, data.sessionId);
    const user = {
      fullName: data.fullName, companyName: data.companyName, tenantId: data.tenantId,
      permissions: data.permissions || [], canConfigSystem: !!data.canConfigSystem,
    };
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    emit();
    return user;
  }
```

- [ ] **Step 2: `refresh` lưu quyền**

Trong `refresh` (dòng 57), đổi dòng dựng `user`:

```javascript
      const user = {
        fullName: info.fullName, companyName: info.companyName, tenantId: info.tenantId,
        permissions: info.permissions || [], canConfigSystem: !!info.canConfigSystem,
      };
```

- [ ] **Step 3: Thêm helper + export**

Trước `window.tourkitAuth = {` (dòng 99), thêm:

```javascript
  const getPermissions = () => { const u = getUser(); return (u && u.permissions) || []; };
  const hasPerm = (code) => getPermissions().indexOf(code) !== -1;
```

Đổi object export (dòng 99-102) để thêm `getPermissions, hasPerm`:

```javascript
  window.tourkitAuth = {
    SESSION_KEY, USER_KEY,
    getSessionId, getUser, isAuthed, login, loginToken, logout, onChange, refresh, authedFetch,
    getPermissions, hasPerm,
  };
```

- [ ] **Step 4: Kiểm thủ công (console)**

Load app, đăng nhập, mở DevTools console:
`window.tourkitAuth.getPermissions()` → mảng mã quyền; `window.tourkitAuth.hasPerm('CH_HT_THAOTAC')` → true/false.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/core/auth.jsx
git commit -m "feat(auth): frontend lưu + expose permissions qua tourkitAuth"
```

---

## Task 6: Gate action tools lúc execute (`ActionExecutor`)

**Files:**
- Modify: `Services/Chat/ActionExecutor.cs:22-62` (DI), `:121-122` (dispatch), `:464-478` (ExecuteCrmQueueAsync)

- [ ] **Step 1: Inject `TkSessionStore`**

Trong `Services/Chat/ActionExecutor.cs`:

(a) Thêm field (sau dòng 32 `_mailAccount`):

```csharp
    private readonly TkSessionStore _sessions;
```

(b) Constructor (dòng 50-62): thêm tham số `TkSessionStore sessions` (đặt trước `AiCallContext aiCtx`) và gán `_sessions = sessions;`. `using TourkitAiProxy.Services.TourKit;` đã có (dòng 9).

- [ ] **Step 2: Truyền `sessionId` vào nhánh CRM**

Dòng 121-122, đổi:

```csharp
            case ActionKind.CrmQueue:
                return await ExecuteCrmQueueAsync(req, tenantId, username, jwt, sessionId, ct);
```

- [ ] **Step 3: Gate trước enqueue**

Thay `ExecuteCrmQueueAsync` (dòng 464-478):

```csharp
    private async Task<ActionResult> ExecuteCrmQueueAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, string? sessionId, CancellationToken ct)
    {
        if (_done.TryGetValue(req.ActionId, out var cached)) return cached;

        // Tự lấy lại quyền nếu phiên chưa loaded (fetch lỗi lúc login / restart) — no-op khi đã loaded.
        await _sessions.EnsurePermissionsAsync(sessionId ?? "", ct);

        // Kiểm quyền TRƯỚC khi enqueue — thiếu quyền báo ngay, KHÔNG đưa vào hàng đợi (tránh worker
        // POST CRM rồi bị TourKit.Api từ chối 403; UX xấu vì user tưởng đã tạo). Mã khớp PermissionCodes.
        var (needPerm, permLabel) = req.Action.ToLowerInvariant() switch
        {
            "assign_task"        => (TkPermissionCodes.TaoViec,    "tạo việc"),
            "create_appointment" => (TkPermissionCodes.TaoNhacHen, "tạo lịch hẹn"),
            _ => ("", "")
        };
        if (!string.IsNullOrEmpty(needPerm) && !_sessions.HasPermission(sessionId, needPerm))
        {
            _log.LogInformation("[ActionExecutor] {Action} DENIED tenant={Tenant} user={User} — thiếu quyền {Perm}",
                req.Action, tenantId, username, needPerm);
            return new ActionResult(req.Action,
                $"Bạn không có quyền {permLabel} trong hệ thống. Vui lòng liên hệ quản trị viên để được cấp quyền.");
        }

        var (result, success) = req.Action.ToLowerInvariant() switch
        {
            "assign_task" => await ExecuteAssignTaskAsync(req, tenantId, username, jwt, ct),
            "create_appointment" => await ExecuteCreateAppointmentAsync(req, tenantId, username, jwt, ct),
            _ => throw new InvalidOperationException($"Unhandled CrmQueue action: {req.Action}")
        };

        if (success) _done[req.ActionId] = result;
        return result;
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded (nếu báo lỗi DI thứ tự tham số — kiểm lại constructor Step 1b).

- [ ] **Step 5: Kiểm thủ công**

Đăng nhập bằng user THIẾU `CV_TAOMOI`, ở `/assistant` yêu cầu "giao việc cho …" → xác nhận →
`POST /api/v1/assistant/action/execute` trả `message` = "Bạn không có quyền tạo việc…"; kiểm `dbo.CrmActionQueue` KHÔNG có row mới. Với user CÓ quyền → enqueue bình thường.

- [ ] **Step 6: Commit**

```bash
git add Services/Chat/ActionExecutor.cs
git commit -m "feat(auth): chặn assign_task/create_appointment khi thiếu quyền (lúc execute)"
```

---

## Task 7: Gate mutation workflow PerTenant + service-account (server-side)

**Files:**
- Modify: `Endpoints/WorkflowEndpoints.cs` — PUT `{type}` (dòng 69), run-now (dòng 116), service-account POST (dòng 183)/DELETE (dòng 236); helpers (dòng 323+)

- [ ] **Step 1: Thêm helper quyền**

Trong `Endpoints/WorkflowEndpoints.cs`, sau `Unauthorized()` (dòng 335), thêm. `using TourkitAiProxy.Services.TourKit;` đã có (dòng 5).

```csharp
    /// Ensure quyền (tự lấy lại nếu chưa loaded) rồi kiểm CH_HT_THAOTAC. async vì có thể fetch upstream.
    private static async Task<bool> CanConfigSystemAsync(string sid, TkSessionStore sessions, CancellationToken ct)
    {
        await sessions.EnsurePermissionsAsync(sid, ct);
        return sessions.HasPermission(sid, TkPermissionCodes.CauHinhHeThong);
    }

    private static IResult Forbidden()
        => Results.Json(new { error = "Bạn không có quyền Cấu hình hệ thống (CH_HT_THAOTAC)." }, statusCode: 403);
```

> Các handler gate bên dưới PHẢI là `async` (PUT + run-now hiện là lambda sync → thêm `async`; các handler còn lại đã async).

- [ ] **Step 2: Gate PUT `{type}` cho PerTenant**

Đổi signature lambda PUT (dòng 69) sang `async` — thêm `async` trước `(` và bảo đảm có `ctx` (đã có). Trong handler (dòng 77-83), đổi để giữ `sid` và chặn sau khi resolve `wf`:

```csharp
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant, user) = auth.Value;

            var wf = registry.Resolve(type);
            if (wf == null)
                return Results.Json(new { error = $"Workflow '{type}' không tồn tại" }, statusCode: 404);
            if (wf.Scope == WorkflowScope.PerTenant && !await CanConfigSystemAsync(sid, sessions, ctx.RequestAborted))
                return Forbidden();
```

- [ ] **Step 3: Gate run-now cho PerTenant**

Đổi signature lambda `run-now` (dòng 116) sang `async`. Trong handler (dòng 124-130), đổi tương tự:

```csharp
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant, user) = auth.Value;

            var wf = registry.Resolve(type);
            if (wf == null)
                return Results.Json(new { error = $"Workflow '{type}' không tồn tại" }, statusCode: 404);
            if (wf.Scope == WorkflowScope.PerTenant && !await CanConfigSystemAsync(sid, sessions, ctx.RequestAborted))
                return Forbidden();
```

> `run-now` giữ nguyên phần fire-and-forget `_ = scheduler.RunOneAsync(...)` phía dưới — chỉ thêm `async` + gate ở đầu.

- [ ] **Step 4: Gate service-account POST + DELETE**

POST (dòng 190-192, đã async) đổi:

```csharp
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant, user) = auth.Value;
            if (!await CanConfigSystemAsync(sid, sessions, ctx.RequestAborted)) return Forbidden();
```

DELETE (dòng 241-243, đã async) đổi:

```csharp
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant, _) = auth.Value;
            if (!await CanConfigSystemAsync(sid, sessions, ctx.RequestAborted)) return Forbidden();
```

> GET `/workflows/service-account` (dòng 222) để nguyên — read-only, frontend không gọi khi thiếu quyền.

- [ ] **Step 5: Build + kiểm thủ công**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

User thiếu `CH_HT_THAOTAC`: `PUT /api/v1/workflows/deal-auto-review` → 403; `POST /api/v1/workflows/service-account` → 403. User `mail-auto-sync` (PerUser) PUT vẫn 200.

- [ ] **Step 6: Commit**

```bash
git add Endpoints/WorkflowEndpoints.cs
git commit -m "feat(auth): gate cấu hình workflow PerTenant + service-account theo CH_HT_THAOTAC"
```

---

## Task 8: crm-queue auto-scope theo người dùng khi thiếu quyền

**Files:**
- Modify: `Services/Crm/CrmActionQueueRepository.cs:28-44`
- Modify: `Endpoints/WorkflowEndpoints.cs:305-318` (GET /workflows/crm-queue)

- [ ] **Step 1: Thêm lọc `username` vào repo**

Trong `Services/Crm/CrmActionQueueRepository.cs`, đổi `ListForMonitorAsync`:

```csharp
    /// Đọc cho trang theo dõi (lọc Kind/Status/Username, mới nhất trước).
    /// username != null → chỉ hành động do user đó tạo (dùng khi user thiếu quyền Cấu hình hệ thống).
    public async Task<List<CrmActionRow>> ListForMonitorAsync(
        string tenantId, string? kind, int? status, int take, CancellationToken ct = default,
        string? username = null)
    {
        if (take < 1) take = 1; if (take > 500) take = 500;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<CrmActionRow>(@"
SELECT TOP (@take)
    Id, TenantId, Username, Kind, PayloadJson, [Status], ResultJson,
    RetryCount, ErrorMessage, CreatedUtc, ProcessedUtc
FROM dbo.CrmActionQueue
WHERE TenantId = @tenantId
  AND (@kind IS NULL OR Kind = @kind)
  AND (@status IS NULL OR [Status] = @status)
  AND (@username IS NULL OR Username = @username)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take, username });
        return rows.AsList();
    }
```

- [ ] **Step 2: Endpoint auto-scope theo quyền**

Trong `Endpoints/WorkflowEndpoints.cs`, đổi handler `GET /workflows/crm-queue` (dòng 313-317):

```csharp
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant, user) = auth.Value;
            // Thiếu quyền Cấu hình hệ thống → chỉ thấy hành động DO CHÍNH MÌNH tạo (enforce server-side).
            var scopeUser = await CanConfigSystemAsync(sid, sessions, ctx.RequestAborted) ? null : user;
            var items = await crmQueue.ListForMonitorAsync(tenant, kind, status, limit ?? 50, ctx.RequestAborted, scopeUser);
            return Results.Json(new { items });
```

- [ ] **Step 3: Build + kiểm thủ công**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

User thiếu quyền: `GET /api/v1/workflows/crm-queue` chỉ trả row có `username` = user đó. User có quyền: trả toàn tenant.

- [ ] **Step 4: Commit**

```bash
git add Services/Crm/CrmActionQueueRepository.cs Endpoints/WorkflowEndpoints.cs
git commit -m "feat(auth): crm-queue chỉ trả hành động của user khi thiếu quyền cấu hình"
```

---

## Task 9: Component `NoPermissionBox` + nạp vào app

**Files:**
- Create: `wwwroot/components/permission-gate.jsx`
- Modify: `wwwroot/index.html` (thêm script tag), `wwwroot/bundle-entry.js` (thêm import)

- [ ] **Step 1: Tạo component**

Create `wwwroot/components/permission-gate.jsx`:

```jsx
// components/permission-gate.jsx — hộp "Không có quyền" + helper bọc trang theo quyền.
//   window.NoPermissionBox  — màn báo thiếu quyền (dùng khi trang yêu cầu 1 permission cụ thể)
(function () {
  'use strict';

  function NoPermissionBox({ feature }) {
    return (
      <main className="page" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
        <div style={{ maxWidth: 440, textAlign: 'center', padding: 36, borderRadius: 16,
          border: '1px solid var(--border)', background: 'var(--surface)' }}>
          <div style={{ fontSize: 46, marginBottom: 14 }}>🔒</div>
          <h2 style={{ margin: '0 0 10px' }}>Không có quyền truy cập</h2>
          <p style={{ color: 'var(--text-2)', lineHeight: 1.65, margin: 0 }}>
            Tính năng <b>{feature}</b> yêu cầu quyền <b>Cấu hình hệ thống</b>.
            Vui lòng liên hệ quản trị viên để được cấp quyền.
          </p>
        </div>
      </main>
    );
  }

  window.NoPermissionBox = NoPermissionBox;
})();
```

- [ ] **Step 2: Nạp vào `index.html` (dev/Babel mode)**

Trong `wwwroot/index.html`, tìm dòng nạp `components/dialogs.jsx` và thêm NGAY SAU:

```html
    <script type="text/babel" src="components/permission-gate.jsx"></script>
```

- [ ] **Step 3: Nạp vào `bundle-entry.js` (prod/esbuild mode)**

Trong `wwwroot/bundle-entry.js`, tìm import `./components/dialogs.jsx` và thêm NGAY SAU:

```javascript
import "./components/permission-gate.jsx";
```

> BẮT BUỘC cả 2 bước — thiếu bundle-entry thì prod bundle mất component → `React #130`.

- [ ] **Step 4: Kiểm thủ công**

Load app (dev), console: `typeof window.NoPermissionBox` → `"function"`.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/components/permission-gate.jsx wwwroot/index.html wwwroot/bundle-entry.js
git commit -m "feat(ui): component NoPermissionBox + nạp vào cả 2 mode"
```

---

## Task 10: Gate trang `/widget-admin` + `/visa-config`

**Files:**
- Modify: `wwwroot/pages/widget-admin.jsx:395` (export)
- Modify: `wwwroot/pages/visa-config.jsx:159` (export)

- [ ] **Step 1: Bọc export `WidgetAdminPage`**

Trong `wwwroot/pages/widget-admin.jsx`, đổi dòng cuối (dòng 395):

```jsx
window.WidgetAdminPage = function WidgetAdminPageGate(props) {
  if (!window.tourkitAuth.hasPerm('CH_HT_THAOTAC'))
    return <window.NoPermissionBox feature="Widget Chat" />;
  return <WidgetAdminPage {...props} />;
};
```

> Bọc ở export (không thêm early-return trong thân component) → tránh vi phạm rules-of-hooks; wrapper không gọi hook nào.

- [ ] **Step 2: Bọc export `VisaConfigPage`**

Trong `wwwroot/pages/visa-config.jsx`, đổi dòng export (dòng 159). Lưu ý file này định nghĩa `VisaConfigPage` bên trong IIFE và gán `window.VisaConfigPage = VisaConfigPage;` — đổi thành:

```jsx
  window.VisaConfigPage = function VisaConfigPageGate(props) {
    if (!window.tourkitAuth.hasPerm('CH_HT_THAOTAC'))
      return <window.NoPermissionBox feature="Câu hỏi Visa" />;
    return <VisaConfigPage {...props} />;
  };
```

- [ ] **Step 3: Kiểm thủ công**

User thiếu `CH_HT_THAOTAC`: vào `/widget-admin` và `/visa-config` → thấy hộp "Không có quyền truy cập". User có quyền → trang hiện bình thường.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/widget-admin.jsx wwwroot/pages/visa-config.jsx
git commit -m "feat(ui): gate /widget-admin và /visa-config theo CH_HT_THAOTAC"
```

---

## Task 11: `/workflows` — thiếu quyền chỉ hiện "Theo người dùng"

**Files:**
- Modify: `wwwroot/pages/workflows.jsx:895-1016` (WorkflowsPage)

- [ ] **Step 1: Đọc quyền + guard loadSa**

Trong `WorkflowsPage` (dòng 895), thêm ngay sau khai báo state (sau dòng 899 `saConfigured`):

```jsx
  const canConfig = window.tourkitAuth.hasPerm('CH_HT_THAOTAC');
```

Đổi effect load (dòng 918) để bỏ qua `loadSa` khi thiếu quyền (tránh gọi endpoint không cần):

```jsx
  uE(() => { loadWorkflows(); if (canConfig) loadSa(); }, []);
```

- [ ] **Step 2: Ẩn PerTenant + service account khi thiếu quyền**

Trong khối render (dòng 981-1016), đổi 2 dòng filter (dòng 982-983):

```jsx
        const perUser = workflows.filter(w => w.scope === 'PerUser');
        const perTenant = canConfig ? workflows.filter(w => w.scope === 'PerTenant') : [];
```

Vì `perTenant` rỗng khi thiếu quyền → toàn bộ `section` "Theo tổ chức" (kèm `ServiceAccountConfig`) tự bị bỏ (điều kiện `perTenant.length > 0` ở dòng 1002). Không cần sửa thêm trong khối đó.

> `<CrmQueueCard />` (dòng 1018) giữ nguyên: backend đã auto-scope theo user khi thiếu quyền (Task 8) → user chỉ thấy hàng đợi của chính mình.

- [ ] **Step 3: Kiểm thủ công**

User thiếu `CH_HT_THAOTAC`: `/workflows` chỉ hiện nhóm "Theo người dùng" (mail-auto-sync) + hàng đợi CRM của chính mình; KHÔNG thấy tài khoản dịch vụ / deal-auto-review / customer-auto-review. User có quyền → thấy đầy đủ.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/workflows.jsx
git commit -m "feat(ui): /workflows thiếu quyền chỉ hiện scope theo người dùng"
```

---

## Task 12: Cảnh báo quyền khi lưu tài khoản dịch vụ

**Files:**
- Modify: `Endpoints/WorkflowEndpoints.cs:196-219` (POST /workflows/service-account)

- [ ] **Step 1: Fetch quyền của service account + gộp cảnh báo**

Trong handler POST `/workflows/service-account`, sau khi `login` OK và tính `dealsVisible` (trước `await store.UpsertAsync(...)`, dòng 208), thêm:

```csharp
                // Cảnh báo (KHÔNG chặn lưu) nếu tài khoản dịch vụ thiếu quyền ghi CRM mà automation
                // tương lai có thể cần (giao việc / lịch hẹn). Deal visibility đã cảnh báo qua dealsVisible.
                var saPerms = await api.GetPermissionsAsync(login.Token, ctx.RequestAborted) ?? new();
                var missing = new List<string>();
                if (!saPerms.Any(p => string.Equals(p, TkPermissionCodes.TaoViec, StringComparison.OrdinalIgnoreCase)))
                    missing.Add("tạo việc (CV_TAOMOI)");
                if (!saPerms.Any(p => string.Equals(p, TkPermissionCodes.TaoNhacHen, StringComparison.OrdinalIgnoreCase)))
                    missing.Add("tạo lịch hẹn (CS_KH_TAOMOI)");
```

Đổi câu `return Results.Json(new { ok = true, ... })` (dòng 209) thành gộp cả 2 loại cảnh báo:

```csharp
                var warnings = new List<string>();
                if (dealsVisible == 0) warnings.Add("Đăng nhập OK nhưng thấy 0 deal — có thể thiếu quyền CH_XEM_ALL.");
                if (missing.Count > 0) warnings.Add("Tài khoản thiếu quyền: " + string.Join(", ", missing) + ".");
                await store.UpsertAsync(tenant, req.Username.Trim(), req.Password, updatedBy: user, ctx.RequestAborted);
                return Results.Json(new { ok = true, dealsVisible, warning = warnings.Count > 0 ? string.Join(" ", warnings) : (string?)null });
```

> Xoá `await store.UpsertAsync(...)` cũ ở dòng 208 (đã chuyển vào block trên để giữ đúng thứ tự).

- [ ] **Step 2: Build + kiểm thủ công**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: Build succeeded.

Lưu service account bằng tài khoản thiếu `CV_TAOMOI`/`CS_KH_TAOMOI` → response `warning` liệt kê quyền thiếu; vẫn `ok:true` (đã lưu). Frontend `ServiceAccountConfig` đã hiển thị `warning` sẵn (không cần sửa FE).

- [ ] **Step 3: Commit**

```bash
git add Endpoints/WorkflowEndpoints.cs
git commit -m "feat(auth): cảnh báo khi tài khoản dịch vụ thiếu quyền ghi CRM"
```

---

## Task 13: Regression check + GitNexus re-index

- [ ] **Step 1: Build toàn bộ (web + worker) + test**

Run:
```bash
dotnet build TourkitAiProxy.csproj
dotnet build TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: 3 lệnh succeed; test PASS (bao gồm 3 test mới Task 1).

- [ ] **Step 2: Smoke test 2 luồng chính**

Chạy app. Với 1 user **thiếu** `CH_HT_THAOTAC` và thiếu `CV_TAOMOI`:
- `/assistant` giao việc → bị chặn (message thiếu quyền), queue không tăng.
- `/widget-admin`, `/visa-config` → hộp "Không có quyền".
- `/workflows` → chỉ "Theo người dùng".

Với 1 user **đủ quyền**: tất cả hoạt động như trước.

- [ ] **Step 3: Cập nhật tài liệu**

Trong `CLAUDE.md`, mục "Database schema", thêm `PermissionsJson` vào mô tả `dbo.TkSessions`; ghi chú action tools + trang tích hợp nay gate theo quyền TourKit. (Docs người dùng: tùy chọn, dùng agent `tourkit-doc-writer` sau.)

```bash
git add CLAUDE.md
git commit -m "docs: ghi chú permission gating + cột PermissionsJson"
```

- [ ] **Step 4: GitNexus detect_changes + re-index**

Chạy `detect_changes({scope:"compare", base_ref:"main"})` xác nhận blast-radius đúng kỳ vọng, rồi `node .gitnexus/run.cjs analyze` để cập nhật index.

---

## Self-Review

**Spec coverage:**
- Lấy quyền từ hệ thống → Task 1 (fetch) + Task 3 (lưu/persist) + Task 4 (expose).
- Chặn giao việc/lịch hẹn khi thiếu quyền, **chỉ lúc execute** → Task 6 (gate trong `ExecuteCrmQueueAsync`, KHÔNG đụng planner/proposal). ✅ khớp lựa chọn "Chỉ lúc execute".
- `CH_HT_THAOTAC` điều khiển `/widget-admin`, `/visa-config`, `/workflows` → Task 9 (box) + Task 10 (2 trang gate cứng) + Task 11 (`/workflows` vẫn hiện nhưng chỉ scope người dùng) + Task 7/8 (enforce server-side). ✅
- Service account cảnh báo khi thiếu quyền → Task 12. ✅

**Placeholder scan:** không có TODO/TBD; mọi step code cụ thể.

**Type consistency:**
- `LoginTokenResponse` thêm `Permissions`/`CanConfigSystem` (Task 4) — client đọc `permissions`/`canConfigSystem` camelCase (Task 5), khớp `JsonSerializerDefaults.Web` của `Results.Json`.
- `TkPermissionCodes.TaoViec/TaoNhacHen/CauHinhHeThong` dùng nhất quán ở Task 6/7/12.
- `HasPermission(sessionId, code)` — chữ ký giống nhau ở ActionExecutor (Task 6) + WorkflowEndpoints (Task 7 qua `CanConfigSystem`) + Task 8.
- `ListForMonitorAsync` thêm tham số `username` optional (default null) → caller cũ (nếu có) không vỡ; caller mới truyền `scopeUser`.

**Rủi ro cần lưu ý khi execute:**
- **Thứ tự tham số constructor `ActionExecutor`** (Task 6 Step 1b) — đặt `TkSessionStore sessions` đúng vị trí đã ghi, tránh lệch DI.
- **Quyết định đã chốt: FAIL-CLOSED + tự lấy lại.** Rỗng = coi như không có quyền (chặn). NHƯNG phân biệt rõ 2 loại rỗng: (a) HTTP 200 trả `[]` = user thật sự không quyền → `PermissionsLoaded=true`, authoritative; (b) lỗi mạng/5xx/timeout sau retry = `GetPermissionsAsync` trả `null` → `PermissionsLoaded=false` → `EnsurePermissionsAsync` tự lấy LẠI ở mọi điểm gate (/session, action-execute, workflow) cho tới khi thành công. Nhờ vậy 1 blip upstream KHÔNG khoá vĩnh viễn user, mà cũng không nới lỏng gate.
- **`GET /api/auth/permissions` phải tồn tại ở host `TourKit:BaseUrl`** (giống `/api/ai/*`). Nếu endpoint bị thiếu hẳn (404 kéo dài, không phải transient) → retry vẫn cạn → `null` → gate chặn liên tục + Ensure gọi lại mỗi request (tốn upstream). Xác nhận staging/prod có endpoint TRƯỚC khi bật tính năng. 404 "vĩnh viễn" hiện coi là transient (retry) — nếu muốn tránh spam, có thể mở rộng nhánh `catch (TourKitApiException ex) when (ex.Status == 404)` trả rỗng authoritative; cân nhắc khi vận hành.
```
