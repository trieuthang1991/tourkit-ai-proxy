# Phase 0 — Automation Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chuẩn hoá state layer (TkSessions, UsageTracker, Redis) để 2+ process (web + Hangfire worker) chia sẻ được state, làm nền cho automation các feature sau này.

**Architecture:**
- Bật Redis (mới chỉ là config) → `ChatCache` + `TenantQuotaStore` tự cross-process (cả 2 đã hook sẵn).
- Migrate `TkSessionStore` từ file `data/tk-sessions.json` → bảng SQL `dbo.TkSessions` (giữ in-mem cache cho hot path `Get`).
- Migrate `UsageTracker` từ counter in-mem → bảng SQL `dbo.AiUsageCounters` (daily aggregate); endpoint `/api/v1/usage` đọc từ DB.
- API surface 3 service (`TkSessionStore`, `UsageTracker`, callers) GIỮ NGUYÊN — chỉ thay internal storage. Không breaking change cho endpoint/frontend.

**Tech Stack:** ASP.NET Core 8, Dapper, SQL Server (`PushDb` connection, đã sẵn), StackExchange.Redis (đã sẵn), xUnit cho test pure logic.

**Non-goals (Phase 1+):**
- Cài Hangfire (sẽ làm sau khi foundation xong).
- Migrate `data/customers.seed.json` → live CRM fetch (separate plan).
- Tách worker process (codebase sẵn sàng nhưng chưa deploy).
- `TenantServiceAccounts` cho cron tenant-wide (Phase 2).

---

## File Structure

**New files:**
- `Services/TourKit/TkSessionRepository.cs` — Dapper CRUD `dbo.TkSessions`, encrypt/decrypt mật khẩu qua Crypton, serialize `SessionChatMemory` vào cột `ChatMemoryJson`.
- `Services/UsageRepository.cs` — Dapper aggregate `dbo.AiUsageCounters`, UPSERT theo `(Date, Model)`, Snapshot trả về object cùng shape với endpoint hiện tại.
- `TourkitAiProxy.Tests/UsageRepositoryFormatTests.cs` — unit test pure-logic format Snapshot output (không đụng DB).

**Modified files:**
- `Services/Db/TourkitAiDb.cs` — thêm 2 block `CREATE TABLE IF NOT EXISTS` (`TkSessions`, `AiUsageCounters`) vào `SchemaSql`.
- `Services/TourKit/TkSessionStore.cs` — rewrite internal: bỏ file Load/Persist, dùng `TkSessionRepository`. Giữ `ConcurrentDictionary` làm hot cache + load lazy từ SQL khi miss.
- `Services/UsageTracker.cs` — rewrite: `Track` → repo.AppendAsync + write-through in-mem; `Snapshot` → repo.SnapshotAsync (fallback in-mem nếu DB fail).
- `Program.cs` — đăng ký `TkSessionRepository`, `UsageRepository`; gọi `InitAsync` cho session/usage migrate file→DB lần đầu.
- `appsettings.example.json` — comment hướng dẫn điền `Redis:ConnectionString` (copy từ TourKit.Api ENC: được).

**Files PHẢI KHÔNG đụng:**
- Endpoint files (`AiEndpoints`, `ChatEndpoints`, `MailEndpoints`...): API surface giữ nguyên.
- `MailAccountStore`, `TenantQuotaStore`, `ChatCache`, `RedisStore`: đã share cross-process sẵn.
- Frontend: không thay đổi.

---

## Task 1: Verify Redis config (no code)

**Files:** `appsettings.json` (user-local, gitignored), `appsettings.example.json`.

- [ ] **Step 1: Đọc Redis config hiện tại**

```bash
grep -A2 '"Redis"' appsettings.json
```

Expected: hoặc `"ConnectionString": ""` (chưa cấu hình) hoặc `"ConnectionString": "ENC:..."` (đã có).

- [ ] **Step 2: Nếu rỗng → user copy từ TourKit.Api**

Vào TourKit.Api `appsettings.json`, copy nguyên `"Redis": { "ConnectionString": "ENC:..." }` vào `appsettings.json` của project này (cùng instance Redis, key prefix `tkai:` đã tách).

Nếu không có Redis sẵn → mục này SKIP (vẫn chạy được, nhưng quota/cache sẽ KHÔNG share cross-process — không đáp ứng được Phase 1 automation).

- [ ] **Step 3: Update appsettings.example.json comment**

Edit file `appsettings.example.json`, đổi `_comment` của `Redis` thành:

```json
"Redis": {
  "_comment": "BẮT BUỘC cho automation (cross-process state cho quota + cache + worker). Copy ConnectionString (ENC: cũng được) từ TourKit.Api. Để rỗng = chỉ chạy 1 process (web only).",
  "ConnectionString": ""
}
```

- [ ] **Step 4: Restart app → verify log "Redis (...)"**

```bash
dotnet run --project TourkitAiProxy.csproj
```

Trong log startup tìm 2 dòng:
- `RedisStore backend: Redis (...)` (KHÔNG phải "disabled")
- `TenantQuotaStore backend: File + Redis (...)` (KHÔNG phải "File only")

Nếu thấy "disabled" hoặc "Redis fail" → debug connection string trước khi đi tiếp.

- [ ] **Step 5: Commit appsettings.example.json**

```bash
git add appsettings.example.json
git commit -m "docs(config): note Redis bắt buộc cho automation cross-process"
```

---

## Task 2: Thêm bảng dbo.TkSessions vào schema

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs` (thêm block CREATE TABLE)

- [ ] **Step 1: Đọc cuối file để biết format hiện tại**

```bash
wc -l Services/Db/TourkitAiDb.cs
```

Tìm cuối block `SchemaSql` (ngay trước `";`).

- [ ] **Step 2: Thêm block tạo bảng TkSessions trước dấu `";"` đóng `SchemaSql`**

Chèn đoạn SQL này VÀO CUỐI `SchemaSql` (trước dấu `";"`):

