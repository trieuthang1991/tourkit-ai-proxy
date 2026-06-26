# User Workflows — cấu hình lịch chạy AI tự động

**Ngày:** 2026-06-26
**Trạng thái:** Approved (brainstorm)
**Tác giả:** Claude (theo brief CEO)

## Mục tiêu

Cho user trong tenant cấu hình các tác vụ AI chạy tự động theo lịch (interval). V1 chỉ có **1 workflow built-in**: tự động đồng bộ + phân loại email Gmail. Framework đủ chỗ thêm workflow mới về sau (mỗi workflow = 1 class implement `IScheduledWorkflow`) mà không đụng scheduler / UI / schema.

## Decision matrix (đã chốt)

| Khía cạnh | Quyết định | Lý do |
|---|---|---|
| Loại workflow v1 | Chỉ `mail-auto-sync` | YAGNI; framework mở rộng dễ |
| Phạm vi config | Per-(Tenant, Username) cho Mail | Khớp `MailAccountStore` per-user hiện tại — mỗi user có Gmail riêng |
| Workflow tương lai | Per-tenant không phân quyền nội bộ | Schema dùng `Username=''` cho tenant-wide |
| Format lịch | Interval dropdown 5/10/15/30/60 phút | Đủ 95% use-case, cron để dành (YAGNI) |
| Error handling | Auto-pause sau **5 fail liên tiếp** + badge đỏ + "Bật lại" 1 click | Tránh đốt quota khi App Password hết hạn |
| Run log retention | Lưu 100 run / scope / workflow, UI show 20 gần nhất | Đủ debug, không phình DB |
| Notification | Inline trên trang workflow (status pill + last run + error) | KHÔNG toast / push / email-out — out of scope |
| Manual trigger | Có nút "▶ Chạy ngay" — đi qua đúng pipeline scheduler | User test config nhanh |
| Scheduler tick | `BackgroundService` mỗi **60s** | Đủ chính xác cho interval ≥ 5 phút |
| Multi-instance | 1 instance — chưa khóa SQL | TODO: khi scale cần leader election hoặc partition by `hash(tenantId)` |
| AI quota | `QuotaExhaustedException` propagate → tính là fail → đếm vào 5-strike | Không retry vô nghĩa khi quota hết |
| Per-run timeout | 5 phút | Lớn hơn IMAP fetch + classify nhiều mail |

## Database (thêm vào `Services/Db/TourkitAiDb.cs` `SchemaSql`)

```sql
IF OBJECT_ID('dbo.UserWorkflows', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserWorkflows (
        TenantId            NVARCHAR(64)   NOT NULL,
        Username            NVARCHAR(120)  NOT NULL,        -- '' = tenant-wide
        WorkflowType        NVARCHAR(64)   NOT NULL,
        Enabled             BIT            NOT NULL CONSTRAINT DF_UserWorkflows_Enabled DEFAULT 0,
        IntervalMinutes     INT            NOT NULL CONSTRAINT DF_UserWorkflows_Interval DEFAULT 15,
        ConsecutiveFailures INT            NOT NULL CONSTRAINT DF_UserWorkflows_Failures DEFAULT 0,
        PausedReason        NVARCHAR(500)  NULL,
        NextRunUtc          DATETIME2      NULL,
        LastRunUtc          DATETIME2      NULL,
        LastRunStatus       NVARCHAR(16)   NULL,
        LastRunSummary      NVARCHAR(MAX)  NULL,
        UpdatedBy           NVARCHAR(120)  NULL,
        UpdatedAtUtc        DATETIME2      NOT NULL CONSTRAINT DF_UserWorkflows_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UserWorkflows PRIMARY KEY (TenantId, Username, WorkflowType)
    );
END;

IF OBJECT_ID('dbo.WorkflowRuns', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WorkflowRuns (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        TenantId        NVARCHAR(64)   NOT NULL,
        Username        NVARCHAR(120)  NOT NULL,
        WorkflowType    NVARCHAR(64)   NOT NULL,
        TriggerKind     NVARCHAR(16)   NOT NULL,           -- 'scheduled' | 'manual'
        StartedUtc      DATETIME2      NOT NULL,
        FinishedUtc     DATETIME2      NULL,
        Status          NVARCHAR(16)   NOT NULL,           -- 'ok' | 'failed'
        Summary         NVARCHAR(MAX)  NULL,               -- JSON {fetched, classified, skipped}
        Error           NVARCHAR(1000) NULL,
        DurationMs      INT            NULL
    );
    CREATE INDEX IX_WorkflowRuns_Scope_Started
      ON dbo.WorkflowRuns(TenantId, Username, WorkflowType, StartedUtc DESC);
END;
```

