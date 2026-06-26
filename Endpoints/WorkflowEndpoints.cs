using System.Diagnostics;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflows;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// User Workflows — cấu hình lịch chạy AI tự động per-(tenant, username).
///   GET    /api/v1/workflows                   — danh sách workflow + trạng thái
///   PUT    /api/v1/workflows/{type}             — bật/tắt + đổi interval (upsert)
///   POST   /api/v1/workflows/{type}/run-now     — chạy ngay 1 lần (synchronous)
///   GET    /api/v1/workflows/{type}/runs        — lịch sử N run gần nhất
///
/// Tất cả require X-Session-Id header → resolve (TenantId, Username).
/// </summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── GET /workflows ─── danh sách + trạng thái ───────────────────────────
        v1.MapGet("/workflows", (
            HttpContext ctx,
            WorkflowRegistry registry,
            WorkflowRepository repo,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            // Lấy toàn bộ config đã lưu cho (tenant, user)
            var saved = repo.ListForScope(tenant, user)
                .ToDictionary(r => r.WorkflowType, StringComparer.OrdinalIgnoreCase);

            // Merge với catalog đã đăng ký (để hiện cả workflow chưa có config)
            var items = registry.All().Select(wf =>
            {
                var scopeUser = wf.Scope == WorkflowScope.PerUser ? user : "";
                var row = saved.TryGetValue(wf.Type, out var r) ? r : null;
                return new
                {
                    type = wf.Type,
                    label = wf.Label,
                    description = wf.Description,
                    scope = wf.Scope.ToString(),
                    enabled = row?.Enabled ?? false,
                    intervalMinutes = row?.IntervalMinutes ?? 15,
                    consecutiveFailures = row?.ConsecutiveFailures ?? 0,
                    pausedReason = row?.PausedReason,
                    nextRunUtc = row?.NextRunUtc,
                    lastRunUtc = row?.LastRunUtc,
                    lastRunStatus = row?.LastRunStatus,
                    lastRunSummary = row?.LastRunSummary,
                    updatedBy = row?.UpdatedBy,
                    updatedAtUtc = row?.UpdatedAtUtc
                };
            }).ToList();

            return Results.Json(new { items });
        });

        // ─── PUT /workflows/{type} ─── upsert config ─────────────────────────────
        v1.MapPut("/workflows/{type}", (
            string type,
            WorkflowConfigRequest req,
            HttpContext ctx,
            WorkflowRegistry registry,
            WorkflowRepository repo,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            var wf = registry.Resolve(type);
            if (wf == null)
                return Results.Json(new { error = $"Workflow '{type}' không tồn tại" }, statusCode: 404);

            // Interval hợp lệ: 1..1440 phút (min 1 phút, max 24 giờ)
            var interval = Math.Clamp(req.IntervalMinutes, 1, 1440);
            var scopeUser = wf.Scope == WorkflowScope.PerUser ? user : "";

            repo.UpsertConfig(tenant, scopeUser, type, req.Enabled, interval, updatedBy: user);

            // Trả lại config mới nhất
            var updated = repo.Get(tenant, scopeUser, type);
            return Results.Json(new
            {
                ok = true,
                type,
                enabled = updated?.Enabled ?? req.Enabled,
                intervalMinutes = updated?.IntervalMinutes ?? interval,
                consecutiveFailures = updated?.ConsecutiveFailures ?? 0,
                pausedReason = updated?.PausedReason,
                nextRunUtc = updated?.NextRunUtc
            });
        });

        // ─── POST /workflows/{type}/run-now ─── manual trigger (synchronous) ─────
        v1.MapPost("/workflows/{type}/run-now", async (
            string type,
            HttpContext ctx,
            WorkflowRegistry registry,
            WorkflowRepository repo,
            WorkflowSchedulerService scheduler,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            var wf = registry.Resolve(type);
            if (wf == null)
                return Results.Json(new { error = $"Workflow '{type}' không tồn tại" }, statusCode: 404);

            var scopeUser = wf.Scope == WorkflowScope.PerUser ? user : "";

            // Đảm bảo config row tồn tại (tạo mới nếu chưa có, giữ trạng thái nếu đã có)
            var existing = repo.Get(tenant, scopeUser, type);
            if (existing == null)
                repo.UpsertConfig(tenant, scopeUser, type, enabled: false, intervalMinutes: 15, updatedBy: user);

            var sw = Stopwatch.StartNew();
            // Chạy qua scheduler pipeline (failures tracking + auto-pause + log run)
            await scheduler.RunOneAsync(wf, tenant, scopeUser, type, "manual", ctx.RequestAborted);
            sw.Stop();

            // Lấy run cuối cùng để trả thông tin
            var lastRun = repo.RecentRuns(tenant, scopeUser, type, 1).FirstOrDefault();
            return Results.Json(new
            {
                ok = lastRun?.Status == "ok",
                summary = lastRun?.Summary,
                error = lastRun?.Error,
                durationMs = lastRun?.DurationMs ?? (int)sw.ElapsedMilliseconds
            });
        });

        // ─── GET /workflows/{type}/runs ─── lịch sử run ──────────────────────────
        v1.MapGet("/workflows/{type}/runs", (
            string type,
            HttpContext ctx,
            WorkflowRegistry registry,
            WorkflowRepository repo,
            TkSessionStore sessions,
            int? limit) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            var wf = registry.Resolve(type);
            if (wf == null)
                return Results.Json(new { error = $"Workflow '{type}' không tồn tại" }, statusCode: 404);

            var scopeUser = wf.Scope == WorkflowScope.PerUser ? user : "";
            var lim = Math.Clamp(limit ?? 20, 1, 100);

            var runs = repo.RecentRuns(tenant, scopeUser, type, lim);
            return Results.Json(new
            {
                items = runs.Select(r => new
                {
                    id = r.Id,
                    triggerKind = r.TriggerKind,
                    startedUtc = r.StartedUtc,
                    finishedUtc = r.FinishedUtc,
                    status = r.Status,
                    summary = r.Summary,
                    error = r.Error,
                    durationMs = r.DurationMs
                }).ToList()
            });
        });

        return routes;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static (string SessionId, string TenantId, string Username)? RequireSession(
        HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId, s.Username);
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}

/// Request body cho PUT /workflows/{type}
public sealed record WorkflowConfigRequest(bool Enabled, int IntervalMinutes);