```sql

IF OBJECT_ID('dbo.TkSessions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TkSessions (
        Id                NVARCHAR(64)    NOT NULL,
        TenantId          NVARCHAR(128)   NOT NULL,
        Username          NVARCHAR(128)   NOT NULL,
        PasswordEnc       NVARCHAR(512)   NOT NULL,
        FullName          NVARCHAR(256)   NULL,
        CompanyName       NVARCHAR(256)   NULL,
        ChatMemoryJson    NVARCHAR(MAX)   NULL,
        LastUsedUtc       DATETIME2       NOT NULL,
        CreatedUtc        DATETIME2       NOT NULL CONSTRAINT DF_TkSessions_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TkSessions PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_TkSessions_TenantUser ON dbo.TkSessions(TenantId, Username);
    CREATE INDEX IX_TkSessions_LastUsed   ON dbo.TkSessions(LastUsedUtc);
END;
```

**Lưu ý:** KHÔNG lưu JWT (re-login khi cần). KHÔNG lưu password plaintext (Crypton encrypt).

- [ ] **Step 3: Update log message trong `InitAsync`**

Tìm dòng:
```csharp
_log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory/MailAccounts/Mails/MailSyncState/VisaAssessments/TourQuotes đã có/đã tạo)");
```

Đổi thành:
```csharp
_log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory/MailAccounts/Mails/MailSyncState/VisaAssessments/TourQuotes/TkSessions/AiUsageCounters đã có/đã tạo)");
```

(Lưu ý `AiUsageCounters` sẽ thêm ở Task 7 — viết trước để khỏi sửa lại.)

- [ ] **Step 4: Build → verify schema không có lỗi syntax**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors.

- [ ] **Step 5: Chạy app → verify bảng được tạo**

```bash
dotnet run --project TourkitAiProxy.csproj
```

Mở SQL Server Management Studio (hoặc Azure Data Studio), connect vào `PushDb`, chạy:
```sql
SELECT name FROM sys.tables WHERE name = 'TkSessions';
SELECT name, type_desc FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TkSessions');
```

Expected: 1 row `TkSessions`, 3 indexes (`PK_TkSessions`, `IX_TkSessions_TenantUser`, `IX_TkSessions_LastUsed`).

Kill app sau khi verify (`Ctrl+C`).

- [ ] **Step 6: Commit schema-only change**

```bash
git add Services/Db/TourkitAiDb.cs
git commit -m "feat(db): thêm bảng dbo.TkSessions cho persist phiên TourKit cross-process"
```

---

## Task 3: Tạo TkSessionRepository (Dapper CRUD)

**Files:**
- Create: `Services/TourKit/TkSessionRepository.cs`

- [ ] **Step 1: Tạo file mới với CRUD đầy đủ**

```csharp
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// Dapper repo cho dbo.TkSessions. Mật khẩu Crypton-encrypted; JWT KHÔNG persist (re-login khi cần).
/// SessionChatMemory serialize vào cột ChatMemoryJson (nullable).
///
/// CHỈ làm CRUD thuần — không cache, không retry. Caller (TkSessionStore) lo cache + side-effect.
/// </summary>
public class TkSessionRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TkSessionRepository> _log;

    private static readonly JsonSerializerOptions _jsonOpts =
        new(JsonSerializerDefaults.Web);

    public TkSessionRepository(TourkitAiDb db, ILogger<TkSessionRepository> log)
    {
        _db = db; _log = log;
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string PasswordEnc { get; set; } = "";
        public string? FullName { get; set; }
        public string? CompanyName { get; set; }
        public string? ChatMemoryJson { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    /// Load TẤT CẢ session chưa idle expire (caller pass cutoff = UtcNow - IdleTtl).
    /// Trả về list TkSession (JWT rỗng, ép re-login lần dùng đầu).
    public async Task<List<TkSession>> ListActiveAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<Row>(
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                "FROM dbo.TkSessions WHERE LastUsedUtc >= @cut",
                new { cut = cutoffUtc });
            var list = new List<TkSession>();
            foreach (var r in rows)
            {
                var s = TryHydrate(r);
                if (s != null) list.Add(s);
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] ListActive lỗi");
            return new List<TkSession>();
        }
    }

    /// Lookup 1 session by id (nullable). Dùng khi cache miss.
    public async Task<TkSession?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var row = await c.QueryFirstOrDefaultAsync<Row>(
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                "FROM dbo.TkSessions WHERE Id = @id",
                new { id });
            return row == null ? null : TryHydrate(row);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] Get {Id} lỗi", id);
            return null;
        }
    }

    /// UPSERT session. Crypton-encrypt password, serialize ChatMemory. Không lưu JWT.
    public async Task UpsertAsync(TkSession s, CancellationToken ct = default)
    {
        var pwdEnc = Crypton.Encrypt(s.Password);
        var memJson = s.ChatMemory == null ? null : JsonSerializer.Serialize(s.ChatMemory, _jsonOpts);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
MERGE dbo.TkSessions AS T
USING (SELECT @Id AS Id) AS S ON T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    TenantId       = @TenantId,
    Username       = @Username,
    PasswordEnc    = @PasswordEnc,
    FullName       = @FullName,
    CompanyName    = @CompanyName,
    ChatMemoryJson = @ChatMemoryJson,
    LastUsedUtc    = @LastUsedUtc
WHEN NOT MATCHED THEN INSERT
    (Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc)
VALUES
    (@Id, @TenantId, @Username, @PasswordEnc, @FullName, @CompanyName, @ChatMemoryJson, @LastUsedUtc);",
                new {
                    s.Id, s.TenantId, s.Username,
                    PasswordEnc    = pwdEnc,
                    s.FullName, s.CompanyName,
                    ChatMemoryJson = memJson,
                    LastUsedUtc    = s.LastUsed
                });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TkSessionRepo] Upsert {Id} lỗi", s.Id);
            throw;   // upsert fail → caller (TkSessionStore) phải biết để rollback in-mem
        }
    }

    /// Xoá tất cả session idle quá cutoff. Trả số rows xoá.
    public async Task<int> PruneIdleAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.ExecuteAsync(
                "DELETE FROM dbo.TkSessions WHERE LastUsedUtc < @cut",
                new { cut = cutoffUtc });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] PruneIdle lỗi");
            return 0;
        }
    }

    private TkSession? TryHydrate(Row r)
    {
        var pwd = Crypton.Decrypt(r.PasswordEnc);
        if (string.IsNullOrEmpty(pwd))
        {
            _log.LogWarning("[TkSessionRepo] Session {Id} decrypt fail — skip", r.Id);
            return null;
        }
        SessionChatMemory? mem = null;
        if (!string.IsNullOrWhiteSpace(r.ChatMemoryJson))
        {
            try { mem = JsonSerializer.Deserialize<SessionChatMemory>(r.ChatMemoryJson, _jsonOpts); }
            catch (Exception ex) { _log.LogWarning(ex, "[TkSessionRepo] ChatMemory parse fail {Id}", r.Id); }
        }
        return new TkSession
        {
            Id = r.Id, TenantId = r.TenantId, Username = r.Username, Password = pwd,
            FullName = r.FullName, CompanyName = r.CompanyName,
            Jwt = "", JwtExpiresAt = DateTime.MinValue,   // ép re-login lần dùng đầu
            LastUsed = r.LastUsedUtc,
            ChatMemory = mem ?? SessionChatMemory.Empty()
        };
    }
}
```

