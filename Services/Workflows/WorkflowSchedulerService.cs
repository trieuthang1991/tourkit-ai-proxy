using System.Diagnostics;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// BackgroundService tick mỗi 60 giây: lấy workflow đang due → chạy song song.
///
/// <b>Concurrency note:</b> <c>SetNextRun</c> chạy NGAY khi tick consume (trước khi Task.Run)
/// → tick kế tiếp thấy <c>NextRunUtc &gt; now</c> → skip cho đến khi task finish.
///
/// <b>Auto-pause:</b> sau 5 fail liên tiếp → <c>AutoPause</c> → scheduler bỏ qua mãi cho đến
/// khi user "Bật lại" (UpsertConfig enabled=true → reset ConsecutiveFailures + xoá PausedReason).
///
/// <b>TODO (multi-instance):</b> khi scale ngang cần leader election hoặc partition theo
/// <c>hash(tenantId)</c> để tránh cùng workflow chạy đồng thời trên 2 instance.
/// </summary>
public class WorkflowSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RunTimeout      = TimeSpan.FromMinutes(5);
    private const int AutoPauseThreshold = 5;

    private readonly WorkflowRepository _repo;
    private readonly WorkflowRegistry _registry;
    private readonly ILogger<WorkflowSchedulerService> _log;

    public WorkflowSchedulerService(
        WorkflowRepository repo,
        WorkflowRegistry registry,
        ILogger<WorkflowSchedulerService> log)
    {
        _repo = repo; _registry = registry; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[Scheduler] Khởi động — tick mỗi {Sec}s", TickInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ProcessTickAsync(stoppingToken); }
                catch (Exception ex) { _log.LogWarning(ex, "[Scheduler] Tick lỗi (không thoát vòng lặp)"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown bình thường */ }

        _log.LogInformation("[Scheduler] Dừng");
    }

    private async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var due = _repo.ListDue(now);
        _log.LogInformation("[Scheduler] tick — {N} workflow due", due.Count);

        foreach (var cfg in due)
        {
            var wf = _registry.Resolve(cfg.WorkflowType);
            if (wf == null)
            {
                _log.LogWarning("[Scheduler] Unknown workflow type '{Wt}' — skip và setNextRun", cfg.WorkflowType);
                _repo.SetNextRun(cfg.TenantId, cfg.Username, cfg.WorkflowType,
                    now.AddMinutes(cfg.IntervalMinutes));
                continue;
            }

            // Cập nhật NextRunUtc NGAY TRƯỚC Task.Run để tick kế không re-fire cùng scope.
            _repo.SetNextRun(cfg.TenantId, cfg.Username, cfg.WorkflowType,
                now.AddMinutes(cfg.IntervalMinutes));

            // Capture để dùng trong lambda (closure)
            var tenant = cfg.TenantId;
            var user = cfg.Username;
            var type = cfg.WorkflowType;
            var interval = cfg.IntervalMinutes;

            _ = Task.Run(async () =>
            {
                await RunOneAsync(wf, tenant, user, type, "scheduled", stoppingToken);
            }, stoppingToken);
        }
    }

    /// Chạy 1 workflow (dùng chung cả scheduled lẫn manual trigger).
    internal async Task RunOneAsync(
        IScheduledWorkflow wf,
        string tenantId, string username, string type,
        string triggerKind,
        CancellationToken outerCt)
    {
        var sw = Stopwatch.StartNew();
        var startedUtc = DateTime.UtcNow;
        WorkflowRunResult result;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(RunTimeout);
            result = await wf.RunAsync(tenantId, username, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new WorkflowRunResult(Ok: false, Summary: null, Error: "Vượt quá thời gian 5 phút");
        }
        catch (Exception ex)
        {
            result = new WorkflowRunResult(Ok: false, Summary: null, Error: ex.Message);
        }
        finally
        {
            sw.Stop();
        }

        var finishedUtc = DateTime.UtcNow;
        var status = result.Ok ? "ok" : "failed";
        _log.LogInformation("[Workflow] {Type} tenant={T} user={U} trigger={Tr} ok={Ok} dur={Ms}ms",
            type, tenantId, username, triggerKind, result.Ok, sw.ElapsedMilliseconds);

        // Ghi lịch sử run
        try
        {
            _repo.AppendRun(tenantId, username, type, triggerKind,
                startedUtc, finishedUtc, status, result.Summary, result.Error, (int)sw.ElapsedMilliseconds);
            _repo.UpdateLastRun(tenantId, username, type, finishedUtc, status, result.Summary);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Scheduler] AppendRun lỗi (không thoát vòng lặp)");
        }

        // Failures tracking + auto-pause
        try
        {
            if (result.Ok)
            {
                _repo.ResetFailures(tenantId, username, type);
            }
            else
            {
                // QuotaExhaustedException xuyên qua dưới dạng lỗi thông thường — xử lý như fail bình thường.
                var newCount = _repo.IncrementFailures(tenantId, username, type);
                if (newCount >= AutoPauseThreshold)
                {
                    var reason = result.Error ?? $"{AutoPauseThreshold} lần thất bại liên tiếp";
                    _repo.AutoPause(tenantId, username, type, reason);
                    _log.LogWarning("[Scheduler] AutoPause {T}/{U}/{Wt}: {R}", tenantId, username, type, reason);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Scheduler] Failures tracking lỗi (không thoát vòng lặp)");
        }
    }
}
