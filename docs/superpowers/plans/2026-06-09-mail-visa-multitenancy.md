# Mail + Visa multi-tenancy fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Khắc phục bug cross-tenant data leak ở Mail + Visa: chuyển 4 store (`MailAccountStore`, `MailRepository`, `MailSyncStore`, `VisaRepository`) + `VisaFileStore` từ singleton file-backed sang DB-scoped theo `TenantId`. Bật session check ở 11 mail + 5 visa endpoint.

**Architecture:** SQL Server PushDb (shared instance như Reviews, schema thêm 4 bảng vào `TourkitAiDb.SchemaSql`). Service nhận `tenantId` qua parameter (Option B — mẫu Reviews, không inject `ITenantContext`). `MultiTenantMigration` helper move legacy file vào `data/legacy-backup/{ts}/` lúc startup — backup-then-discard. KHÔNG fallback file khi DB lỗi (trả 503).

**Tech Stack:** .NET 8, Dapper (như Reviews), SQL Server PushDb. xUnit cho `MultiTenantMigration` unit test. Mọi repo rewrite → smoke test (start proxy + curl), không unit test DB (theo precedent Reviews).

**Test command:** `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`

**Reference docs:**
- Spec: `docs/superpowers/specs/2026-06-09-mail-visa-multitenancy-design.md`
- Mẫu DB-backed: `Services/Reviews/ReviewRepository.cs` (commit `eb81a5f`)
- Schema location: `Services/Db/TourkitAiDb.cs` const `SchemaSql`

---

## File structure (Phase 0 of multi-tenant fix)

### Create

- `Services/Db/MultiTenantMigration.cs` — static helper `Run(dataDir, log)`: move `mails.json`, `mail-account.json`, `mail-sync.json`, `visa-assessments.json` + `visa-files/` vào `data/legacy-backup/{ts}/`. Sync (chỉ move file), gọi từ Program.cs sau `app.Build()`.
- `Services/TourKit/ITenantContext.cs` — interface skeleton (chưa inject vào service ở plan này, để dành Phase 1 RESTful refactor).
- `Services/TourKit/HttpTenantContext.cs` — implementation đọc `X-Session-Id` header + `TkSessionStore`. DI: `AddScoped<ITenantContext, HttpTenantContext>()`.
- `TourkitAiProxy.Tests/Db/MultiTenantMigrationTests.cs` — 3 unit test: noop khi không có legacy, move file đúng path, move folder đúng path.

### Modify

- `Services/Db/TourkitAiDb.cs` — append 4 bảng vào `SchemaSql` const: `MailAccounts`, `Mails`, `MailSyncState`, `VisaAssessments`. Cập nhật log message InitAsync.
- `Services/Mail/MailAccountStore.cs` — full rewrite: DB-backed thay file. Mọi method nhận `tenantId` param.
- `Services/Mail/MailRepository.cs` — full rewrite: DB-backed. Mọi method nhận `tenantId`.
- `Services/Mail/MailSyncStore.cs` — full rewrite: DB-backed. Mọi method nhận `tenantId`.
- `Services/Mail/IMailSource.cs` — interface đổi: `FetchRecentAsync(string tenantId, int max, CancellationToken ct)`.
- `Services/Mail/GmailImapClient.cs` — implementation nhận `tenantId`, dùng để query `MailAccountStore.Get(tenantId)` + `MailSyncStore.Get(tenantId, address)`.
- `Services/Mail/MailReplyService.cs` — `DraftStreamAsync` + `ComposeNewStreamAsync` nhận `tenantId` cho `_account.Signature(tenantId)`.
- `Services/Visa/VisaRepository.cs` — full rewrite: DB-backed. Mọi method nhận `tenantId`.
- `Services/Visa/VisaFileStore.cs` — `Save/DeleteAssessment/HasFiles` nhận `tenantId` param. Path đổi `data/visa-files/{tenantId}/{assessmentId}/{file}`.
- `Endpoints/MailEndpoints.cs` — thêm `RequireSession` helper. Mọi handler (11 endpoint) require session + extract tenant.
- `Endpoints/VisaEndpoints.cs` — same pattern (5 endpoint).
- `Program.cs` — gọi `MultiTenantMigration.Run(...)` sau `app.Build()`. Thêm `AddScoped<ITenantContext, HttpTenantContext>()`. Bổ sung `MailRepository.InitAsync` / `VisaRepository.InitAsync` task chạy startup (hoặc tận dụng `TourkitAiDb.InitAsync` đã có).
- `wwwroot/pages/mail.jsx` — mọi fetch dùng `window.tourkitAuth.authedFetch` (đã có), redirect login nếu 401.
- `wwwroot/pages/visa.jsx` — same.
- `appsettings.example.json` + `appsettings.json` — xóa block `Mail:Gmail:Address` + `Mail:Gmail:AppPassword`.
- `CLAUDE.md` — cập nhật section SmartMail/Visa: bỏ "fallback config/env" wording, thêm "per-tenant scoped via TenantId in PushDb".

---

## Task 1: MultiTenantMigration helper + unit tests

**Files:**
- Create: `Services/Db/MultiTenantMigration.cs`
- Create: `TourkitAiProxy.Tests/Db/MultiTenantMigrationTests.cs`

- [ ] **Step 1: Tạo test file với 3 test**

```csharp
// TourkitAiProxy.Tests/Db/MultiTenantMigrationTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services.Db;
using Xunit;

namespace TourkitAiProxy.Tests.Db;

public class MultiTenantMigrationTests
{
    [Fact]
    public void Run_is_noop_when_no_legacy_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            MultiTenantMigration.Run(dir, NullLogger.Instance);
            Assert.False(Directory.Exists(Path.Combine(dir, "legacy-backup")),
                "Không có legacy file → không tạo backup folder");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Run_moves_legacy_files_to_timestamped_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mails.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "mail-account.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "visa-assessments.json"), "{}");

            MultiTenantMigration.Run(dir, NullLogger.Instance);

            Assert.False(File.Exists(Path.Combine(dir, "mails.json")));
            Assert.False(File.Exists(Path.Combine(dir, "mail-account.json")));
            Assert.False(File.Exists(Path.Combine(dir, "visa-assessments.json")));

            var backups = Directory.GetDirectories(Path.Combine(dir, "legacy-backup"));
            Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(backups[0], "mails.json")));
            Assert.True(File.Exists(Path.Combine(backups[0], "mail-account.json")));
            Assert.True(File.Exists(Path.Combine(backups[0], "visa-assessments.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Run_moves_visa_files_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var visaDir = Path.Combine(dir, "visa-files", "assessment-A");
            Directory.CreateDirectory(visaDir);
            File.WriteAllBytes(Path.Combine(visaDir, "passport.pdf"), new byte[] { 1, 2, 3 });

            MultiTenantMigration.Run(dir, NullLogger.Instance);

            Assert.False(Directory.Exists(Path.Combine(dir, "visa-files")));
            var backups = Directory.GetDirectories(Path.Combine(dir, "legacy-backup"));
            Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(backups[0], "visa-files", "assessment-A", "passport.pdf")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test, expect FAIL (class not found)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MultiTenantMigration"`
Expected: `error CS0234: namespace 'Services.Db' does not contain 'MultiTenantMigration'`

- [ ] **Step 3: Implement MultiTenantMigration**