**Pruning:** sau mỗi `INSERT INTO WorkflowRuns`, repo chạy thêm 1 `DELETE` xóa run cũ > 100 cho cùng scope (CTE + `ROW_NUMBER() OVER (... ORDER BY StartedUtc DESC)`).

## Backend

### Cấu trúc thư mục

```
Services/Workflows/
  IScheduledWorkflow.cs        # interface + WorkflowRunResult record
  WorkflowRegistry.cs          # DI singleton, IEnumerable<IScheduledWorkflow> → Dictionary
  WorkflowRepository.cs        # Dapper CRUD: Upsert, ListForScope, ListDue, AppendRun, RecentRuns, ResetFailures, IncrementFailures, AutoPause, SetNextRun
  WorkflowSchedulerService.cs  # BackgroundService — tick 60s
  MailAutoSyncWorkflow.cs      # IScheduledWorkflow.Type = "mail-auto-sync"
  MailSyncService.cs           # SHARED — extract logic từ MailEndpoints POST /mail/sync
Endpoints/
  WorkflowEndpoints.cs         # /api/v1/workflows/*
```

### Interface

```csharp
public interface IScheduledWorkflow
{
    string Type { get; }                 // "mail-auto-sync"
    string Label { get; }                // "Tự động đồng bộ Gmail"
    string Description { get; }          // "Kéo email mới từ Gmail + AI phân loại..."
    WorkflowScope Scope { get; }         // PerUser | PerTenant
    Task<WorkflowRunResult> RunAsync(string tenantId, string username, CancellationToken ct);
}

public enum WorkflowScope { PerUser, PerTenant }

public record WorkflowRunResult(bool Ok, string? Summary, string? Error);
```

`Scope = PerUser` → endpoint resolve `username` từ session.
`Scope = PerTenant` → workflow ignore `username` (caller pass `""`).

### Scheduler logic (`WorkflowSchedulerService.ExecuteAsync`)

```
loop until stoppingToken cancel:
    sleep 60s
    due = repo.ListDue(now)          // Enabled=1 AND PausedReason IS NULL AND (NextRunUtc IS NULL OR NextRunUtc <= now)
    foreach (cfg in due):
        wf = registry.Resolve(cfg.WorkflowType)
        if (wf == null) {
            log warn "Unknown workflow type"
            repo.SetNextRun(scope, now + IntervalMinutes)  // skip để khỏi lặp
            continue
        }
        _ = Task.Run(async () => {
            sw = Stopwatch.StartNew()
            startedUtc = DateTime.UtcNow
            try {
                using cts = LinkedTokenSource(stoppingToken).CancelAfter(5min)
                result = await wf.RunAsync(cfg.TenantId, cfg.Username, cts.Token)
                repo.AppendRun(scope, "scheduled", startedUtc, sw.Elapsed,
                    ok: result.Ok, summary: result.Summary, error: result.Error)
                if (result.Ok) repo.ResetFailures(scope)
                else {
                    newCount = repo.IncrementFailures(scope)
                    if (newCount >= 5) repo.AutoPause(scope, reason: result.Error ?? "5 lần thất bại liên tiếp")
                }
            } catch (OperationCanceledException) {
                repo.AppendRun(... ok:false, error:"Vượt quá thời gian 5 phút")
                handle như fail
            } catch (Exception ex) {
                repo.AppendRun(... ok:false, error:ex.Message)
                handle như fail
            } finally {
                repo.SetNextRun(scope, now + IntervalMinutes)
            }
        })
```

**Concurrency note:** nhiều workflow chạy song song qua `Task.Run`. Cùng `(tenant, user, type)` không bao giờ trùng vì `SetNextRun` chạy ngay khi tick consume; tick kế tiếp `NextRunUtc > now` → skip cho đến khi finish + SetNextRun mới.

