# Mail + Visa multi-tenancy fix — Design Spec

> Trạng thái: **đã brainstorm xong, chờ chủ dự án review spec → writing-plans.** Chưa code.
> Ngày: 2026-06-09. **Security priority** — pause Phase 0 RESTful refactor (đang ở Task 2/15) để fix trước.

## Goal

Khắc phục bug nghiêm trọng về data isolation ở 2 feature Mail (SmartMail AI) + Visa (Visa Assessment). Hiện 2 feature dùng singleton state global (`data/mails.json`, `data/visa-assessments.json`, `data/mail-account.json`) → bất kỳ user nào login đều thấy data của tất cả tenant khác, bao gồm PII nhạy cảm (hộ chiếu, sao kê, email khách). Cần scope theo `TenantId` (mẫu Reviews / Chat / Tours đã có).

## Bug evidence (vì sao cần ưu tiên)

| Scenario user B login khác tenant | Hiện trạng |
|-----------------------------------|------------|
| User B vào `/mail` | Thấy **TOÀN BỘ email** của user A đã sync trước đó |
| User B vào `/visa/assessments` | Thấy **TOÀN BỘ visa assessment** của user A (kèm PII hộ chiếu/sao kê) |
| User B set Gmail của họ | **Đè** creds user A → user A mất quyền vào hộp thư |
| User B reply 1 email trong hộp thư T1 | SMTP gửi qua creds T2 → khách T1 nhận từ địa chỉ T2 (spam) |
| Anonymous gọi `GET /mail` | Trả data luôn (endpoint **KHÔNG check session**) |

Grep `sessions.Get` trong `MailEndpoints.cs` + `VisaEndpoints.cs` = 0 match → cả 2 không auth gate.

## So sánh với feature đã đúng

| Feature | Cách scope | Ref |
|---------|------------|-----|
| Reviews | DB `dbo.Reviews` có cột TenantId index, endpoint check session + truyền tenant | `eb81a5f`, `ddf7278` |
| Chat-Analytics | Cache key prefix `r\|{tenant}\|{question}` + session bắt buộc | hiện có |
| Tours (TenantStore) | Per-tenant key Redis/file | hiện có |
| **Mail + Visa** | **CHƯA scope** — global singleton | **Bug này fix** |

## Quyết định kiến trúc đã chốt (brainstorm)

| # | Quyết định |
|---|---|
| Ownership | **1 tenant = 1 Gmail account + 1 inbox + 1 Visa list shared cho mọi NV trong tenant đó**. Không per-user (YAGNI: inbox công ty là use case thực; per-user phức tạp hơn không cần). |
| Storage | **SQL Server PushDb** (instance shared như Reviews). Schema: 3 bảng Mail + 1 bảng Visa với composite PK (TenantId, Id). |
| Legacy data | **Backup → discard.** Move `data/{mails,mail-account,mail-sync,visa-assessments}.json` + `data/visa-files/` vào `data/legacy-backup/{ts}/` lúc startup migration. User re-config Gmail + re-sync. |
| `appsettings` fallback | **Drop hoàn toàn** `Mail:Gmail:Address/AppPassword` config. Dev local cũng setup từ UI như user thật. (YAGNI.) |
| Service signature | **Option B: pass `tenantId` qua parameter** (mẫu Reviews). Service không inject `ITenantContext` → giữ pure, dễ test ngoài request context. |
| DB fallback file | **KHÔNG fallback file** khi DB lỗi. Trả 503. Lý do: re-implement 2 storage = nợ kỹ thuật. Reviews có fallback chỉ vì legacy MVP. |

---

## #1 — DB schema (PushDb)

Tất cả tables ở PushDb shared. `MailRepository.InitAsync()` + `VisaRepository.InitAsync()` tạo schema lúc startup (fire-and-forget, mẫu `ReviewRepository.InitAsync` commit `eb81a5f`).

### `dbo.MailAccounts` (1 row / tenant)