```csharp
// Services/Db/MultiTenantMigration.cs
namespace TourkitAiProxy.Services.Db;

/// <summary>
/// Backup legacy single-tenant data files lúc deploy chuyển sang multi-tenant DB.
/// Move data/{mails,mail-account,mail-sync,visa-assessments}.json + data/visa-files/
/// vào data/legacy-backup/{yyyy-MM-dd-HHmmss}/. Gọi 1 lần từ Program.cs sau app.Build().
///
/// Idempotent — gọi nhiều lần OK (lần sau không có legacy → noop).
/// Sync (chỉ move file metadata) — không cần fire-and-forget.
/// </summary>
public static class MultiTenantMigration
{
    private static readonly string[] LegacyFiles =
    {
        "mails.json", "mail-account.json", "mail-sync.json", "visa-assessments.json"
    };
    private static readonly string[] LegacyFolders = { "visa-files" };

    public static void Run(string dataDir, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;

        bool hasLegacy = LegacyFiles.Any(f => File.Exists(Path.Combine(dataDir, f)))
            || LegacyFolders.Any(d =>
                Directory.Exists(Path.Combine(dataDir, d))
                && Directory.EnumerateFileSystemEntries(Path.Combine(dataDir, d)).Any());
        if (!hasLegacy) return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
        var backupRoot = Path.Combine(dataDir, "legacy-backup", ts);
        Directory.CreateDirectory(backupRoot);

        foreach (var f in LegacyFiles)
        {
            var src = Path.Combine(dataDir, f);
            if (File.Exists(src)) File.Move(src, Path.Combine(backupRoot, f));
        }
        foreach (var d in LegacyFolders)
        {
            var src = Path.Combine(dataDir, d);
            if (Directory.Exists(src)) Directory.Move(src, Path.Combine(backupRoot, d));
        }

        log.LogWarning("[multi-tenant migration] Backed up legacy single-tenant data to {Path}. " +
                       "Mail/Visa now require login + per-tenant setup. " +
                       "Rollback: stop proxy, move files back, revert deploy.", backupRoot);
    }
}
```

- [ ] **Step 4: Run test, expect PASS (3/3)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MultiTenantMigration"`
Expected: `Passed! 3/3`

- [ ] **Step 5: Commit**

```bash
git add Services/Db/MultiTenantMigration.cs TourkitAiProxy.Tests/Db/MultiTenantMigrationTests.cs
git commit -m "feat(db): MultiTenantMigration backup helper + tests"
```

---

## Task 2: Wire MultiTenantMigration vào Program.cs

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Mở Program.cs, tìm dòng `var app = builder.Build();`**

Sau dòng đó, TRƯỚC bất kỳ `app.Use*()` nào, thêm:

```csharp
// Multi-tenant migration: backup legacy single-tenant data lần đầu deploy.
// Sync — chỉ move file, không cần fire-and-forget. Idempotent — lần sau noop.
TourkitAiProxy.Services.Db.MultiTenantMigration.Run(
    Path.Combine(app.Environment.ContentRootPath, "data"),
    app.Services.GetRequiredService<ILogger<Program>>());
```

- [ ] **Step 2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors, 0 warnings`

- [ ] **Step 3: Smoke test — start proxy + verify noop**

Đảm bảo không có legacy file ở `data/` (Phase 0 trước đó chưa tạo):
```bash
ls data/
# Nếu thấy mails.json / mail-account.json / visa-assessments.json → backup tay trước khi test
```

Start background:
```bash
dotnet run --project TourkitAiProxy.csproj --no-build
```

Wait ready, verify không có folder `legacy-backup` tạo (vì không có legacy):
```bash
ls data/legacy-backup/ 2>&1 || echo "OK no legacy-backup created"
```

Kill: `taskkill //F //IM dotnet.exe`

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "feat(startup): wire MultiTenantMigration.Run sau app.Build()"
```

---

## Task 3: Extend TourkitAiDb.SchemaSql với 4 bảng mới

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs`

- [ ] **Step 1: Mở `Services/Db/TourkitAiDb.cs`, tìm const `SchemaSql` cuối file**

Append 4 block IF OBJECT_ID vào sau block `IF OBJECT_ID('dbo.AiHistory', 'U') IS NULL`. Toàn bộ const sau khi sửa:

```csharp
    // ─── Schema (SQL Server 2016+ — dùng IF NOT EXISTS / OBJECT_ID idempotent) ──
    private const string SchemaSql = @"
IF OBJECT_ID('dbo.Reviews', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reviews (
        CustomerId     NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        [Rank]         NVARCHAR(2)     NULL,
        AlertLevel     NVARCHAR(32)    NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        TokensIn       INT             NULL,
        TokensOut      INT             NULL,
        GeneratedAt    BIGINT          NOT NULL,
        FeedbackJson   NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_Reviews PRIMARY KEY CLUSTERED (TenantId, CustomerId)
    );
    CREATE INDEX IX_Reviews_TenantId_Rank   ON dbo.Reviews(TenantId, [Rank]);
    CREATE INDEX IX_Reviews_GeneratedAt     ON dbo.Reviews(GeneratedAt DESC);
END;

IF OBJECT_ID('dbo.DealScores', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DealScores (
        DealId         NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        WinRate        INT             NULL,
        [Level]        NVARCHAR(32)    NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        TokensIn       INT             NULL,
        TokensOut      INT             NULL,
        GeneratedAt    BIGINT          NOT NULL,
        CONSTRAINT PK_DealScores PRIMARY KEY CLUSTERED (TenantId, DealId)
    );
    CREATE INDEX IX_DealScores_TenantId_Level ON dbo.DealScores(TenantId, [Level]);
END;

IF OBJECT_ID('dbo.AiHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiHistory (
        Id             BIGINT          IDENTITY(1,1) NOT NULL,
        Feature        NVARCHAR(32)    NOT NULL,
        EntityId       NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        GeneratedAt    BIGINT          NOT NULL,
        CONSTRAINT PK_AiHistory PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_AiHistory_FeatureEntity ON dbo.AiHistory(Feature, EntityId, GeneratedAt DESC);
END;

IF OBJECT_ID('dbo.MailAccounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MailAccounts (
        TenantId        NVARCHAR(128)   NOT NULL,
        Address         NVARCHAR(256)   NOT NULL,
        AppPasswordEnc  NVARCHAR(512)   NOT NULL,
        Signature       NVARCHAR(MAX)   NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_MailAccounts PRIMARY KEY CLUSTERED (TenantId)
    );
END;

IF OBJECT_ID('dbo.Mails', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Mails (
        TenantId        NVARCHAR(128)   NOT NULL,
        Id              NVARCHAR(256)   NOT NULL,
        FromName        NVARCHAR(256)   NULL,
        FromEmail       NVARCHAR(256)   NULL,
        Subject         NVARCHAR(1024)  NULL,
        Body            NVARCHAR(MAX)   NULL,
        BodyHtml        NVARCHAR(MAX)   NULL,
        ReceivedAt      DATETIME2       NOT NULL,
        IsRead          BIT             NOT NULL,
        Category        NVARCHAR(32)    NULL,
        Status          NVARCHAR(32)    NOT NULL,
        AiSummary       NVARCHAR(MAX)   NULL,
        DraftJson       NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_Mails PRIMARY KEY CLUSTERED (TenantId, Id)
    );
    CREATE INDEX IX_Mails_Tenant_Received ON dbo.Mails(TenantId, ReceivedAt DESC);
END;

IF OBJECT_ID('dbo.MailSyncState', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MailSyncState (
        TenantId        NVARCHAR(128)   NOT NULL,
        Address         NVARCHAR(256)   NOT NULL,
        UidValidity     BIGINT          NOT NULL,
        LastUid         BIGINT          NOT NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_MailSyncState PRIMARY KEY CLUSTERED (TenantId, Address)
    );
END;

IF OBJECT_ID('dbo.VisaAssessments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.VisaAssessments (
        TenantId        NVARCHAR(128)   NOT NULL,
        Id              NVARCHAR(64)    NOT NULL,
        ApplicantName   NVARCHAR(256)   NULL,
        Country         NVARCHAR(64)    NULL,
        Status          NVARCHAR(32)    NOT NULL,
        ExtractionJson  NVARCHAR(MAX)   NOT NULL,
        ResultJson      NVARCHAR(MAX)   NULL,
        FileCount       INT             NOT NULL,
        FilesPurged     BIT             NOT NULL,
        CreatedAt       DATETIME2       NOT NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_VisaAssessments PRIMARY KEY CLUSTERED (TenantId, Id)
    );
    CREATE INDEX IX_VisaAssessments_Tenant_Created ON dbo.VisaAssessments(TenantId, CreatedAt DESC);
END;
";
```