- [ ] **Step 2: Build → verify compile clean**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors. Repo chưa được wire vào DI nên không ảnh hưởng runtime.

- [ ] **Step 3: Commit repo**

```bash
git add Services/TourKit/TkSessionRepository.cs
git commit -m "feat(tourkit): TkSessionRepository — Dapper CRUD cho dbo.TkSessions"
```

---

## Task 4: Đăng ký TkSessionRepository vào DI

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Tìm dòng `AddSingleton<TkSessionStore>` để chèn ngay TRƯỚC**

```bash
grep -n "TkSessionStore" Program.cs
```

Expected: dòng `builder.Services.AddSingleton<TkSessionStore>();`

- [ ] **Step 2: Thêm registration cho repo**

Edit `Program.cs`, ngay TRƯỚC dòng `builder.Services.AddSingleton<TkSessionStore>();`, thêm:

```csharp
builder.Services.AddSingleton<TkSessionRepository>();
```

- [ ] **Step 3: Build → verify DI resolve được**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "wire(di): đăng ký TkSessionRepository"
```

---

## Task 5: Rewire TkSessionStore dùng SQL repo + giữ in-mem cache

**Files:**
- Modify: `Services/TourKit/TkSessionStore.cs` (rewrite internals — API public giữ nguyên 100%)

- [ ] **Step 1: Rewrite toàn bộ file**

Replace toàn bộ nội dung `Services/TourKit/TkSessionStore.cs` bằng:

```csharp
using System.Collections.Concurrent;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Services.TourKit;

/// 1 phiên đăng nhập TourKit. Giữ credentials đã giải mã (in-memory) để tự re-login khi JWT hết hạn.
public class TkSession
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }

    public string Jwt { get; set; } = "";
    public string? FullName { get; set; }
    public string? CompanyName { get; set; }
    public DateTime JwtExpiresAt { get; set; }   // soft TTL — re-login khi quá hạn
    public DateTime LastUsed { get; set; }

    // BỘ NHỚ CHAT — persist cùng session xuống SQL (cột ChatMemoryJson).
    public SessionChatMemory ChatMemory { get; set; } = SessionChatMemory.Empty();
}