| Column | Type | Note |
|--------|------|------|
| TenantId | nvarchar(64) | **PK** |
| Address | nvarchar(256) | Gmail address |
| AppPasswordEnc | nvarchar(512) | Crypton-encrypted |
| Signature | nvarchar(max) | NULL allowed |
| UpdatedAt | datetime2 | tracking |

### `dbo.Mails` (1 row / email per tenant)

| Column | Type | Note |
|--------|------|------|
| TenantId | nvarchar(64) | **PK part 1**, index |
| Id | nvarchar(256) | **PK part 2** (Message-Id) |
| FromName / FromEmail | nvarchar(256) | |
| Subject | nvarchar(1024) | |
| Body / BodyHtml | nvarchar(max) | BodyHtml NULL allowed |
| ReceivedAt | datetime2 | sort DESC |
| IsRead | bit | |
| Category | nvarchar(32) | NULL = chưa classify |
| Status | nvarchar(32) | moi/dang_xu_ly/da_phan_hoi/da_dong |
| AiSummary | nvarchar(max) | NULL allowed |
| DraftJson | nvarchar(max) | NULL, serialize MailDraft |
| **Index** | `IX_Mails_Tenant_Received` ON (TenantId, ReceivedAt DESC) | |

### `dbo.MailSyncState` (per tenant per address)

| Column | Type | Note |
|--------|------|------|
| TenantId | nvarchar(64) | **PK part 1** |
| Address | nvarchar(256) | **PK part 2** |
| UidValidity / LastUid | bigint | |
| UpdatedAt | datetime2 | |

### `dbo.VisaAssessments` (1 row / assessment per tenant)

| Column | Type | Note |
|--------|------|------|
| TenantId | nvarchar(64) | **PK part 1**, index |
| Id | nvarchar(64) | **PK part 2** (Guid) |
| ApplicantName | nvarchar(256) | |
| Country | nvarchar(64) | NULL allowed |
| Status | nvarchar(32) | extracted / scored |
| ExtractionJson | nvarchar(max) | serialize VisaExtraction |
| ResultJson | nvarchar(max) | NULL until scored, serialize VisaResult |
| FileCount | int | |
| FilesPurged | bit | |
| CreatedAt / UpdatedAt | datetime2 | |
| **Index** | `IX_VisaAssessments_Tenant_Created` ON (TenantId, CreatedAt DESC) | |

### Visa files (filesystem)

PII files (PDF hộ chiếu/sao kê) vẫn local disk, BLOB lớn không phù hợp DB. Đường dẫn đổi:
- Cũ: `data/visa-files/{assessmentId}/{file}`
- **Mới: `data/visa-files/{tenantId}/{assessmentId}/{file}`**

`VisaFileStore.Save/Delete/HasFiles` nhận thêm `tenantId` param. `Purge()` vẫn global (xóa file >7 ngày theo mtime).

---

## #2 — Repository signatures + `ITenantContext`

### `ITenantContext` interface mới (Phase 1 RESTful sẽ dùng nhiều, ở fix này chỉ optional)

```csharp
// Services/TourKit/ITenantContext.cs
namespace TourkitAiProxy.Services.TourKit;

public interface ITenantContext
{
    string TenantId { get; }              // throw nếu anonymous
    string? TryGetTenantId();             // null nếu anonymous
}
```

Implementation `HttpTenantContext`: đọc `X-Session-Id` header + `TkSessionStore.Get(sid)?.TenantId`. DI: `AddScoped<ITenantContext, HttpTenantContext>()`.

Ở fix này, `ITenantContext` chỉ tạo skeleton — chưa inject vào services. Services nhận `tenantId` qua parameter (Option B trong brainstorm).

### Repository signatures (4 file rewrite)

**`MailAccountStore`** (DB-backed thay file):
```csharp
(string Address, string AppPassword)? Get(string tenantId);
void Set(string tenantId, string address, string appPassword, string? signature);
bool IsConfigured(string tenantId);
string CurrentAddress(string tenantId);
string Signature(string tenantId);
```