- [ ] **Step 2: Sửa log message InitAsync để mention bảng mới**

Tìm dòng:
```csharp
_log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory đã có/đã tạo)");
```

Đổi thành:
```csharp
_log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory/MailAccounts/Mails/MailSyncState/VisaAssessments đã có/đã tạo)");
```

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors, 0 warnings`

- [ ] **Step 4: Smoke test — start proxy + verify schema init log**

```bash
dotnet run --project TourkitAiProxy.csproj --no-build
```

Wait ready (`http://localhost:5080/healthz` returns 200). Verify log có dòng `TourkitAiDb schema OK (Reviews/.../VisaAssessments đã có/đã tạo)`. Kill:

```bash
taskkill //F //IM dotnet.exe
```

Nếu DB chưa sẵn sàng (vd local dev không có SQL Server), log sẽ là `TourkitAiDb InitAsync thất bại — DB chưa sẵn sàng...` — OK cho local. Phase tiếp theo cần DB thật để test repo rewrite.

- [ ] **Step 5: Commit**

```bash
git add Services/Db/TourkitAiDb.cs
git commit -m "feat(db): thêm 4 bảng MailAccounts/Mails/MailSyncState/VisaAssessments vào SchemaSql"
```

---

## Task 4: ITenantContext interface + HttpTenantContext implementation

**Files:**
- Create: `Services/TourKit/ITenantContext.cs`
- Create: `Services/TourKit/HttpTenantContext.cs`
- Modify: `Program.cs` (DI register)

- [ ] **Step 1: Tạo `Services/TourKit/ITenantContext.cs`**

```csharp
// Services/TourKit/ITenantContext.cs
namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// Cung cấp TenantId của request hiện tại — đọc từ X-Session-Id header + TkSessionStore.
/// Phase 1 RESTful sẽ extract qua TenantFilter; ở plan này chỉ tạo skeleton, services
/// vẫn nhận tenantId qua parameter (Option B mẫu Reviews).
/// </summary>
public interface ITenantContext
{
    /// TenantId của session hiện tại. Throw nếu anonymous — caller phải đảm bảo auth đã pass.
    string TenantId { get; }

    /// Try variant — trả null nếu anonymous (vd background job, healthcheck).
    string? TryGetTenantId();
}
```

- [ ] **Step 2: Tạo `Services/TourKit/HttpTenantContext.cs`**

```csharp
// Services/TourKit/HttpTenantContext.cs
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// HttpContext-backed implementation: đọc X-Session-Id header / query, lookup TkSessionStore.
/// Scoped — 1 instance per request.
/// </summary>
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _http;
    private readonly TkSessionStore _sessions;

    public HttpTenantContext(IHttpContextAccessor http, TkSessionStore sessions)
    {
        _http = http; _sessions = sessions;
    }

    public string TenantId
        => TryGetTenantId() ?? throw new InvalidOperationException(
            "Anonymous request — caller phải đảm bảo session đã được verify trước khi gọi ITenantContext.TenantId");

    public string? TryGetTenantId()
    {
        var ctx = _http.HttpContext;
        if (ctx == null) return null;
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        return _sessions.Get(sid)?.TenantId;
    }
}
```

- [ ] **Step 3: Wire DI ở `Program.cs`**

Tìm dòng `builder.Services.AddHttpContextAccessor();` (đã có). Sau nó, thêm:

```csharp
// ITenantContext — đọc tenantId từ X-Session-Id header. Phase 1 RESTful sẽ dùng nhiều
// qua TenantFilter; ở plan này chỉ register, services vẫn nhận tenantId qua parameter.
builder.Services.AddScoped<TourkitAiProxy.Services.TourKit.ITenantContext,
                          TourkitAiProxy.Services.TourKit.HttpTenantContext>();
```

- [ ] **Step 4: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors, 0 warnings`

- [ ] **Step 5: Commit**

```bash
git add Services/TourKit/ITenantContext.cs Services/TourKit/HttpTenantContext.cs Program.cs
git commit -m "feat(tenant): ITenantContext + HttpTenantContext skeleton (chưa inject vào service)"
```

---

## Task 5: MailAccountStore DB rewrite + cập nhật callers

**Files:**
- Modify: `Services/Mail/MailAccountStore.cs`
- Modify: `Services/Mail/MailReplyService.cs`
- Modify: `Endpoints/MailEndpoints.cs`

- [ ] **Step 1: Rewrite `Services/Mail/MailAccountStore.cs` full**

```csharp
using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Lưu/đọc creds hộp thư Gmail + chữ ký, SCOPED THEO TenantId.
/// DB-backed (dbo.MailAccounts). App Password mã hóa Crypton; tenant khác KHÔNG thấy creds nhau.
/// Không có fallback file — DB lỗi → throw, endpoint trả 503.
/// </summary>
public class MailAccountStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailAccountStore> _log;

    public MailAccountStore(TourkitAiDb db, ILogger<MailAccountStore> log)
    {
        _db = db; _log = log;
    }

    /// Lấy creds của tenant. null nếu chưa cấu hình.
    public (string Address, string AppPassword)? Get(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<(string Address, string AppPasswordEnc)>(
            @"SELECT Address, AppPasswordEnc FROM dbo.MailAccounts WHERE TenantId = @t",
            new { t = tenantId });
        if (row.Address == null) return null;
        var pwd = string.IsNullOrEmpty(row.AppPasswordEnc) ? "" : Crypton.Decrypt(row.AppPasswordEnc);
        return (row.Address, pwd);
    }

    /// Upsert creds + chữ ký cho tenant.
    public void Set(string tenantId, string address, string appPassword, string? signature)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));

        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.MailAccounts AS T
USING (SELECT @t AS TenantId) AS S
   ON T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET Address=@a, AppPasswordEnc=@p, Signature=@s, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Address, AppPasswordEnc, Signature, UpdatedAt)
                       VALUES (@t, @a, @p, @s, SYSUTCDATETIME());",
            new
            {
                t = tenantId,
                a = address.Trim(),
                p = Crypton.Encrypt(appPassword.Trim()),
                s = (signature ?? "").Trim()
            });
        _log.LogInformation("[MailAccount] Set tenant={Tenant} address={Addr}", tenantId, address.Trim());
    }

    public bool IsConfigured(string tenantId) => Get(tenantId) is { } x && !string.IsNullOrWhiteSpace(x.Address);

    /// Địa chỉ đang cấu hình (cho UI hiển thị) — KHÔNG trả App Password. Empty nếu chưa setup.
    public string CurrentAddress(string tenantId) => Get(tenantId)?.Address ?? "";

    /// Chữ ký công ty. Empty nếu chưa setup hoặc chưa đặt.
    public string Signature(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return "";
        using var c = _db.Open();
        return c.QueryFirstOrDefault<string?>(
            @"SELECT Signature FROM dbo.MailAccounts WHERE TenantId = @t",
            new { t = tenantId }) ?? "";
    }

    public bool HasSignature(string tenantId) => !string.IsNullOrWhiteSpace(Signature(tenantId));
}
```

- [ ] **Step 2: Cập nhật `Services/Mail/MailReplyService.cs` — `ComposeNewStreamAsync` + `DraftStreamAsync` nhận tenantId**

Mở file. Tìm 2 method `ComposeNewStreamAsync` và `DraftStreamAsync`. Cả 2 đều gọi `_account.Signature()` (no arg) — đổi thành `_account.Signature(tenantId)`. Thêm param `string tenantId` vào signature method.

Cụ thể search & modify:
- `public async Task<string> ComposeNewStreamAsync(ComposeDraftRequest req, ...)` → thêm `string tenantId` làm param đầu tiên (sau `this`)
- `public async Task<string> DraftStreamAsync(MailItem mail, DraftReplyRequest req, ...)` → tương tự thêm `string tenantId` param đầu

Tìm tất cả `_account.Signature()` trong file → đổi thành `_account.Signature(tenantId)`.

- [ ] **Step 3: Cập nhật `Endpoints/MailEndpoints.cs` — tạm pass placeholder `""` cho tenantId**

Mọi gọi `account.CurrentAddress()` → `account.CurrentAddress("")`. Tương tự `IsConfigured()` → `IsConfigured("")`, `Signature()` → `Signature("")`, `Set(addr, pwd, sig)` → `Set("", addr, pwd, sig)`.

Trong handler `/mail/compose/draft`: `replyService.ComposeNewStreamAsync(req, ...)` → `replyService.ComposeNewStreamAsync("", req, ...)`.

Trong handler `/mail/{id}/reply/draft`: `replyService.DraftStreamAsync(mail, req, ...)` → `replyService.DraftStreamAsync("", mail, req, ...)`.

**Lưu ý:** placeholder `""` này sẽ được replace bằng real tenantId ở Task 10 (MailEndpoints session check). Hiện tại tenant="" tương đương "chưa scope" — hành vi gần giống cũ, không breaking compile.

- [ ] **Step 4: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`. Có thể có warning về `_account.IsConfigured("")` luôn pass empty — bỏ qua, sẽ fix ở Task 10.