/// <summary>
/// Lưu phiên TourKit. JWT KHÔNG ra client; client chỉ giữ sessionId. Tự re-login bằng credentials
/// trong phiên khi JWT soft-expire hoặc 401.
///
/// Persistence: SQL `dbo.TkSessions` (mật khẩu Crypton-encrypted, JWT KHÔNG lưu).
/// Cache: in-mem `ConcurrentDictionary` cho hot path Get; Get cache-miss → load từ SQL.
/// Cross-process: 2 instance cùng SQL share state; write-through đảm bảo nhất quán.
/// </summary>
public class TkSessionStore
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionRepository _repo;
    private readonly ILogger<TkSessionStore> _log;
    private readonly ConcurrentDictionary<string, TkSession> _cache = new();

    // JWT TourKit sống vài giờ; refresh chủ động sau 50 phút cho an toàn (re-login rẻ).
    private static readonly TimeSpan SoftTtl = TimeSpan.FromMinutes(50);
    // Dọn phiên không dùng quá 30 ngày.
    private static readonly TimeSpan IdleTtl = TimeSpan.FromDays(30);

    public TkSessionStore(TourKitApiClient api, TkSessionRepository repo, ILogger<TkSessionStore> log)
    {
        _api = api; _repo = repo; _log = log;
        LoadActiveFromSql();
    }

    /// Khởi động: load mọi session chưa idle expire vào cache.
    /// Nếu DB lỗi → cache rỗng, Get sẽ thử lại từ SQL khi cần.
    private void LoadActiveFromSql()
    {
        try
        {
            var list = _repo.ListActiveAsync(DateTime.UtcNow - IdleTtl).GetAwaiter().GetResult();
            foreach (var s in list) _cache[s.Id] = s;
            _log.LogInformation("Loaded {N} TourKit sessions từ SQL vào cache", list.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Load sessions từ SQL fail — cache rỗng, sẽ lazy-load khi Get");
        }
    }

    /// Login lần đầu từ credentials đã giải mã → tạo phiên, persist SQL, trả về phiên.
    public async Task<TkSession> CreateAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        _ = PruneIdleAsync(ct);   // fire-and-forget, không block login
        var login = await _api.LoginAsync(tenantId, username, password, ct);

        var session = new TkSession
        {
            Id          = Guid.NewGuid().ToString("N"),
            TenantId    = tenantId,
            Username    = username,
            Password    = password,
            Jwt         = login.Token,
            FullName    = login.FullName,
            CompanyName = login.CompanyName,
            JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl),
            LastUsed    = DateTime.UtcNow
        };
        _cache[session.Id] = session;
        await _repo.UpsertAsync(session, ct);
        _log.LogInformation("TourKit session {Id} tạo cho tenant={Tenant} user={User}", session.Id, tenantId, username);
        return session;
    }

    /// Get cache trước; cache miss → load SQL (đồng bộ — chỉ xảy ra lần đầu hoặc sau restart).
    public TkSession? Get(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (_cache.TryGetValue(sessionId, out var s)) return s;
        // Cache miss → SQL fallback (đồng bộ, chỉ 1 lần / session / process)
        var fromDb = _repo.GetAsync(sessionId).GetAwaiter().GetResult();
        if (fromDb != null) _cache[sessionId] = fromDb;
        return fromDb;
    }

    /// JWT còn hạn (soft TTL); tự re-login nếu hết. Throw nếu phiên không tồn tại.
    public async Task<string> GetValidJwtAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        s.LastUsed = DateTime.UtcNow;
        if (string.IsNullOrEmpty(s.Jwt) || DateTime.UtcNow >= s.JwtExpiresAt)
            await ReloginAsync(s, ct);
        else
            // chỉ update LastUsed → write-through cho cross-process biết session đang active
            await _repo.UpsertAsync(s, ct);
        return s.Jwt;
    }

    /// Buộc re-login (gọi khi TourKit trả 401 giữa chừng).
    public async Task<string> ForceReloginAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        await ReloginAsync(s, ct);
        return s.Jwt;
    }

    private async Task ReloginAsync(TkSession s, CancellationToken ct)
    {
        var login = await _api.LoginAsync(s.TenantId, s.Username, s.Password, ct);
        s.Jwt = login.Token;
        s.FullName = login.FullName;
        s.CompanyName = login.CompanyName;
        s.JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl);
        s.LastUsed = DateTime.UtcNow;
        await _repo.UpsertAsync(s, ct);
        _log.LogInformation("TourKit session {Id} re-login (JWT refreshed)", s.Id);
    }

    private async Task PruneIdleAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow - IdleTtl;
            // Drop in-mem cache đồng thời
            foreach (var kv in _cache)
                if (kv.Value.LastUsed < cutoff) _cache.TryRemove(kv.Key, out _);
            var removed = await _repo.PruneIdleAsync(cutoff, ct);
            if (removed > 0)
                _log.LogInformation("[TkSessionStore] Pruned {N} idle sessions", removed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionStore] PruneIdle lỗi");
        }
    }

    // ─── Chat memory helpers ────────────────────────────────────────────────────

    /// Lấy bộ nhớ chat của phiên. Trả null nếu không tìm thấy phiên.
    public SessionChatMemory? GetMemory(string sessionId) => Get(sessionId)?.ChatMemory;

    /// Cập nhật bộ nhớ chat, tự gán LastUpdated = UtcNow, write-through SQL.
    public void UpdateMemory(string sessionId, SessionChatMemory memory)
    {
        var s = Get(sessionId);
        if (s == null) return;
        s.ChatMemory = memory with { LastUpdated = DateTime.UtcNow };
        s.LastUsed = DateTime.UtcNow;
        // Fire-and-forget: chat memory update là hot path, không block agent loop
        _ = _repo.UpsertAsync(s);
    }

    /// Xóa bộ nhớ chat về Empty (khi user yêu cầu reset hội thoại).
    public void ClearMemory(string sessionId)
    {
        var s = Get(sessionId);
        if (s == null) return;
        s.ChatMemory = SessionChatMemory.Empty();
        s.LastUsed = DateTime.UtcNow;
        _ = _repo.UpsertAsync(s);
    }
}
```

**Lưu ý quan trọng:**
- Bỏ `IWebHostEnvironment env` khỏi constructor (không cần file path nữa).
- Bỏ `_path`, `_ioLock`, `Load()`, `Persist()`, record `Persisted`.
- Tất cả write đều write-through SQL (qua repo).
- `Get` đồng bộ (block) khi cache miss — chấp nhận vì chỉ xảy ra 1 lần / session / process.

- [ ] **Step 2: Build → verify compile clean**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors. Nếu compile lỗi → kiểm tra `using` đã đủ (`System.Collections.Concurrent`, `TourkitAiProxy.Services.Chat`) và `TkSessionRepository` đã đăng ký DI ở Task 4.

- [ ] **Step 3: Commit rewrite**

```bash
git add Services/TourKit/TkSessionStore.cs
git commit -m "refactor(tourkit): TkSessionStore dùng SQL repo + in-mem cache (bỏ file persist)"
```

---

## Task 6: Migration script — import file legacy → SQL (one-shot)

**Files:**
- Modify: `Program.cs` (gọi migration script ở startup, idempotent)

- [ ] **Step 1: Thêm hàm migration vào `TkSessionStore.cs`**

Edit `Services/TourKit/TkSessionStore.cs`, thêm method PUBLIC mới ngay TRƯỚC dòng `// ─── Chat memory helpers ──`:

```csharp
/// One-shot migration: đọc file legacy `data/tk-sessions.json` (nếu còn) → import vào SQL.
/// Chạy ở startup. Sau khi import xong → rename file thành `.migrated` để khỏi chạy lại.
/// Idempotent: nếu file không tồn tại HOẶC đã rename → no-op.
public async Task MigrateFromLegacyFileAsync(string dataDir, CancellationToken ct = default)
{
    var path = Path.Combine(dataDir, "tk-sessions.json");
    if (!File.Exists(path)) return;

    try
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        // Persisted shape cũ — chỉ giữ field cần thiết
        var legacyList = System.Text.Json.JsonSerializer.Deserialize<List<LegacyPersisted>>(json, opts) ?? new();
        int ok = 0, skip = 0;
        foreach (var p in legacyList)
        {
            var pwd = TourkitAiProxy.Services.Security.Crypton.Decrypt(p.EncPassword);
            if (string.IsNullOrEmpty(pwd)) { skip++; continue; }
            var existing = await _repo.GetAsync(p.Id, ct);
            if (existing != null) { skip++; continue; }   // đã có trong SQL, skip
            var s = new TkSession
            {
                Id = p.Id, TenantId = p.TenantId, Username = p.Username, Password = pwd,
                FullName = p.FullName, CompanyName = p.CompanyName,
                Jwt = "", JwtExpiresAt = DateTime.MinValue,
                LastUsed = DateTime.TryParse(p.LastUsedIso, out var d) ? d.ToUniversalTime() : DateTime.UtcNow,
                ChatMemory = p.ChatMemory ?? SessionChatMemory.Empty()
            };
            await _repo.UpsertAsync(s, ct);
            _cache[s.Id] = s;
            ok++;
        }
        // Rename file để khỏi chạy lại lần sau
        File.Move(path, path + ".migrated", overwrite: true);
        _log.LogInformation("[TkSessionStore] Migrated {Ok} sessions từ file legacy (skip {Skip}), file → .migrated", ok, skip);
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[TkSessionStore] Migrate file legacy lỗi — giữ file nguyên để retry");
    }
}

private sealed record LegacyPersisted(
    string Id, string TenantId, string Username, string EncPassword,
    string? FullName, string? CompanyName, string LastUsedIso,
    SessionChatMemory? ChatMemory = null);
```