### Manual trigger

`POST /api/v1/workflows/{type}/run-now` → giống logic scheduler nhưng:
- `TriggerKind = "manual"`
- BỎ qua `NextRunUtc` check (chạy ngay cả khi chưa due)
- VẪN đi qua failures tracking + auto-pause (consistency)
- Trả response sau khi finish (synchronous, không fire-and-forget) — UI hiện "Đang chạy..." trước, đợi response

### Mail sync extract

**Hiện trạng** (`MailEndpoints.MapPost("/mail/sync")`): inline ~80 dòng — IMAP fetch → loop classify → save. Trả `{items, counts, classified}`.

**Refactor:**

```csharp
// Services/Mail/MailSyncService.cs
public class MailSyncService
{
    public MailSyncService(IMailSource source, MailRepository repo, MailClassifier classifier,
        IWorkflowTraceAccessor trace, ILogger<MailSyncService> log) { ... }

    public async Task<MailSyncResult> RunAsync(
        string tenantId, string username, int max, CancellationToken ct) { ... }
}

public record MailSyncResult(int Fetched, int Classified, int Skipped);
```

`MailEndpoints` đổi thành 1 dòng: `await sync.RunAsync(tenant, user, fetchCap, ctx.RequestAborted)` rồi shape lại response cũ (kèm `items` để khỏi break frontend hiện tại).

`MailAutoSyncWorkflow.RunAsync` dùng `max = 100`:
- Service throw `QuotaExhaustedException` → bubble lên scheduler → fail run, đếm strike
- Service throw bất kỳ lỗi nào khác (IMAP timeout, auth fail) → cũng fail run
- Khi `MailAccountStore.Get(tenant, user)` null trong `MailSyncService` → throw `InvalidOperationException("Chưa cấu hình tài khoản Gmail")` → fail run

### API

| Method | Path | Body / Response |
|---|---|---|
| GET | `/api/v1/workflows` | `{ items: [{ type, label, description, scope, enabled, intervalMinutes, consecutiveFailures, pausedReason, nextRunUtc, lastRunUtc, lastRunStatus, lastRunSummary, updatedBy }] }` |
| PUT | `/api/v1/workflows/{type}` | Body `{ enabled, intervalMinutes }` → upsert; khi `enabled=true` & `pausedReason!=null` → reset failures + clear pausedReason (= user bật lại) |
| POST | `/api/v1/workflows/{type}/run-now` | Trigger 1 lần ngoài lịch → trả `{ ok, summary, error, durationMs }` sau khi finish |
| GET | `/api/v1/workflows/{type}/runs?limit=20` | `{ items: [{ id, triggerKind, startedUtc, finishedUtc, status, summary, error, durationMs }] }` |

Tất cả require `X-Session-Id` → `ITenantContext` resolve `(TenantId, Username)`. Workflow `Scope == PerTenant` → repo dùng `Username=''`.

### DI register (`Program.cs`)

```csharp
builder.Services.AddSingleton<WorkflowRepository>();
builder.Services.AddSingleton<WorkflowRegistry>();
builder.Services.AddSingleton<MailSyncService>();
builder.Services.AddSingleton<IScheduledWorkflow, MailAutoSyncWorkflow>();
builder.Services.AddHostedService<WorkflowSchedulerService>();
// ...
app.MapWorkflowEndpoints();
```

## Frontend

**Route mới:** `/workflows` — `wwwroot/pages/workflows.jsx`.

**Nav entry** (`wwwroot/app.jsx`): thêm `<Link to="/workflows">⚙️ Tự động</Link>` cạnh các link feature khác.

**Layout** — dọc, mỗi workflow 1 card:

```
┌────────────────────────────────────────────────┐
│ 📧 Tự động đồng bộ Gmail        [● Đang chạy] │  ← pill: enabled/disabled/paused
│ Kéo email mới từ Gmail + AI phân loại          │
│ ──────────────────────────────────────────────│
│ Trạng thái: [▢ On]                             │
│ Tần suất:   [Mỗi 15 phút ▾]                    │
│                                                │
│ Lần chạy cuối:  2 phút trước · ✓ 3 mail mới    │
│ Lần kế tiếp:    sau 13 phút                    │
│                                                │
│ [▶ Chạy ngay]   [📜 20 lần gần nhất ▾]         │
└────────────────────────────────────────────────┘
```