- [ ] **Step 5: Run tests verify không regress**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: tests pass (101+, có thể skip vài integration nếu có).

- [ ] **Step 6: Commit**

```bash
git add Services/Mail/MailAccountStore.cs Services/Mail/MailReplyService.cs Endpoints/MailEndpoints.cs
git commit -m "refactor(mail): MailAccountStore DB-backed per-tenant + caller updates (placeholder tenantId)"
```

---

## Task 6: MailRepository DB rewrite + callers

**Files:**
- Modify: `Services/Mail/MailRepository.cs`
- Modify: `Endpoints/MailEndpoints.cs`

- [ ] **Step 1: Rewrite `Services/Mail/MailRepository.cs` full**

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Counts cho sidebar: tổng + chưa đọc + theo trạng thái + theo nhóm.
/// </summary>
public record MailCounts(int Total, int Unread, Dictionary<string, int> ByStatus, Dictionary<string, int> ByCategory);

/// <summary>
/// SQL Server-backed store: (TenantId, mailId) → MailItem. Persist dbo.Mails.
/// Mọi method nhận tenantId — query luôn filter scoped theo tenant. Cross-tenant access trả null.
/// Không fallback file — DB lỗi → throw, endpoint trả 503.
/// </summary>
public class MailRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MailRepository(TourkitAiDb db, ILogger<MailRepository> log)
    {
        _db = db; _log = log;
    }

    public MailItem? Get(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<MailRow>(
            @"SELECT * FROM dbo.Mails WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id });
        return row == null ? null : Hydrate(row);
    }

    public bool Has(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return false;
        using var c = _db.Open();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM dbo.Mails WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id }) > 0;
    }

    public void Upsert(string tenantId, MailItem item)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));
        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.Mails AS T
USING (SELECT @t AS TenantId, @id AS Id) AS S
   ON T.TenantId = S.TenantId AND T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    FromName=@fn, FromEmail=@fe, Subject=@sub, Body=@body, BodyHtml=@html,
    ReceivedAt=@recv, IsRead=@read, Category=@cat, Status=@stat,
    AiSummary=@sum, DraftJson=@draft
WHEN NOT MATCHED THEN INSERT
    (TenantId, Id, FromName, FromEmail, Subject, Body, BodyHtml, ReceivedAt, IsRead, Category, Status, AiSummary, DraftJson)
VALUES
    (@t, @id, @fn, @fe, @sub, @body, @html, @recv, @read, @cat, @stat, @sum, @draft);",
            new
            {
                t = tenantId, id = item.Id,
                fn = item.From.Name, fe = item.From.Email,
                sub = item.Subject, body = item.Body, html = item.BodyHtml,
                recv = DateTime.TryParse(item.ReceivedAt, out var dt) ? dt : DateTime.UtcNow,
                read = item.IsRead, cat = item.Category, stat = item.Status, sum = item.AiSummary,
                draft = item.Draft == null ? null : JsonSerializer.Serialize(item.Draft, _jsonOpts)
            });
    }

    public bool SetStatus(string tenantId, string id, string status)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "UPDATE dbo.Mails SET Status=@s WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, s = status }) > 0;
    }

    public bool SetRead(string tenantId, string id, bool isRead = true)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "UPDATE dbo.Mails SET IsRead=@r WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, r = isRead }) > 0;
    }

    public bool SetDraft(string tenantId, string id, MailDraft draft, string status)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        var draftJson = JsonSerializer.Serialize(draft, _jsonOpts);
        return c.Execute(
            "UPDATE dbo.Mails SET DraftJson=@d, Status=@s WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id, d = draftJson, s = status }) > 0;
    }

    /// Lọc theo status/category/search (search bỏ dấu, không phân biệt hoa). Mới nhất trước.
    public IReadOnlyList<MailItem> Filter(string tenantId, string? status, string? category, string? search)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return Array.Empty<MailItem>();
        using var c = _db.Open();
        var rows = c.Query<MailRow>(
            "SELECT * FROM dbo.Mails WHERE TenantId=@t ORDER BY ReceivedAt DESC",
            new { t = tenantId }).ToList();

        IEnumerable<MailItem> q = rows.Select(Hydrate).Where(m => m != null)!;
        if (!string.IsNullOrWhiteSpace(status))   q = q.Where(m => m!.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m!.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = Norm(search);
            q = q.Where(m => Norm($"{m!.Subject} {m.From.Name} {m.From.Email} {m.Body}").Contains(s));
        }
        return q.ToList()!;
    }

    public MailCounts Counts(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new MailCounts(0, 0, new(), new());
        using var c = _db.Open();
        var rows = c.Query<(string Status, string? Category, bool IsRead)>(
            "SELECT Status, Category, IsRead FROM dbo.Mails WHERE TenantId=@t",
            new { t = tenantId }).ToList();

        var byStatus = new Dictionary<string, int>();
        var byCat = new Dictionary<string, int>();
        int unread = 0;
        foreach (var r in rows)
        {
            byStatus[r.Status] = byStatus.GetValueOrDefault(r.Status) + 1;
            var cat = r.Category ?? "khac";
            byCat[cat] = byCat.GetValueOrDefault(cat) + 1;
            if (!r.IsRead) unread++;
        }
        return new MailCounts(rows.Count, unread, byStatus, byCat);
    }

    // ─── Hydration ────────────────────────────────────────────────────────
    private MailItem? Hydrate(MailRow row)
    {
        try
        {
            MailDraft? draft = string.IsNullOrEmpty(row.DraftJson)
                ? null
                : JsonSerializer.Deserialize<MailDraft>(row.DraftJson, _jsonOpts);
            return new MailItem(
                Id: row.Id,
                From: new MailContact(row.FromName ?? "", row.FromEmail ?? ""),
                Subject: row.Subject ?? "",
                Body: row.Body ?? "",
                ReceivedAt: row.ReceivedAt.ToString("o"),
                IsRead: row.IsRead,
                Category: row.Category,
                Status: row.Status,
                AiSummary: row.AiSummary,
                Draft: draft,
                BodyHtml: row.BodyHtml);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[MailRepo] Hydrate row {Id} fail", row.Id);
            return null;
        }
    }

    private sealed class MailRow
    {
        public string TenantId { get; set; } = "";
        public string Id { get; set; } = "";
        public string? FromName { get; set; }
        public string? FromEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? BodyHtml { get; set; }
        public DateTime ReceivedAt { get; set; }
        public bool IsRead { get; set; }
        public string? Category { get; set; }
        public string Status { get; set; } = "moi";
        public string? AiSummary { get; set; }
        public string? DraftJson { get; set; }
    }

    /// Chuẩn hóa search: lowercase + bỏ dấu tiếng Việt + đ→d.
    private static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