**`MailRepository`** (DB-backed):
```csharp
MailItem? Get(string tenantId, string id);
bool Has(string tenantId, string id);
void Upsert(string tenantId, MailItem item);
bool SetStatus(string tenantId, string id, string status);
bool SetRead(string tenantId, string id, bool isRead = true);
bool SetDraft(string tenantId, string id, MailDraft draft, string status);
IReadOnlyList<MailItem> Filter(string tenantId, string? status, string? category, string? search);
MailCounts Counts(string tenantId);
Task InitAsync();   // mới — tạo schema lúc startup
```

**`MailSyncStore`** (DB-backed):
```csharp
SyncState? Get(string tenantId, string address);
void Set(string tenantId, string address, uint uidValidity, uint lastUid);
```

**`VisaRepository`** (DB-backed):
```csharp
VisaAssessment? Get(string tenantId, string id);
List<VisaAssessment> All(string tenantId);
void Save(string tenantId, VisaAssessment a);
bool Delete(string tenantId, string id);
Task InitAsync();   // mới
```

**`VisaFileStore`** (vẫn filesystem):
```csharp
string Save(string tenantId, string assessmentId, int index, string fileName, byte[] bytes);
void DeleteAssessment(string tenantId, string assessmentId);
bool HasFiles(string tenantId, string assessmentId);
void Purge();   // không đổi
```

### Service layer (Mail/Visa services)

KHÔNG đổi public API. Chỉ thread `tenantId` qua parameter khi gọi repo. Caller (endpoint) resolve 1 lần, pass xuống — mẫu Reviews.

Affected:
- `GmailImapClient.SyncAsync(string tenantId, ...)`
- `MailReplyService.DraftStreamAsync(string tenantId, ...)`, `ComposeNewStreamAsync(string tenantId, ...)`
- `MailClassifier.ClassifyAsync(...)` — không cần tenant (classify chỉ logic AI), repo upsert ở caller
- `VisaScoringService.ScoreAsync(...)` — không cần tenant (chỉ logic AI), repo save ở caller
- `VisaExtractionService.ExtractAsync(...)` — tương tự

---

## #3 — Endpoint changes

### Pattern auth (tạm thời, Phase 1 RESTful sẽ extract `AuthFilter`)

Helper duplicate ở `MailEndpoints` + `VisaEndpoints`:

```csharp
private static (string SessionId, string TenantId)? RequireSession(HttpContext ctx, TkSessionStore sessions)
{
    var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
        ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    var s = sessions.Get(sid);
    return s == null ? null : (sid!, s.TenantId);
}

// Usage trong handler:
var auth = RequireSession(ctx, sessions);
if (auth == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
var (_, tenantId) = auth.Value;
```

### MailEndpoints — 11 endpoint update

Mọi endpoint **bắt buộc** session. Anonymous → 401.

| Endpoint | Change |
|----------|--------|
| `GET /mail/account` | `account.Get(tenantId)` |
| `POST /mail/account` | `account.Set(tenantId, ...)` |
| `POST /mail/sync` | `GmailImapClient.SyncAsync(tenantId, ...)` + `mailRepo.Upsert(tenantId, ...)` |
| `GET /mail` | `repo.Filter(tenantId, status, cat, search)` |
| `GET /mail/{id}` | `repo.Get(tenantId, id)` → 404 nếu cross-tenant |
| `POST /mail/{id}/read` | `repo.SetRead(tenantId, id)` |
| `POST /mail/compose/draft` (SSE) | replyService nhận tenant |
| `POST /mail/compose/send` | `repo.Upsert(tenantId, ...)` |
| `POST /mail/{id}/reply/draft` (SSE) | lookup mail theo tenant |
| `POST /mail/{id}/reply/send` | tương tự |
| `PATCH /mail/{id}/status` | `repo.SetStatus(tenantId, id, ...)` |