- [ ] **Step 2: Gọi migration ở `Program.cs` startup**

Tìm trong `Program.cs` block:
```csharp
TourkitAiProxy.Services.Db.MultiTenantMigration.Run(
    Path.Combine(app.Environment.ContentRootPath, "data"),
    app.Services.GetRequiredService<ILogger<Program>>());
```

NGAY SAU block đó, thêm:
```csharp
// One-shot migrate tk-sessions.json → SQL (idempotent: file → .migrated sau khi xong)
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Services.TourKit.TkSessionStore>();
        await store.MigrateFromLegacyFileAsync(
            Path.Combine(app.Environment.ContentRootPath, "data"));
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "Migrate tk-sessions file → SQL fail");
    }
});
```

- [ ] **Step 3: Build → verify clean**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Run app với file legacy tồn tại → verify migrate**

Trước khi chạy, check file legacy có không:
```bash
ls -la data/tk-sessions.json 2>/dev/null
```

Chạy app:
```bash
dotnet run --project TourkitAiProxy.csproj
```

Trong log tìm: `[TkSessionStore] Migrated N sessions từ file legacy (skip 0), file → .migrated`

Verify SQL:
```sql
SELECT COUNT(*) FROM dbo.TkSessions;
SELECT TOP 5 Id, TenantId, Username, FullName, LastUsedUtc FROM dbo.TkSessions ORDER BY LastUsedUtc DESC;
```

Expected: số rows khớp với số session cũ trong file. File đổi thành `tk-sessions.json.migrated`.

Verify file đã rename:
```bash
ls -la data/tk-sessions.json* 2>/dev/null
```

Expected: chỉ thấy `tk-sessions.json.migrated`, KHÔNG có `tk-sessions.json` nữa.

Kill app (`Ctrl+C`).

- [ ] **Step 5: Restart app → verify no-op lần 2 (idempotent)**

```bash
dotnet run --project TourkitAiProxy.csproj
```

Log KHÔNG được có dòng "Migrated N sessions" lần này (vì file đã rename).
Log PHẢI có dòng `Loaded N TourKit sessions từ SQL vào cache` (N = số session migrate ở lần 1).

Kill app.

- [ ] **Step 6: Smoke test chat-analytics**

Mở trình duyệt vào `http://localhost:5080/assistant`, đăng nhập (nếu chưa). Hỏi 1 câu test: "doanh thu hôm nay".

Expected: chat hoạt động bình thường, KHÔNG bị bắt login lại (vì session cũ đã migrate sang SQL).

- [ ] **Step 7: Commit migration**

```bash
git add Services/TourKit/TkSessionStore.cs Program.cs
git commit -m "feat(tourkit): one-shot migrate tk-sessions.json → SQL ở startup (idempotent)"
```

---

## Task 7: Thêm bảng dbo.AiUsageCounters vào schema

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs`

- [ ] **Step 1: Thêm block tạo bảng AiUsageCounters vào cuối `SchemaSql`**

Chèn ngay TRƯỚC dấu `";"` đóng `SchemaSql`:

```sql

IF OBJECT_ID('dbo.AiUsageCounters', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiUsageCounters (
        DateUtc        DATE          NOT NULL,
        Model          NVARCHAR(128) NOT NULL,
        Calls          BIGINT        NOT NULL CONSTRAINT DF_AiUsage_Calls   DEFAULT 0,
        InTokens       BIGINT        NOT NULL CONSTRAINT DF_AiUsage_InTok   DEFAULT 0,
        OutTokens      BIGINT        NOT NULL CONSTRAINT DF_AiUsage_OutTok  DEFAULT 0,
        TotalLatencyMs BIGINT        NOT NULL CONSTRAINT DF_AiUsage_Lat     DEFAULT 0,
        UpdatedUtc     DATETIME2     NOT NULL CONSTRAINT DF_AiUsage_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_AiUsageCounters PRIMARY KEY CLUSTERED (DateUtc, Model)
    );
    CREATE INDEX IX_AiUsage_Date ON dbo.AiUsageCounters(DateUtc DESC);
END;
```

**Lưu ý:** Aggregate per-day per-model để Snapshot rẻ (1 SELECT WHERE DateUtc trong N ngày gần đây, không cần scan tất cả call).

- [ ] **Step 2: Build → verify schema syntax**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Chạy app → verify bảng tạo**

```bash
dotnet run --project TourkitAiProxy.csproj
```

SQL:
```sql
SELECT name FROM sys.tables WHERE name = 'AiUsageCounters';
```

Expected: 1 row.

Kill app.

- [ ] **Step 4: Commit**

```bash
git add Services/Db/TourkitAiDb.cs
git commit -m "feat(db): thêm bảng dbo.AiUsageCounters cho counter cross-process"
```

---

## Task 8: Tạo UsageRepository (Dapper aggregate)

**Files:**
- Create: `Services/UsageRepository.cs`

- [ ] **Step 1: Tạo file mới**

```csharp
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services;

/// <summary>
/// Dapper repo cho dbo.AiUsageCounters. Aggregate daily per-model — Snapshot rẻ.
/// AppendAsync: UPSERT (date, model) +1 call, +inTok, +outTok, +latency.
/// SnapshotAsync: SELECT tổng tất cả ngày → object cùng shape với endpoint /api/v1/usage cũ.
///
/// Tách thành class riêng để UsageTracker chỉ orchestrate (cache + delegate).
/// Không cache trong repo — UsageTracker giữ in-mem snapshot riêng.
/// </summary>
public class UsageRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<UsageRepository> _log;

    public UsageRepository(TourkitAiDb db, ILogger<UsageRepository> log)
    {
        _db = db; _log = log;
    }

    /// UPSERT 1 call vào counter của ngày hôm nay × model.
    public async Task AppendAsync(string model, int inTok, int outTok, long ms, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model)) model = "unknown";
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
MERGE dbo.AiUsageCounters AS T
USING (SELECT CAST(SYSUTCDATETIME() AS DATE) AS DateUtc, @Model AS Model) AS S
   ON T.DateUtc = S.DateUtc AND T.Model = S.Model