```

- [ ] **Step 2: Cập nhật `Endpoints/MailEndpoints.cs` — pass `""` placeholder cho tenantId vào mọi repo call**

Tìm tất cả `repo.Has(...)`, `repo.Upsert(...)`, `repo.Get(...)`, `repo.SetRead(...)`, `repo.SetStatus(...)`, `repo.SetDraft(...)`, `repo.Filter(...)`, `repo.Counts(...)` → prefix arg đầu thành `""`.

Cụ thể (sửa từng dòng):
- `repo.Has(mail.Id)` → `repo.Has("", mail.Id)`
- `repo.Upsert(mail with { ... })` → `repo.Upsert("", mail with { ... })`
- `repo.Filter(null, null, null)` → `repo.Filter("", null, null, null)`
- `repo.Counts()` → `repo.Counts("")`
- `repo.Get(id)` → `repo.Get("", id)`
- `repo.SetRead(id, true)` → `repo.SetRead("", id, true)`
- `repo.SetStatus(id, req.Status)` → `repo.SetStatus("", id, req.Status)`
- `repo.SetDraft(id, draft, status: "da_phan_hoi")` → `repo.SetDraft("", id, draft, status: "da_phan_hoi")`
- `repo.Filter(status, category, search)` → `repo.Filter("", status, category, search)`

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Run tests verify không regress**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: tests pass.

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailRepository.cs Endpoints/MailEndpoints.cs
git commit -m "refactor(mail): MailRepository DB-backed per-tenant + caller updates (placeholder tenantId)"
```

---

## Task 7: IMailSource interface + GmailImapClient + MailSyncStore DB rewrite

**Files:**
- Modify: `Services/Mail/IMailSource.cs`
- Modify: `Services/Mail/GmailImapClient.cs`
- Modify: `Services/Mail/MailSyncStore.cs`
- Modify: `Endpoints/MailEndpoints.cs`

- [ ] **Step 1: Đổi interface `IMailSource.cs`**

```csharp
// Services/Mail/IMailSource.cs
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// <summary>Nguồn mail per-tenant — pull email mới hơn lần sync trước theo TenantId.</summary>
public interface IMailSource
{
    /// Pull N email mới nhất cho tenant. Incremental: chỉ email có UID > lần trước.
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(string tenantId, int max, CancellationToken ct);
}
```

- [ ] **Step 2: Rewrite `Services/Mail/MailSyncStore.cs` full sang DB**

```csharp
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// State đồng bộ IMAP per-(TenantId, Address) — để kéo INCREMENTAL chỉ email UID mới hơn lần trước.
/// DB-backed dbo.MailSyncState. UidValidity đổi (server reset) → coi như mới, kéo lại từ đầu.
/// </summary>
public class MailSyncStore
{
    public record SyncState(uint UidValidity, uint LastUid);

    private readonly TourkitAiDb _db;
    private readonly ILogger<MailSyncStore> _log;

    public MailSyncStore(TourkitAiDb db, ILogger<MailSyncStore> log)
    {
        _db = db; _log = log;
    }

    public SyncState? Get(string tenantId, string address)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(address)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<(long UidValidity, long LastUid)>(
            @"SELECT UidValidity, LastUid FROM dbo.MailSyncState
              WHERE TenantId=@t AND Address=@a",
            new { t = tenantId, a = address });
        return row.UidValidity == 0 && row.LastUid == 0
            ? null
            : new SyncState((uint)row.UidValidity, (uint)row.LastUid);
    }

    public void Set(string tenantId, string address, uint uidValidity, uint lastUid)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("tenantId / address rỗng");
        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.MailSyncState AS T
USING (SELECT @t AS TenantId, @a AS Address) AS S
   ON T.TenantId = S.TenantId AND T.Address = S.Address
WHEN MATCHED THEN UPDATE SET UidValidity=@uv, LastUid=@lu, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Address, UidValidity, LastUid, UpdatedAt)
                       VALUES (@t, @a, @uv, @lu, SYSUTCDATETIME());",
            new { t = tenantId, a = address, uv = (long)uidValidity, lu = (long)lastUid });
    }
}
```

- [ ] **Step 3: Cập nhật `Services/Mail/GmailImapClient.cs` — `FetchRecentAsync` nhận tenantId**

Mở file. Tìm method `FetchRecentAsync(int max, CancellationToken ct)`. Đổi signature thành `FetchRecentAsync(string tenantId, int max, CancellationToken ct)`.

Trong body method:
- Tìm gọi `_account.Get()` → đổi thành `_account.Get(tenantId)`
- Tìm gọi `_sync.Get(address)` → đổi thành `_sync.Get(tenantId, address)`
- Tìm gọi `_sync.Set(address, ...)` → đổi thành `_sync.Set(tenantId, address, ...)`

Nếu method có check `if (creds == null) throw new InvalidOperationException(...)` — giữ nguyên, chỉ cập nhật message để mention tenant.

- [ ] **Step 4: Cập nhật `Endpoints/MailEndpoints.cs` — pass `""` placeholder vào `source.FetchRecentAsync`**

Tìm trong handler `/mail/sync`:
```csharp
fetched = await source.FetchRecentAsync(SyncMax, ctx.RequestAborted);
```

Đổi thành:
```csharp
fetched = await source.FetchRecentAsync("", SyncMax, ctx.RequestAborted);
```

- [ ] **Step 5: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`. Có thể có warning nullable trong Dapper tuple — bỏ qua nếu không phải error.

- [ ] **Step 6: Run tests**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: tests pass.

- [ ] **Step 7: Commit**

```bash
git add Services/Mail/IMailSource.cs Services/Mail/MailSyncStore.cs Services/Mail/GmailImapClient.cs Endpoints/MailEndpoints.cs
git commit -m "refactor(mail): IMailSource + GmailImapClient + MailSyncStore per-tenant (placeholder)"
```

---

## Task 8: VisaRepository DB rewrite + callers

**Files:**
- Modify: `Services/Visa/VisaRepository.cs`
- Modify: `Endpoints/VisaEndpoints.cs`

- [ ] **Step 1: Rewrite `Services/Visa/VisaRepository.cs` full**

```csharp
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// SQL Server-backed store: (TenantId, assessmentId) → VisaAssessment. Persist dbo.VisaAssessments.
/// Mọi method nhận tenantId — query scoped theo tenant. Cross-tenant access trả null/false.
/// </summary>
public class VisaRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<VisaRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VisaRepository(TourkitAiDb db, ILogger<VisaRepository> log)
    {
        _db = db; _log = log;
    }

    public VisaAssessment? Get(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<VisaRow>(
            @"SELECT * FROM dbo.VisaAssessments WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id });
        return row == null ? null : Hydrate(row);
    }

    public List<VisaAssessment> All(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return new();
        using var c = _db.Open();
        var rows = c.Query<VisaRow>(
            @"SELECT * FROM dbo.VisaAssessments WHERE TenantId=@t ORDER BY CreatedAt DESC",
            new { t = tenantId }).ToList();
        return rows.Select(Hydrate).Where(a => a != null).ToList()!;
    }

    public void Save(string tenantId, VisaAssessment a)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));
        using var c = _db.Open();
        var extJson = JsonSerializer.Serialize(a.Extraction, _jsonOpts);
        var resJson = a.Result == null ? null : JsonSerializer.Serialize(a.Result, _jsonOpts);
        c.Execute(@"
MERGE dbo.VisaAssessments AS T
USING (SELECT @t AS TenantId, @id AS Id) AS S
   ON T.TenantId = S.TenantId AND T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    ApplicantName=@an, Country=@co, Status=@st,
    ExtractionJson=@ext, ResultJson=@res,
    FileCount=@fc, FilesPurged=@fp, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Id, ApplicantName, Country, Status, ExtractionJson, ResultJson,
     FileCount, FilesPurged, CreatedAt, UpdatedAt)
