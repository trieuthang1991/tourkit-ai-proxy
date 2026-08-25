using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Dapper CRUD cho dbo.UserWorkflows + dbo.WorkflowRuns.
/// Tất cả method đều idempotent / safe khi gọi nhiều lần.
/// </summary>
public class WorkflowRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<WorkflowRepository> _log;

    public WorkflowRepository(TourkitAiDb db, ILogger<WorkflowRepository> log)
    {
        _db = db; _log = log;
    }

    // ── Row types ────────────────────────────────────────────────────────────────

    public sealed class WorkflowConfigRow
    {
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string WorkflowType { get; set; } = "";
        public bool Enabled { get; set; }
        public int IntervalMinutes { get; set; } = 15;
        public int ConsecutiveFailures { get; set; }
        public string? PausedReason { get; set; }
        public DateTime? NextRunUtc { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string? LastRunStatus { get; set; }
        public string? LastRunSummary { get; set; }
        public string? OptionsJson { get; set; }   // điều kiện/option ĐỘNG per-workflow (JSON)
        public string? UpdatedBy { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class DueWorkflowRow
    {
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string WorkflowType { get; set; } = "";
        public int IntervalMinutes { get; set; } = 15;
        public string? OptionsJson { get; set; }   // truyền xuống workflow.RunAsync
    }

    public sealed class WorkflowRunRow
    {
        public long Id { get; set; }
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string WorkflowType { get; set; } = "";
        public string TriggerKind { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Status { get; set; } = "";
        public string? Summary { get; set; }
        public string? Error { get; set; }
        public int? DurationMs { get; set; }
    }

    // ── Config CRUD ──────────────────────────────────────────────────────────────

    /// Lấy config 1 workflow của (tenant, user, type). null nếu chưa tồn tại.
    public WorkflowConfigRow? Get(string tenantId, string username, string type)
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<WorkflowConfigRow>(
            @"SELECT * FROM dbo.UserWorkflows
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt",
            new { t = tenantId, u = username, wt = type });
    }

    /// List mọi config của (tenant, user). Trả cả tenant-wide (Username='').
    public IReadOnlyList<WorkflowConfigRow> ListForScope(string tenantId, string username)
    {
        using var c = _db.Open();
        return c.Query<WorkflowConfigRow>(
            @"SELECT * FROM dbo.UserWorkflows
              WHERE TenantId=@t AND (Username=@u OR Username='')
              ORDER BY WorkflowType",
            new { t = tenantId, u = username }).AsList();
    }

    /// Upsert config. Khi enabled=true và có PausedReason → reset failures + xoá PausedReason ("Bật lại").
    /// optionsJson = null → GIỮ NGUYÊN options cũ (COALESCE); truyền chuỗi để cập nhật.
    public void UpsertConfig(string tenantId, string username, string type,
        bool enabled, int intervalMinutes, string updatedBy, string? optionsJson = null)
    {
        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.UserWorkflows AS T
USING (SELECT @t AS TenantId, @u AS Username, @wt AS WorkflowType) AS S
   ON T.TenantId=S.TenantId AND T.Username=S.Username AND T.WorkflowType=S.WorkflowType
WHEN MATCHED THEN
    UPDATE SET
        Enabled          = @en,
        IntervalMinutes  = @iv,
        OptionsJson      = COALESCE(@opts, OptionsJson),
        UpdatedBy        = @by,
        UpdatedAtUtc     = SYSUTCDATETIME(),
        -- Khi bật lại (enabled=true) → reset failures + clear PausedReason
        ConsecutiveFailures = CASE WHEN @en=1 THEN 0 ELSE ConsecutiveFailures END,
        PausedReason        = CASE WHEN @en=1 THEN NULL ELSE PausedReason END
WHEN NOT MATCHED THEN
    INSERT (TenantId, Username, WorkflowType, Enabled, IntervalMinutes, OptionsJson, UpdatedBy, UpdatedAtUtc)
    VALUES (@t, @u, @wt, @en, @iv, @opts, @by, SYSUTCDATETIME());",
            new { t = tenantId, u = username, wt = type, en = enabled, iv = intervalMinutes, opts = optionsJson, by = updatedBy });
        _log.LogInformation("[WorkflowRepo] Upsert tenant={T} user={U} type={Wt} enabled={En} interval={Iv}",
            tenantId, username, type, enabled, intervalMinutes);
    }

    // ── Scheduler helpers ────────────────────────────────────────────────────────

    /// List workflow đang due (Enabled=1, PausedReason IS NULL, NextRunUtc <= now hoặc NULL).
    public IReadOnlyList<DueWorkflowRow> ListDue(DateTime nowUtc)
    {
        using var c = _db.Open();
        return c.Query<DueWorkflowRow>(
            @"SELECT TenantId, Username, WorkflowType, IntervalMinutes, OptionsJson
              FROM dbo.UserWorkflows
              WHERE Enabled=1 AND PausedReason IS NULL
                AND (NextRunUtc IS NULL OR NextRunUtc <= @now)",
            new { now = nowUtc }).AsList();
    }

    /// Cập nhật NextRunUtc cho 1 scope. Gọi ngay khi tick consume để tránh re-fire trong tick kế.
    public void SetNextRun(string tenantId, string username, string type, DateTime nextUtc)
    {
        using var c = _db.Open();
        c.Execute(
            @"UPDATE dbo.UserWorkflows SET NextRunUtc=@next
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt",
            new { t = tenantId, u = username, wt = type, next = nextUtc });
    }

    /// Lưu kết quả chạy vào WorkflowRuns + prune > 100 rows cho cùng scope.
    public void AppendRun(string tenantId, string username, string type,
        string triggerKind, DateTime startedUtc, DateTime finishedUtc,
        string status, string? summary, string? error, int durationMs)
    {
        using var c = _db.Open();
        // Truncate error để khỏi tràn NVARCHAR(1000)
        var errTrunc = error?.Length > 990 ? error[..990] + "…" : error;
        c.Execute(@"
INSERT INTO dbo.WorkflowRuns (TenantId, Username, WorkflowType, TriggerKind, StartedUtc, FinishedUtc, Status, Summary, Error, DurationMs)
VALUES (@t, @u, @wt, @tk, @s, @f, @st, @sum, @err, @dur);

-- Prune: giữ 100 run gần nhất cho cùng scope
WITH x AS (
    SELECT Id,
           ROW_NUMBER() OVER (PARTITION BY TenantId, Username, WorkflowType ORDER BY StartedUtc DESC) rn
    FROM dbo.WorkflowRuns
    WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt
)
DELETE FROM x WHERE rn > 100;",
            new
            {
                t = tenantId, u = username, wt = type, tk = triggerKind,
                s = startedUtc, f = finishedUtc, st = status,
                sum = summary, err = errTrunc, dur = durationMs
            });
    }

    /// Lấy N run gần nhất của 1 scope.
    public IReadOnlyList<WorkflowRunRow> RecentRuns(string tenantId, string username, string type, int limit = 20)
    {
        using var c = _db.Open();
        return c.Query<WorkflowRunRow>(
            @"SELECT TOP (@lim) *
              FROM dbo.WorkflowRuns
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt
              ORDER BY StartedUtc DESC",
            new { t = tenantId, u = username, wt = type, lim = limit }).AsList();
    }

    /// Tăng ConsecutiveFailures lên 1. Trả giá trị mới sau UPDATE.
    public int IncrementFailures(string tenantId, string username, string type)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>(
            @"UPDATE dbo.UserWorkflows
              SET ConsecutiveFailures = ConsecutiveFailures + 1
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt;
              SELECT ConsecutiveFailures FROM dbo.UserWorkflows
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt;",
            new { t = tenantId, u = username, wt = type });
    }

    /// Reset ConsecutiveFailures = 0, xoá PausedReason (sau run thành công).
    public void ResetFailures(string tenantId, string username, string type)
    {
        using var c = _db.Open();
        c.Execute(
            @"UPDATE dbo.UserWorkflows
              SET ConsecutiveFailures=0, PausedReason=NULL
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt",
            new { t = tenantId, u = username, wt = type });
    }

    /// Auto-pause: Enabled=0, PausedReason=@reason (sau 5 fail liên tiếp).
    public void AutoPause(string tenantId, string username, string type, string reason)
    {
        using var c = _db.Open();
        c.Execute(
            @"UPDATE dbo.UserWorkflows
              SET Enabled=0, PausedReason=@r
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt",
            new { t = tenantId, u = username, wt = type, r = reason });
        _log.LogWarning("[WorkflowRepo] AutoPause tenant={T} user={U} type={Wt}: {R}", tenantId, username, type, reason);
    }

    /// Cập nhật LastRunUtc + LastRunStatus + LastRunSummary vào config row (cache nhanh cho UI).
    public void UpdateLastRun(string tenantId, string username, string type,
        DateTime lastRunUtc, string status, string? summary)
    {
        using var c = _db.Open();
        c.Execute(
            @"UPDATE dbo.UserWorkflows
              SET LastRunUtc=@ts, LastRunStatus=@st, LastRunSummary=@sum
              WHERE TenantId=@t AND Username=@u AND WorkflowType=@wt",
            new { t = tenantId, u = username, wt = type, ts = lastRunUtc, st = status, sum = summary });
    }
}