WHEN MATCHED THEN UPDATE SET
    Calls          = T.Calls + 1,
    InTokens       = T.InTokens + @In,
    OutTokens      = T.OutTokens + @Out,
    TotalLatencyMs = T.TotalLatencyMs + @Ms,
    UpdatedUtc     = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (DateUtc, Model, Calls, InTokens, OutTokens, TotalLatencyMs, UpdatedUtc)
VALUES
    (S.DateUtc, S.Model, 1, @In, @Out, @Ms, SYSUTCDATETIME());",
                new { Model = model, In = (long)inTok, Out = (long)outTok, Ms = ms });
        }
        catch (Exception ex)
        {
            // Không throw — usage tracking là phụ, không được làm fail AI call
            _log.LogWarning(ex, "[UsageRepo] Append model={Model} lỗi", model);
        }
    }

    public sealed record CounterRow(string Model, long Calls, long InTokens, long OutTokens, long TotalLatencyMs);

    /// Trả về list rows aggregate trên N ngày gần đây (default 30).
    /// UsageTracker dùng để build Snapshot.
    public async Task<List<CounterRow>> ReadAggregateAsync(int daysBack = 30, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<CounterRow>(@"
SELECT Model,
       SUM(Calls)          AS Calls,
       SUM(InTokens)       AS InTokens,
       SUM(OutTokens)      AS OutTokens,
       SUM(TotalLatencyMs) AS TotalLatencyMs
FROM dbo.AiUsageCounters
WHERE DateUtc >= DATEADD(DAY, -@d, CAST(SYSUTCDATETIME() AS DATE))
GROUP BY Model
ORDER BY Calls DESC;",
                new { d = daysBack });
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[UsageRepo] ReadAggregate lỗi");
            return new List<CounterRow>();
        }
    }
}
```

- [ ] **Step 2: Đăng ký DI ở `Program.cs`**

Tìm dòng `builder.Services.AddSingleton<UsageTracker>();`. NGAY TRƯỚC dòng đó, thêm:

```csharp
builder.Services.AddSingleton<UsageRepository>();
```

- [ ] **Step 3: Build → verify clean**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Services/UsageRepository.cs Program.cs
git commit -m "feat(usage): UsageRepository — daily counter SQL cho cross-process aggregation"
```

---

## Task 9: Test format Snapshot output (pure logic)

**Files:**
- Create: `TourkitAiProxy.Tests/UsageSnapshotFormatTests.cs`

- [ ] **Step 1: Đọc test project pattern hiện tại**

```bash
ls TourkitAiProxy.Tests/*.cs
```

Pick 1 file existing (vd `MailTaxonomyTests.cs`) để xem pattern xUnit + namespace.

- [ ] **Step 2: Viết test cho hàm format (sẽ tạo ở Task 10)**

Tạo file `TourkitAiProxy.Tests/UsageSnapshotFormatTests.cs`:

```csharp
using TourkitAiProxy.Services;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Test pure-logic format Snapshot từ list rows → object endpoint trả ra.
/// Không đụng DB.
public class UsageSnapshotFormatTests
{
    [Fact]
    public void Format_EmptyList_ReturnsZeroes()
    {
        var rows = new List<UsageRepository.CounterRow>();
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(0L, (long)snap.calls);
        Assert.Equal(0L, (long)snap.inputTokens);
        Assert.Equal(0L, (long)snap.outputTokens);
        Assert.Equal(0L, (long)snap.avgLatencyMs);
    }

    [Fact]
    public void Format_SingleRow_AggregatesCorrectly()
    {
        var rows = new List<UsageRepository.CounterRow>
        {
            new("deepseek-v4-flash", Calls: 10, InTokens: 1000, OutTokens: 500, TotalLatencyMs: 5000)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(10L, (long)snap.calls);
        Assert.Equal(1000L, (long)snap.inputTokens);
        Assert.Equal(500L, (long)snap.outputTokens);
        Assert.Equal(500L, (long)snap.avgLatencyMs);    // 5000 / 10
    }

    [Fact]
    public void Format_MultipleModels_SumsTotalsAndKeepsByModel()
    {
        var rows = new List<UsageRepository.CounterRow>
        {
            new("model-a", Calls: 5, InTokens: 100, OutTokens: 50, TotalLatencyMs: 500),
            new("model-b", Calls: 3, InTokens: 60, OutTokens: 30, TotalLatencyMs: 600)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(8L, (long)snap.calls);
        Assert.Equal(160L, (long)snap.inputTokens);
        Assert.Equal(80L, (long)snap.outputTokens);
        Assert.Equal(137L, (long)snap.avgLatencyMs);    // 1100 / 8 = 137.5 → 137 (long truncates)
        var byModel = (Dictionary<string, long>)snap.byModel;
        Assert.Equal(5L, byModel["model-a"]);
        Assert.Equal(3L, byModel["model-b"]);
    }

    [Fact]
    public void Format_CostEstimate_UsesDeepseekPricing()
    {
        // Cost hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) — không chính xác model khác
        // nhưng đủ order-of-magnitude (giữ tương thích endpoint cũ).
        var rows = new List<UsageRepository.CounterRow>
        {
            new("any-model", Calls: 1, InTokens: 1_000_000, OutTokens: 1_000_000, TotalLatencyMs: 100)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(1.37, (double)snap.estimatedCostUsd, precision: 2);   // 0.27 + 1.10
    }
}
```