VALUES
    (@t, @id, @an, @co, @st, @ext, @res, @fc, @fp, @cre, SYSUTCDATETIME());",
            new
            {
                t = tenantId, id = a.Id,
                an = a.ApplicantName, co = a.Country, st = a.Status,
                ext = extJson, res = resJson,
                fc = a.FileCount, fp = a.FilesPurged,
                cre = DateTime.TryParse(a.CreatedAt, out var dt) ? dt : DateTime.UtcNow
            });
    }

    public bool Delete(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "DELETE FROM dbo.VisaAssessments WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id }) > 0;
    }

    // ─── Hydration ────────────────────────────────────────────────────────
    private VisaAssessment? Hydrate(VisaRow row)
    {
        try
        {
            var ext = JsonSerializer.Deserialize<VisaExtraction>(row.ExtractionJson, _jsonOpts)
                ?? throw new InvalidOperationException("Extraction null");
            VisaResult? res = string.IsNullOrEmpty(row.ResultJson)
                ? null
                : JsonSerializer.Deserialize<VisaResult>(row.ResultJson, _jsonOpts);
            return new VisaAssessment(
                Id: row.Id,
                ApplicantName: row.ApplicantName ?? "",
                Country: row.Country,
                Status: row.Status,
                Extraction: ext,
                Result: res,
                FileCount: row.FileCount,
                FilesPurged: row.FilesPurged,
                CreatedAt: row.CreatedAt.ToString("o"),
                UpdatedAt: row.UpdatedAt.ToString("o"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[VisaRepo] Hydrate row {Id} fail", row.Id);
            return null;
        }
    }

    private sealed class VisaRow
    {
        public string TenantId { get; set; } = "";
        public string Id { get; set; } = "";
        public string? ApplicantName { get; set; }
        public string? Country { get; set; }
        public string Status { get; set; } = "extracted";
        public string ExtractionJson { get; set; } = "{}";
        public string? ResultJson { get; set; }
        public int FileCount { get; set; }
        public bool FilesPurged { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
```

- [ ] **Step 2: Cập nhật `Endpoints/VisaEndpoints.cs` — pass `""` placeholder**

Mở file. Tìm tất cả `repo.Get(...)`, `repo.All()`, `repo.Save(...)`, `repo.Delete(...)` → prefix arg đầu thành `""`.

Cụ thể:
- `repo.All()` → `repo.All("")`
- `repo.Get(id)` → `repo.Get("", id)`
- `repo.Save(a)` → `repo.Save("", a)`
- `repo.Delete(id)` → `repo.Delete("", id)`

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Run tests**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: tests pass.

- [ ] **Step 5: Commit**

```bash
git add Services/Visa/VisaRepository.cs Endpoints/VisaEndpoints.cs
git commit -m "refactor(visa): VisaRepository DB-backed per-tenant + caller updates (placeholder)"
```

---

## Task 9: VisaFileStore tenant param + callers

**Files:**
- Modify: `Services/Visa/VisaFileStore.cs`
- Modify: `Endpoints/VisaEndpoints.cs`

- [ ] **Step 1: Rewrite `Services/Visa/VisaFileStore.cs` thêm tenantId param**

```csharp
namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// Lưu file hồ sơ visa gốc (ảnh/PDF) TẠM THỜI tại data/visa-files/{tenantId}/{assessmentId}/.
/// Tự xóa thư mục cũ hơn RetentionDays (mặc định 7) — lazy purge mỗi lần ghi + lúc khởi động.
/// PII nhạy cảm KHÔNG giữ lâu; thư mục data/ đã gitignored.
///
/// Multi-tenant: path scoped theo tenantId → cross-tenant không đọc được file nhau.
/// </summary>
public class VisaFileStore
{
    private const int RetentionDays = 7;
    private readonly string _root;
    private readonly ILogger<VisaFileStore> _log;
    private readonly object _lock = new();

    public VisaFileStore(IWebHostEnvironment env, ILogger<VisaFileStore> log)
    {
        _log = log;
        _root = Path.Combine(env.ContentRootPath, "data", "visa-files");
        Directory.CreateDirectory(_root);
        Purge();
    }

    /// Lưu file vào data/visa-files/{tenantId}/{assessmentId}/. Trả đường dẫn đĩa.
    public string Save(string tenantId, string assessmentId, int index, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(_root, Safe(tenantId), Safe(assessmentId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{index:D2}_{Safe(fileName)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// Xóa toàn bộ file của 1 assessment (khi user xóa lượt thẩm định).
    public void DeleteAssessment(string tenantId, string assessmentId)
    {
        var dir = Path.Combine(_root, Safe(tenantId), Safe(assessmentId));
        TryDeleteDir(dir);
    }

    /// Có file của assessment này không (cross-tenant = false).
    public bool HasFiles(string tenantId, string assessmentId)
        => Directory.Exists(Path.Combine(_root, Safe(tenantId), Safe(assessmentId)));

    /// Dọn file cũ hơn RetentionDays. Global (mọi tenant), gọi lazy mỗi lần upload + startup.
    public void Purge()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                var cutoffTicks = DateTime.UtcNow.AddDays(-RetentionDays).Ticks;
                // 2 levels: data/visa-files/{tenant}/{assessment}/
                foreach (var tenantDir in Directory.GetDirectories(_root))
                {
                    foreach (var assDir in Directory.GetDirectories(tenantDir))
                    {
                        if (Directory.GetLastWriteTimeUtc(assDir).Ticks < cutoffTicks)
                            TryDeleteDir(assDir);
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Purge visa-files lỗi"); }
        }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Xóa thư mục {Dir} lỗi", dir); }
    }

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 80 ? s[..80] : s;
    }
}
```

- [ ] **Step 2: Cập nhật `Endpoints/VisaEndpoints.cs` — pass `""` cho VisaFileStore methods**

Tìm trong handler `POST /visa/assess`:
- `store.Save(assessmentId, ...)` → `store.Save("", assessmentId, ...)`

Trong handler `DELETE /visa/assessments/{id}`:
- `store.DeleteAssessment(id)` → `store.DeleteAssessment("", id)`

Trong handler `GET /visa/assessments/{id}` nếu có `store.HasFiles(id)`:
- `store.HasFiles(id)` → `store.HasFiles("", id)`

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Run tests**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: tests pass.

- [ ] **Step 5: Commit**

```bash
git add Services/Visa/VisaFileStore.cs Endpoints/VisaEndpoints.cs
git commit -m "refactor(visa): VisaFileStore tenant-scoped path (placeholder caller)"
```

---

## Task 10: MailEndpoints require session + replace placeholder với real tenant

**Files:**
- Modify: `Endpoints/MailEndpoints.cs`

- [ ] **Step 1: Thêm `RequireSession` helper ở cuối class `MailEndpoints`**

Sau method `Sse(...)` cuối file, append:

```csharp
    /// Extract sessionId + tenantId từ request. Return null nếu missing/invalid session.
    /// Handler caller trả 401 nếu null.
    private static (string SessionId, string TenantId)? RequireSession(
        HttpContext ctx, TourkitAiProxy.Services.TourKit.TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId);
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
```

Thêm `using TourkitAiProxy.Services.TourKit;` ở top nếu chưa có.

- [ ] **Step 2: Cập nhật từng handler — thêm TkSessionStore vào DI signature + RequireSession check + thay `""` bằng real tenantId**

Đây là pattern áp dụng cho mọi handler. Ví dụ GET /mail/account:

```csharp
v1.MapGet("/mail/account", (HttpContext ctx, MailAccountStore account, TkSessionStore sessions) =>
{
    var auth = RequireSession(ctx, sessions);
    if (auth == null) return Unauthorized();
    var (_, tenant) = auth.Value;
    return Results.Json(new
    {
        address = account.CurrentAddress(tenant),
        configured = account.IsConfigured(tenant),
        signature = account.Signature(tenant)
    });
});
```

Áp dụng tương tự cho 10 endpoint còn lại. Mỗi handler:
1. Thêm `HttpContext ctx` và `TkSessionStore sessions` vào DI signature (nếu chưa có)
2. Đầu handler: `var auth = RequireSession(ctx, sessions); if (auth == null) return Unauthorized(); var (_, tenant) = auth.Value;`
3. Mọi `""` placeholder ở Task 5-7 → đổi thành `tenant`

Specific changes per handler (replace `""` → `tenant`):
- `POST /mail/account`: `account.Set("", ...)` → `account.Set(tenant, ...)`
- `POST /mail/sync`: `source.FetchRecentAsync("", ...)` → `source.FetchRecentAsync(tenant, ...)`; `repo.Has("", ...)` → `repo.Has(tenant, ...)`; `repo.Upsert("", ...)` → `repo.Upsert(tenant, ...)`; `repo.Filter("", null, null, null)` → `repo.Filter(tenant, null, null, null)`; `repo.Counts("")` → `repo.Counts(tenant)`
- `GET /mail`: `repo.Filter("", ...)` → `repo.Filter(tenant, ...)`; `repo.Counts("")` → `repo.Counts(tenant)`
- `GET /mail/{id}`: `repo.Get("", id)` → `repo.Get(tenant, id)`
- `POST /mail/{id}/read`: `repo.SetRead("", id, true)` → `repo.SetRead(tenant, id, true)`
- `POST /mail/compose/draft`: `replyService.ComposeNewStreamAsync("", req, ...)` → `replyService.ComposeNewStreamAsync(tenant, req, ...)`
- `POST /mail/compose/send`: no repo call để thay (just calls sender.SendAsync). Nhưng `sender.SendAsync` cần biết creds — Task 13 sẽ thêm tenant nếu cần. Hiện chỉ require session để chặn anonymous.
- `POST /mail/{id}/reply/draft`: `repo.Get("", id)` → `repo.Get(tenant, id)`; `replyService.DraftStreamAsync("", mail, req, ...)` → `replyService.DraftStreamAsync(tenant, mail, req, ...)`
- `POST /mail/{id}/reply/send`: `repo.Get("", id)` → `repo.Get(tenant, id)`; `repo.SetDraft("", id, draft, "da_phan_hoi")` → `repo.SetDraft(tenant, id, draft, "da_phan_hoi")`
- `PATCH /mail/{id}/status`: `repo.SetStatus("", id, ...)` → `repo.SetStatus(tenant, id, ...)`

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Smoke test — verify 401 unauthorized**

```bash
dotnet run --project TourkitAiProxy.csproj --no-build
```

Wait ready:
```bash
curl -s http://localhost:5080/api/v1/mail -o /dev/null -w "HTTP %{http_code}\n"
# Expected: HTTP 401
curl -s http://localhost:5080/api/v1/mail/account -o /dev/null -w "HTTP %{http_code}\n"
# Expected: HTTP 401
```

Kill: `taskkill //F //IM dotnet.exe`

- [ ] **Step 5: Commit**

```bash
git add Endpoints/MailEndpoints.cs
git commit -m "feat(mail): require session + thread real tenantId cho 11 endpoint"
```

---

## Task 11: VisaEndpoints require session + replace placeholder

**Files:**
- Modify: `Endpoints/VisaEndpoints.cs`

- [ ] **Step 1: Thêm `RequireSession` + `Unauthorized` helpers ở cuối class `VisaEndpoints`**

```csharp
    private static (string SessionId, string TenantId)? RequireSession(
        HttpContext ctx, TourkitAiProxy.Services.TourKit.TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId);
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
```

Thêm `using TourkitAiProxy.Services.TourKit;` ở top nếu chưa.

- [ ] **Step 2: Cập nhật 5 handler — RequireSession + thay `""` → `tenant`**

Apply pattern (như Task 10) cho 5 endpoint:
- `POST /visa/assess`
- `POST /visa/assess/{id}/score`
- `GET /visa/assessments`
- `GET /visa/assessments/{id}`
- `DELETE /visa/assessments/{id}`

Mỗi handler: thêm `HttpContext ctx, TkSessionStore sessions` vào DI, RequireSession check đầu handler, replace `""` → `tenant` cho tất cả `repo.*` + `store.*` call.

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Smoke test**

```bash
dotnet run --project TourkitAiProxy.csproj --no-build
```

Wait ready:
```bash
curl -s http://localhost:5080/api/v1/visa/assessments -o /dev/null -w "HTTP %{http_code}\n"
# Expected: HTTP 401
```

Kill: `taskkill //F //IM dotnet.exe`

- [ ] **Step 5: Commit**

```bash
git add Endpoints/VisaEndpoints.cs
git commit -m "feat(visa): require session + thread real tenantId cho 5 endpoint"
```

---

## Task 12: Frontend mail.jsx + visa.jsx authedFetch + login gate

**Files:**
- Modify: `wwwroot/pages/mail.jsx`
- Modify: `wwwroot/pages/visa.jsx`

- [ ] **Step 1: Mở `wwwroot/pages/mail.jsx`, tìm các `fetch(...)` calls**

Mỗi `fetch('/api/v1/mail/...')` → đổi thành `window.tourkitAuth.authedFetch('/api/v1/mail/...')`. Helper `authedFetch` đã có (xem `wwwroot/core/auth.jsx`).

Nếu mail.jsx chưa import auth helper: thêm check ở đầu component:
```jsx
function MailPage({ pushToast }) {
  const session = window.tourkitAuth?.getSession();
  if (!session?.sessionId) {
    return <div className="empty-state">Vui lòng đăng nhập để dùng SmartMail.</div>;
  }
  // ... existing component code, replace fetch() with window.tourkitAuth.authedFetch()
}
```

- [ ] **Step 2: Mở `wwwroot/pages/visa.jsx`, áp dụng pattern tương tự**

Mọi `fetch('/api/v1/visa/...')` → `window.tourkitAuth.authedFetch('/api/v1/visa/...')`. Login gate ở đầu component.

- [ ] **Step 3: Smoke test manual — start proxy + browser test**

```bash
dotnet run --project TourkitAiProxy.csproj --no-build
```

Mở browser `http://localhost:5080/mail`:
- Chưa login → thấy login gate "Vui lòng đăng nhập"
- Login (qua `/assistant`) → quay lại `/mail` → load không 401

Mở `http://localhost:5080/visa`:
- Tương tự — login gate hoặc load OK sau login

Kill: `taskkill //F //IM dotnet.exe`

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/mail.jsx wwwroot/pages/visa.jsx
git commit -m "frontend(mail+visa): authedFetch + login gate cho per-tenant scope"
```

---

## Task 13: Drop appsettings Mail:Gmail:* fallback config

**Files:**
- Modify: `appsettings.example.json`
- Modify: `appsettings.json`

- [ ] **Step 1: Mở `appsettings.example.json`, xóa block `Mail`**

Tìm block `"Mail": { "Gmail": { "Address": "...", "AppPassword": "..." } }` → xóa toàn bộ key `Mail` + dấu phẩy theo cú pháp JSON.

- [ ] **Step 2: Mở `appsettings.json` thật (nếu có), xóa block tương tự**

```bash
# Verify config trước khi xóa
grep -A5 '"Mail"' appsettings.json
```

Xóa block. Save.

- [ ] **Step 3: Verify GmailImapClient/MailAccountStore không còn đọc config Mail:Gmail:***

```bash
# Phải KHÔNG match (đã xóa code đọc config ở Task 5)
grep -rn 'Mail:Gmail' Services/ Endpoints/
# Expected: no match (hoặc chỉ comment cũ — xóa nếu có)
```

Nếu còn code đọc config: xóa.

- [ ] **Step 4: Build + smoke test**

```bash
dotnet build TourkitAiProxy.csproj
dotnet run --project TourkitAiProxy.csproj --no-build
```

Verify startup không log error về missing Mail config. Healthz vẫn 200. Kill.

- [ ] **Step 5: Commit**

```bash
git add appsettings.example.json appsettings.json
git commit -m "chore(config): drop Mail:Gmail:* fallback — multi-tenant chỉ qua DB per-tenant setup"
```

---

## Task 14: Two-tenant smoke test (manual + documented)

**Files:**
- Create: `docs/superpowers/notes/2026-06-09-multitenancy-smoke-test.md` (test report)

- [ ] **Step 1: Chuẩn bị 2 session token cho 2 tenant khác nhau**

Cần 2 user TourKit thật khác tenant. Nếu chưa có, hỏi admin TourKit cấp 2 account test.

Hoặc cách nhanh: mock — sửa `TkSessionStore.Get` tạm trả 2 session với TenantId="T1" và "T2" (revert sau test).

- [ ] **Step 2: Document test plan trong file mới**

```markdown
# Mail + Visa multi-tenancy smoke test report

Date: 2026-06-09. Tester: <name>. Build SHA: <sha>.

## Setup
- 2 TourKit accounts: userA (tenant T1), userB (tenant T2)
- DB rỗng (sau migration backup): SELECT COUNT(*) FROM dbo.MailAccounts/Mails/VisaAssessments = 0

## Test 1: Mail isolation
1. Login T1 → setup Gmail info@t1.com → sync → verify N email
2. Logout, login T2 → vào /mail → EXPECT empty (chưa setup, không thấy email T1)
3. T2 setup Gmail info@t2.com → sync → verify chỉ email T2
4. Login T1 lại → vào /mail → vẫn thấy đúng N email T1 (chưa bị đè creds)

## Test 2: Visa isolation
1. Login T1 → upload PDF → extract → score → verify 1 assessment
2. Logout, login T2 → vào /visa/assessments → EXPECT empty
3. T2 thử GET /visa/assessments/{T1-assessment-id} → EXPECT 404
4. T2 thử DELETE /visa/assessments/{T1-assessment-id} → EXPECT 404 (KHÔNG xóa được)

## Test 3: Anonymous access blocked
1. KHÔNG có X-Session-Id header:
   - GET /api/v1/mail → EXPECT 401
   - GET /api/v1/mail/account → EXPECT 401
   - GET /api/v1/visa/assessments → EXPECT 401
   - POST /api/v1/visa/assess → EXPECT 401

## Results
[Fill after running each test]
- Test 1: ✅ / ❌
- Test 2: ✅ / ❌
- Test 3: ✅ / ❌

## Issues found
[None if all pass; otherwise document]
```

- [ ] **Step 3: Run tests cho từng scenario, fill results**

Manual execute từng test ở Step 2. Verify từng check.

- [ ] **Step 4: Nếu có bug → fix trong commit riêng (không trong task này)**

Test này là verification. Nếu fail → tạo task fix riêng + retest.

- [ ] **Step 5: Commit test report**

```bash
mkdir -p docs/superpowers/notes
git add docs/superpowers/notes/2026-06-09-multitenancy-smoke-test.md
git commit -m "docs: smoke test report multi-tenant isolation Mail + Visa"
```

---

## Task 15: Cập nhật CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Mở CLAUDE.md, tìm section SmartMail AI**

Tìm bullet "**Source = Gmail IMAP via MailKit, NOT OAuth.**" — cập nhật để mention per-tenant:

Đổi đoạn cuối:
> "Creds resolved by `MailAccountStore`: persisted `data/mail-account.json` (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI → fallback `Mail:Gmail:Address`/`AppPassword` config or `MAIL_GMAIL_ADDRESS`/`MAIL_GMAIL_APP_PASSWORD` env."

thành:
> "Creds resolved by `MailAccountStore`: DB-backed `dbo.MailAccounts` per-tenant (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI per tenant. KHÔNG còn fallback config/env (đã drop từ commit multi-tenant fix 2026-06-09)."

- [ ] **Step 2: Tìm section storage Mail (file-backed `data/mails.json`)**

Đổi:
> "**Storage = file-backed `data/mails.json`** (`MailRepository`, lock-guarded, camelCase, `Filter`/`Counts` with diacritics-insensitive search). MVP placeholder — swap for DB to scale."

thành:
> "**Storage = SQL Server `dbo.Mails`** per-tenant scoped (`MailRepository`, composite PK `(TenantId, Id)`, index `IX_Mails_Tenant_Received` cho list/sort). Cross-tenant access trả null/404. KHÔNG fallback file — DB lỗi → 503."

- [ ] **Step 3: Tìm section Visa storage tương tự**

Sửa mọi mention "data/visa-assessments.json" → "dbo.VisaAssessments per-tenant". Sửa "data/visa-files/{assessmentId}" → "data/visa-files/{tenantId}/{assessmentId}".

- [ ] **Step 4: Cập nhật API table — đánh dấu auth required**

Trong API table, các endpoint Mail + Visa thêm note "(yêu cầu X-Session-Id)" nếu chưa rõ.

- [ ] **Step 5: Build verify markdown render OK**

```bash
git diff CLAUDE.md | head -100
```

Verify không break heading hierarchy, list markers.

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: cập nhật CLAUDE.md sau multi-tenant fix Mail + Visa"
```

---

## Self-Review

### 1. Spec coverage

| Spec section | Task implements |
|--------------|----------------|
| #1 DB schema 4 bảng | Task 3 (extend TourkitAiDb.SchemaSql) |
| #2 Repository signatures + ITenantContext | Task 4 (interface), Task 5-9 (repo rewrite) |
| #3 Endpoint changes (require session + tenant) | Task 10 (Mail), Task 11 (Visa) |
| #4 Migration helper backup | Task 1-2 (MultiTenantMigration + wire) |
| #4 Frontend update | Task 12 (mail.jsx + visa.jsx) |
| #4 Rollout phasing 5 phase | Mapped: Phase 1→Task 1-3, Phase 2→Task 5-9, Phase 3→Task 10-11, Phase 4→Task 12, Phase 5→Task 14 |
| #4 Drop appsettings Mail:Gmail:* | Task 13 |
| #5 Acceptance criteria (smoke 2 tenant) | Task 14 (documented test report) |
| #6 Risks rollback per phase | Test plan trong Task 14 |
| CLAUDE.md update | Task 15 |

Tất cả spec requirements đều có task. Không gap.

### 2. Placeholder scan

- Không có "TBD"/"TODO"/"implement later"
- Mọi code step có code block
- Test names cụ thể
- Commit messages cụ thể

### 3. Type consistency

- `tenantId` param ở mọi method (Task 5-11) consistent string type
- `MailRepository` method signatures khớp giữa Task 6 (rewrite) và Task 10 (call site)
- `VisaRepository` tương tự (Task 8 và Task 11)
- `MailAccountStore.Get/Set/IsConfigured/Signature` consistent với Task 5 + 10
- `IMailSource.FetchRecentAsync(string tenantId, int max, CancellationToken ct)` consistent Task 7 + 10
- `RequireSession` helper signature consistent Task 10 và Task 11

### 4. Notable design decision

Task 5-9 dùng `""` placeholder cho tenantId — pattern transitional để mỗi commit compile clean mà không phải sửa cả endpoint cùng lúc. Task 10-11 mới thay `""` → real tenant từ session. Đây là deliberate — giảm size mỗi commit, dễ review/revert.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-06-09-mail-visa-multitenancy.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — Tôi dispatch fresh subagent cho mỗi task, review giữa các task, fast iteration. Tốt cho plan này vì:
- 15 task dependency rõ (Task 1→2→...→15)
- Mỗi subagent context sạch → không drift giữa repo signature
- Tôi review code sau mỗi task → catch regression sớm (đặc biệt DB-backed code không có unit test)

**2. Inline Execution** — Tôi execute tasks ngay trong session này, batch với checkpoint. Phù hợp nếu anh muốn theo dõi real-time + can thiệp nhanh.

**Anh chọn approach nào?**