### VisaEndpoints — 5 endpoint update

| Endpoint | Change |
|----------|--------|
| `POST /visa/assess` (upload + extract) | `fileStore.Save(tenantId, ...)`, `visaRepo.Save(tenantId, ...)` |
| `POST /visa/assess/{id}/score` | `visaRepo.Get(tenantId, id)` → 404 cross-tenant, save với tenant |
| `GET /visa/assessments` | `visaRepo.All(tenantId)` |
| `GET /visa/assessments/{id}` | `visaRepo.Get(tenantId, id)` |
| `DELETE /visa/assessments/{id}` | `visaRepo.Delete(tenantId, id)` + `fileStore.DeleteAssessment(tenantId, id)` |

### Security guarantee

- Cross-tenant access: dù biết `mailId`/`assessmentId` của tenant khác, query repo trả null → 404. Filesystem `data/visa-files/{tenant}/...` isolated.
- Direct file access: `data/` không bị `UseStaticFiles` serve (chỉ serve `wwwroot/`). OK.
- MailAccount overwrite: user B set Gmail → DB row mới `tenant=T2`, không động row T1.

### Background services

- `VisaFileStore.Purge()` — chạy startup, global, không cần tenant. Xóa file >7 ngày theo mtime.
- `IMailSource.SyncAsync` — chạy on-demand qua endpoint, không background. OK.

---

## #4 — Migration + rollout

### Startup migration helper

```csharp
// Services/Db/MultiTenantMigration.cs (new)
public static class MultiTenantMigration
{
    public static void Run(string dataDir, ILogger log)
    {
        var legacyFiles = new[] { "mails.json", "mail-account.json", "mail-sync.json", "visa-assessments.json" };
        var legacyFolders = new[] { "visa-files" };

        bool hasLegacy = legacyFiles.Any(f => File.Exists(Path.Combine(dataDir, f)))
            || (Directory.Exists(Path.Combine(dataDir, "visa-files"))
                && Directory.EnumerateFileSystemEntries(Path.Combine(dataDir, "visa-files")).Any());
        if (!hasLegacy) return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
        var backupRoot = Path.Combine(dataDir, "legacy-backup", ts);
        Directory.CreateDirectory(backupRoot);

        foreach (var f in legacyFiles)
        {
            var src = Path.Combine(dataDir, f);
            if (File.Exists(src)) File.Move(src, Path.Combine(backupRoot, f));
        }
        foreach (var d in legacyFolders)
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

Gọi từ `Program.cs` sau `app.Build()`, trước `app.Run()`:
```csharp
MultiTenantMigration.Run(Path.Combine(app.Environment.ContentRootPath, "data"),
    app.Services.GetRequiredService<ILogger<Program>>());