- [ ] **Step 3: Run test → verify FAIL (vì hàm chưa tồn tại)**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~UsageSnapshotFormatTests"
```

Expected: BUILD FAIL với `'UsageTracker' does not contain a definition for 'FormatSnapshot'`.

Tốt — test đã ràng buộc API cần có ở Task 10.

- [ ] **Step 4: Commit failing test**

```bash
git add TourkitAiProxy.Tests/UsageSnapshotFormatTests.cs
git commit -m "test(usage): TDD format Snapshot — fail trước khi implement"
```

---

## Task 10: Rewire UsageTracker dùng SQL repo

**Files:**
- Modify: `Services/UsageTracker.cs` (rewrite — giữ API public Track/Snapshot)

- [ ] **Step 1: Rewrite toàn bộ file**

Replace toàn bộ nội dung `Services/UsageTracker.cs` bằng:

```csharp
using System.Collections.Concurrent;

namespace TourkitAiProxy.Services;

/// <summary>
/// AI usage tracker — daily counter SQL (`dbo.AiUsageCounters`) cho cross-process.
/// In-mem snapshot cache (TTL 10s) để endpoint /api/v1/usage không hit DB mỗi request.
/// Cost estimate hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) — giữ shape cũ.
///
/// Track(): fire-and-forget UPSERT vào SQL (KHÔNG block AI call).
/// Snapshot(): đọc cache nếu còn hạn; nếu không → load SQL → cache lại.
/// </summary>
public class UsageTracker
{
    private readonly UsageRepository _repo;
    private readonly ILogger<UsageTracker> _log;

    // Cache snapshot 10s — đủ tươi cho dashboard, đủ hiệu quả để khỏi nuốt DB
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly object _cacheLock = new();
    private object? _cachedSnapshot;
    private DateTime _cachedAt = DateTime.MinValue;

    public UsageTracker(UsageRepository repo, ILogger<UsageTracker> log)
    {
        _repo = repo; _log = log;
    }

    /// Append 1 call vào SQL counter. Fire-and-forget: không await trong AiEndpoints
    /// vì lỗi log usage KHÔNG được phép fail AI call.
    public void Track(string model, int inTok, int outTok, long ms)
    {
        _ = _repo.AppendAsync(model, inTok, outTok, ms);
        // Invalidate cache để snapshot tiếp theo load lại
        lock (_cacheLock) _cachedAt = DateTime.MinValue;
    }

    /// Snapshot tổng hợp 30 ngày gần nhất, format giống endpoint cũ.
    public object Snapshot()
    {
        lock (_cacheLock)
        {
            if (_cachedSnapshot != null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cachedSnapshot;
        }

        List<UsageRepository.CounterRow> rows;
        try
        {
            rows = _repo.ReadAggregateAsync(daysBack: 30).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[UsageTracker] Read SQL fail → trả snapshot rỗng");
            rows = new List<UsageRepository.CounterRow>();
        }

        var snap = FormatSnapshot(rows);
        lock (_cacheLock) { _cachedSnapshot = snap; _cachedAt = DateTime.UtcNow; }
        return snap;
    }

    /// Pure-logic format helper — tách ra để unit test (UsageSnapshotFormatTests).
    /// Giữ shape giống endpoint cũ: { calls, inputTokens, outputTokens, avgLatencyMs, estimatedCostUsd, byModel }.
    public static object FormatSnapshot(List<UsageRepository.CounterRow> rows)
    {
        long calls = 0, inTok = 0, outTok = 0, totMs = 0;
        var byModel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            calls  += r.Calls;
            inTok  += r.InTokens;
            outTok += r.OutTokens;
            totMs  += r.TotalLatencyMs;
            byModel[r.Model] = (byModel.TryGetValue(r.Model, out var v) ? v : 0) + r.Calls;
        }
        var costUsd = (inTok * 0.27 + outTok * 1.10) / 1_000_000.0;
        return new
        {
            calls            = calls,
            inputTokens      = inTok,
            outputTokens     = outTok,
            avgLatencyMs     = calls == 0 ? 0L : totMs / calls,
            estimatedCostUsd = Math.Round(costUsd, 4),
            byModel          = byModel
        };
    }
}
```

- [ ] **Step 2: Build → verify compile clean**

```bash
dotnet build TourkitAiProxy.csproj
```

Expected: 0 errors. Endpoint `AiEndpoints` không phải sửa — Track/Snapshot API y nguyên.

- [ ] **Step 3: Run test → verify PASS**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~UsageSnapshotFormatTests"
```

Expected: 4 PASS.

- [ ] **Step 4: Manual smoke /api/v1/usage**

```bash
dotnet run --project TourkitAiProxy.csproj
```

Mở trình duyệt: `http://localhost:5080/api/v1/usage`

Expected: JSON `{calls, inputTokens, outputTokens, avgLatencyMs, estimatedCostUsd, byModel}`. Lần đầu có thể `calls: 0` nếu chưa có AI call hôm nay.

Trigger 1 AI call (vào page `/assistant` hỏi 1 câu), refresh `/api/v1/usage` lại sau 10s → expect `calls` tăng, `byModel` có model vừa gọi.

SQL check:
```sql
SELECT * FROM dbo.AiUsageCounters ORDER BY DateUtc DESC, Calls DESC;
```

Expected: ≥1 row cho ngày hôm nay × model vừa dùng.

Kill app.

- [ ] **Step 5: Commit**

```bash
git add Services/UsageTracker.cs
git commit -m "refactor(usage): UsageTracker delegate SQL repo + cache 10s (bỏ in-mem counter)"
```

---

## Task 11: Cross-process verification (manual)

**Files:** không (test thực tế 2 process).

- [ ] **Step 1: Build Release**

```bash
dotnet publish TourkitAiProxy.csproj -c Release -o out
```

Expected: build thành công, output ở `out/`.

- [ ] **Step 2: Chạy 2 instance song song với port khác**

Terminal 1:
```bash
cd out
ASPNETCORE_URLS="http://localhost:5080" dotnet TourkitAiProxy.dll
```

Terminal 2 (NEW):
```bash
cd out
ASPNETCORE_URLS="http://localhost:5081" dotnet TourkitAiProxy.dll
```

Cả 2 đều phải connect SQL `PushDb` thành công (cùng appsettings.json).

- [ ] **Step 3: Test session share**