**Trạng thái paused:** card có viền đỏ + banner trên `"⚠ Đã tạm dừng: {pausedReason}. [Bật lại]"`. Click "Bật lại" = PUT `{enabled:true, intervalMinutes:<current>}` — backend clear `pausedReason` + reset failures.

**Run history (collapsible):** bảng 5 cột — Thời gian / Trigger (▶scheduled / 🖱manual) / Trạng thái (✓/✗) / Tóm tắt / Thời lượng. Click row failed → expand show `error` text.

**Reuse:** dùng style `.quota-table` / `.quota-card` đã có ở admin để keep design ngôn ngữ thống nhất.

## Workflow built-in: `mail-auto-sync`

| Field | Giá trị |
|---|---|
| `Type` | `"mail-auto-sync"` |
| `Label` | `"Tự động đồng bộ Gmail"` |
| `Description` | `"Kéo email mới từ Gmail, AI phân loại + đặt nhãn 6 nhóm (hỏi/đặt tour, báo giá, khiếu nại...)"` |
| `Scope` | `PerUser` |
| `RunAsync` body | Gọi `MailSyncService.RunAsync(tenantId, username, max:100, ct)`, format `Summary = JsonSerializer.Serialize(new { fetched, classified, skipped })` |
| Fail cases | Gmail chưa setup → `"Chưa cấu hình tài khoản Gmail"`; IMAP timeout → exception message; quota hết → `"Hết quota AI"`; sau 5 fail → auto-pause |

## Observability

- `_log.LogInformation("[Scheduler] tick — {N} workflow due", n)` mỗi tick
- `_log.LogInformation("[Workflow] {Type} tenant={T} user={U} trigger={Tr} ok={Ok} dur={Ms}ms", ...)` mỗi run
- Quota: scheduler catch `QuotaExhaustedException` riêng → `Error: "Hết quota AI"` (không stack trace)

## Test plan (manual)

1. Login → vào `/workflows` → thấy card Mail, status "Off"
2. Setup Gmail ở `/mail` (App Password) trước
3. Bật toggle + chọn "Mỗi 5 phút" → save → status "On", nextRun ~5min
4. Click "▶ Chạy ngay" → đợi 1-30s → toast "Hoàn thành: X mail mới"
5. Mở "20 lần gần nhất" → thấy 1 row `manual / ✓`
6. Đợi 5 phút → scheduler chạy 1 row `scheduled / ✓` (refresh trang để thấy)
7. Đổi App Password thành rác → 5 lần chạy → tự pause, badge đỏ + reason "Authentication failed..."
8. Click "Bật lại" → reset failures, run lại bình thường

## Out of scope (v1)

- Push notification / toast cross-page / email-out kết quả run
- Cron expression / khung giờ chạy ("chỉ 7-22h")
- Multi-instance leader election (đặt note TODO trong scheduler)
- Workflow chain (output A → input B)
- Workflow khác ngoài Mail (Re-score top KH / Cảnh báo deal / Báo cáo CRM ngày)
- Phân quyền nội bộ tenant (ai cũng sửa được)
- Per-tenant scope cho Mail (giữ per-user)

## Implementation order

1. SQL schema vào `TourkitAiDb.cs` + update `docs/database-schema.md`
2. `Services/Workflows/IScheduledWorkflow.cs` + `WorkflowRunResult`
3. `Services/Workflows/WorkflowRepository.cs` (Dapper CRUD + pruning)
4. `Services/Workflows/WorkflowRegistry.cs`
5. Refactor `MailEndpoints` POST `/mail/sync` → extract `Services/Mail/MailSyncService.cs`, build pass, response shape giữ nguyên
6. `Services/Workflows/MailAutoSyncWorkflow.cs`
7. `Services/Workflows/WorkflowSchedulerService.cs` (BackgroundService)
8. `Endpoints/WorkflowEndpoints.cs` + DI register trong `Program.cs`
9. `wwwroot/pages/workflows.jsx` + nav entry trong `app.jsx`
10. Smoke test theo Test plan ở trên
11. Update `CLAUDE.md` (API table + folder layout + Conventions)
12. Commit + push feature branch