```

(Sync vì chỉ move file — không cần fire-and-forget.)

### Frontend update (cùng commit với endpoint rewrite)

- `wwwroot/pages/mail.jsx`: mọi fetch thêm `X-Session-Id` header. Check + dùng `window.tourkitAuth.authedFetch` nếu có (commit Reviews/Chat đã có helper); nếu chưa → tạo helper hoặc inline.
- `wwwroot/pages/visa.jsx`: tương tự.
- Login gate: hiện chỉ assistant.jsx + customers.jsx require login. Mail + Visa cần wire vào auth gate global.

### Rollout phases

| Phase | Scope | Effort | Risk |
|-------|-------|--------|------|
| 1 | DB schema init (`MailRepository.InitAsync`, `VisaRepository.InitAsync`) + `MultiTenantMigration` backup helper. Chưa tích hợp service. | ~1h, 1 commit | Low |
| 2 | Rewrite 4 repository (Account, Mail, MailSync, Visa) sang DB-backed accept `tenantId` param. `VisaFileStore` thêm tenant param. Service caller signatures break compile — fix theo. | ~3h, 1-2 commit | Medium |
| 3 | Endpoint update: require session + extract tenant + pass repo (11 mail + 5 visa endpoint) | ~2h, 1-2 commit | Medium |
| 4 | Frontend `mail.jsx` + `visa.jsx` authedFetch + login gate | ~1h, 1 commit | **High** (break nếu miss page) |
| 5 | Smoke test 2 tenant cùng login: verify isolation 100% | ~30min | Verification |

**Total:** ~7.5h, 5-7 commit. Breaking-but-controlled — mỗi phase build clean + test pass.

### Per-commit safety

- `dotnet build` clean
- `dotnet test` all pass (104+ hiện tại, Phase 0 trước đã thêm test JsonElementExtensions)
- Smoke 1 tenant: login → setup mail → sync → verify count
- Phase 5: 2 tenant smoke:
  1. Login T1 → setup `info@t1.com` → sync → verify N email
  2. Logout, login T2 → vào `/mail` → **empty**, KHÔNG thấy email T1
  3. T2 setup `info@t2.com` → sync → verify chỉ email T2
  4. Login T1 lại → vào `/mail` → vẫn thấy đúng email T1 (chưa bị đè)

---

## #5 — Acceptance criteria

- 4 bảng DB tạo đúng schema lúc startup (idempotent IF NOT EXISTS).
- `data/{mails,mail-account,mail-sync,visa-assessments}.json` + `data/visa-files/` được move vào `data/legacy-backup/{ts}/` lúc deploy lần đầu.
- Mọi endpoint Mail (11) + Visa (5) trả 401 khi anonymous gọi.
- Mọi repo method nhận `tenantId` param; query DB scope theo TenantId column.
- Visa file path = `data/visa-files/{tenantId}/{assessmentId}/`.
- `appsettings.json` không còn `Mail:Gmail:Address`/`AppPassword` (dọn config).
- Frontend `/mail` + `/visa` gửi `X-Session-Id` header, redirect login nếu 401.
- Smoke test 2 tenant pass: T1 không thấy data T2 và ngược lại.
- 104+ tests vẫn pass (existing) + thêm 4-6 test mới cho per-tenant query (vd `MailRepository_Get_returns_null_for_cross_tenant_id`).

## #6 — Risks + rollback

| Risk | Mitigation |
|------|-----------|
| DB schema init fail lúc deploy | InitAsync log warning + throw → 503 endpoint; admin fix manual (chạy SQL script ở backup file) |
| Migration backup fail (disk full) | Fail-safe: nếu Move fail → log error + ABORT startup. Tránh state nửa vời. |
| Frontend miss update 1 page → user thấy 401 spam | Manual test cả 2 page trước deploy. Có thể wrap helper `authedFetch` global. |
| Phase 2 break service caller compile | Build clean per-commit; nếu fail → revert commit |
| User T1 login sau deploy, thấy mail trống → panic | Banner UI: "Sau cập nhật bảo mật, vui lòng setup lại Gmail. Email cũ vẫn trong server nhưng cần tenant config." |
| Cross-tenant attack từ scripted client | 404 silent (không leak existence). Log warning ở repo nếu `Get(tenantA, id)` trả null nhưng `Get(*, id)` có row (detect cross-tenant probe). |

## #7 — Out of scope (defer)

- Per-user (NV) scope thay vì per-tenant — anh đã chốt YAGNI
- DB sharding theo tenant (1 DB / tenant) — quá phức tạp cho MVP, dùng row-level isolation
- Audit log mọi action per tenant (CreatedByUserId column) — chưa cần
- Role permission trong tenant (manager vs sale) — chưa cần
- Encrypt-at-rest cho mail body + visa extraction — Crypton chỉ apply cho App Password. Body PII không encrypt (DB-level TDE là responsibility infra)
- Multi-mailbox per tenant (vd 1 tenant có 3 Gmail account khác nhau) — YAGNI cho MVP
- Frontend "switch tenant" UI — login = 1 tenant, logout login khác để switch