Terminal 3 (curl):
```bash
# Login tạo session ở instance A (port 5080)
curl -X POST http://localhost:5080/api/v1/login \
  -H "Content-Type: application/json" \
  -d '{"username":"<your-user>","password":"<your-pwd>","domain":"<your-tenant>"}'
```

Copy `sessionId` từ response. Giả sử là `abc123...`.

```bash
# Validate ở instance B (port 5081) — phải work (vì SQL share)
curl http://localhost:5081/api/v1/session -H "X-Session-Id: abc123..."
```

Expected: 200 OK với session info (KHÔNG phải 401). Đây là proof cross-process session sync.

- [ ] **Step 4: Test usage counter share**

```bash
# Call AI ở instance A
curl -X POST http://localhost:5080/api/v1/completions \
  -H "Content-Type: application/json" \
  -d '{"prompt":"say hi","maxTokens":50}'

# Đợi >10s để cache expire, đọc usage ở instance B
sleep 12
curl http://localhost:5081/api/v1/usage
```

Expected: `calls >= 1`, `byModel` có model vừa gọi ở A. Proof cross-process counter sync.

- [ ] **Step 5: Test quota share**

```bash
# Đọc quota ở B
curl http://localhost:5081/api/v1/quota -H "X-Session-Id: abc123..."
```

Expected: `used` tăng theo số call đã gọi ở A (vì Redis mirror đã làm sẵn).

Nếu thấy `used` không tăng → kiểm tra log Redis backend ở Task 1 step 4.

- [ ] **Step 6: Kill cả 2 instance**

`Ctrl+C` ở Terminal 1 + Terminal 2.

- [ ] **Step 7: Cập nhật CLAUDE.md ghi nhận trạng thái mới**

Edit `CLAUDE.md`, tìm section "Chat-Analytics feature" mục đoạn:
> Sessions persist to `data/tk-sessions.json` (password Crypton-encrypted, JWT NOT persisted)

Sửa thành:
> Sessions persist to SQL `dbo.TkSessions` (password Crypton-encrypted, JWT NOT persisted; in-mem cache cho hot path Get); legacy file `data/tk-sessions.json` được migrate one-shot ở startup → rename `.migrated`.

Tìm section "Usage tracking is in-memory only" — sửa thành:
> **Usage tracking trong SQL** `dbo.AiUsageCounters` (daily aggregate per-model). In-mem cache 10s ở `UsageTracker` cho /api/v1/usage. Cross-process: 2 instance cùng SQL → counter share tự động. Cost estimate vẫn hardcode DeepSeek V4 Pro retail.

- [ ] **Step 8: Commit verification + docs**

```bash
git add CLAUDE.md
git commit -m "docs(claude): cập nhật state layer (TkSessions+Usage qua SQL, cross-process verified)"
```

---

## Task 12: Cleanup + push branch

- [ ] **Step 1: Verify build sạch lần cuối**

```bash
dotnet build TourkitAiProxy.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```

Expected: 0 errors, all tests pass (kể cả test cũ).

- [ ] **Step 2: Verify git log gọn gàng**

```bash
git log --oneline main..HEAD
```

Expected ~10 commit, mỗi commit 1 việc rõ ràng:
- `docs(config): note Redis bắt buộc...`
- `feat(db): thêm bảng dbo.TkSessions...`
- `feat(tourkit): TkSessionRepository...`
- `wire(di): đăng ký TkSessionRepository`
- `refactor(tourkit): TkSessionStore dùng SQL repo...`
- `feat(tourkit): one-shot migrate tk-sessions.json → SQL...`
- `feat(db): thêm bảng dbo.AiUsageCounters...`
- `feat(usage): UsageRepository...`
- `test(usage): TDD format Snapshot...`
- `refactor(usage): UsageTracker delegate SQL repo...`
- `docs(claude): cập nhật state layer...`

- [ ] **Step 3: Push branch**

```bash
git push -u origin feat/automation-foundation
```

Expected: branch lên GitHub.

- [ ] **Step 4: Báo cáo trạng thái Phase 0 cho user**

Liệt kê:
- ✓ Redis enabled (nếu user đã cấu hình)
- ✓ TkSessions → SQL với in-mem cache, file legacy đã migrate
- ✓ UsageTracker → SQL daily counter, cache 10s
- ✓ Cross-process verified (2 instance cùng SQL share state)
- → Sẵn sàng cho Phase 1: cài Hangfire + viết job đầu tiên (mail auto-sync hoặc daily report)

---

## Self-Review Notes

**Spec coverage check:**
- ✓ Bật Redis → Task 1 + Task 11 step 5
- ✓ TkSessionStore → SQL → Task 2-6
- ✓ UsageTracker → SQL → Task 7-10
- ✓ Cross-process verify → Task 11
- ✓ Backward compat (endpoint shape, frontend không đổi) → Task 10 step 4 + Task 11 step 4

**Placeholder scan:** không có "TODO/TBD/implement later". Mọi code block đều đầy đủ.

**Type consistency:**
- `TkSession`, `SessionChatMemory`, `Crypton.Encrypt/Decrypt`: dùng nhất quán tên đã có trong codebase.
- `UsageRepository.CounterRow` record dùng nhất quán giữa Task 8 (định nghĩa), Task 9 (test), Task 10 (consume).
- `FormatSnapshot` static method được test Task 9 + implement Task 10.
- DI naming nhất quán: `TkSessionRepository`, `UsageRepository`.

**Risks không cover trong plan này (note để follow-up):**
- Hot cache `_cache` trong `TkSessionStore` chưa có invalidation cross-process. Nếu instance A update session, instance B's cache vẫn cũ. Mitigation hiện tại: re-login khi 401, OK cho session use case. Cần PubSub Redis cho cache eviction nếu sau này thấy bất đồng bộ thực tế.
- `_ = _repo.UpsertAsync(s)` fire-and-forget trong `UpdateMemory/ClearMemory` không await → race khi 2 update liên tiếp. Acceptable vì chat memory chỉ tăng dần (last-write-wins là đúng semantically).
- `UsageTracker.Track` fire-and-forget tương tự — counter SQL có thể chậm vài giây đồng bộ. Acceptable cho dashboard.
