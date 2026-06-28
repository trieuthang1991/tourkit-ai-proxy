using System.Diagnostics;
using System.Text.Json;
using TourkitAiProxy.Services.Mail;
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
                    nextRunUtc = AsUtc(row?.NextRunUtc),
                    lastRunUtc = AsUtc(row?.LastRunUtc),
                    lastRunStatus = row?.LastRunStatus,
                    lastRunSummary = row?.LastRunSummary,
                    options = ParseOptions(row?.OptionsJson),   // điều kiện/option ĐỘNG (object) cho UI
                    updatedBy = row?.UpdatedBy,
                    updatedAtUtc = AsUtc(row?.UpdatedAtUtc)
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

            // Options ĐỘNG: client gửi object → lưu raw JSON. null = giữ nguyên options cũ.
            var optionsJson = req.Options.HasValue && req.Options.Value.ValueKind != System.Text.Json.JsonValueKind.Null
                ? req.Options.Value.GetRawText()
                : null;

            repo.UpsertConfig(tenant, scopeUser, type, req.Enabled, interval, updatedBy: user, optionsJson: optionsJson);

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
                nextRunUtc = AsUtc(updated?.NextRunUtc),
                options = ParseOptions(updated?.OptionsJson)
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
            // Chạy qua scheduler pipeline (failures tracking + auto-pause + log run) — truyền options đã lưu.
            await scheduler.RunOneAsync(wf, tenant, scopeUser, type, "manual", existing?.OptionsJson, ctx.RequestAborted);
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
                    startedUtc = AsUtc(r.StartedUtc),
                    finishedUtc = AsUtc(r.FinishedUtc),
                    status = r.Status,
                    summary = r.Summary,
                    error = r.Error,
                    durationMs = r.DurationMs
                }).ToList()
            });
        });

        // ─── POST /workflows/service-account ─── tài khoản tự động per-tenant ─────
        // Validate login TourKit + đếm deal thấy được TRƯỚC khi lưu (Crypton-enc). KHÔNG trả password.
        v1.MapPost("/workflows/service-account", async (
            WorkflowServiceAccountRequest req,
            HttpContext ctx,
            TenantServiceAccountStore store,
            TourKitApiClient api,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new { ok = false, error = "Thiếu username/password" }, statusCode: 400);

            try
            {
                var login = await api.LoginAsync(tenant, req.Username.Trim(), req.Password, ctx.RequestAborted);
                int dealsVisible = 0;
                try
                {
                    var env = await api.GetAsync(login.Token, "/api/ai/booking-tickets?pageIndex=1&pageSize=1", ctx.RequestAborted);
                    if (env.ValueKind == JsonValueKind.Object && env.TryGetProperty("total", out var t) && t.TryGetInt32(out var n))
                        dealsVisible = n;
                }
                catch { /* đếm deal best-effort — không chặn lưu */ }

                await store.UpsertAsync(tenant, req.Username.Trim(), req.Password, updatedBy: user, ctx.RequestAborted);
                return Results.Json(new { ok = true, dealsVisible, warning = dealsVisible == 0 ? "Tài khoản đăng nhập OK nhưng thấy 0 deal — có thể thiếu quyền CH_XEM_ALL" : null });
            }
            catch (TourKitApiException ex)
            {
                return Results.Json(new { ok = false, error = $"Đăng nhập thất bại: {ex.Message}" }, statusCode: 200);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 200);
            }
        });

        // ─── GET /workflows/service-account ─── trạng thái cấu hình ──────────────
        v1.MapGet("/workflows/service-account", (
            HttpContext ctx,
            TenantServiceAccountStore store,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, _) = auth.Value;
            var (configured, username) = store.Status(tenant);
            return Results.Json(new { configured, username });
        });

        // ─── DELETE /workflows/service-account ─── xóa tài khoản tự động ─────────
        // Xóa hẳn → workflow ngừng tự login (fail "chưa cấu hình"). Dùng khi muốn tắt automation deal.
        v1.MapDelete("/workflows/service-account", async (
            HttpContext ctx,
            TenantServiceAccountStore store,
            TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, _) = auth.Value;
            var removed = await store.DeleteAsync(tenant, ctx.RequestAborted);
            return Results.Json(new { ok = true, removed });
        });

        // ─── GET /workflows/outbound-mails ─── theo dõi hàng đợi mail ────────────
        v1.MapGet("/workflows/outbound-mails", async (
            HttpContext ctx,
            MailQueueRepository queue,
            TkSessionStore sessions,
            string? kind,
            int? status,
            int? limit) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, _) = auth.Value;
            var rows = await queue.ListForMonitorAsync(tenant, kind, status, limit ?? 50, ctx.RequestAborted);
            return Results.Json(new
            {
                items = rows.Select(r => new
                {
                    id = r.Id, kind = r.Kind, sourceId = r.SourceId, templateCode = r.TemplateCode,
                    toEmail = r.ToEmail, toName = r.ToName, subject = r.Subject,
                    status = (int)r.Status, retryCount = r.RetryCount, errorMessage = r.ErrorMessage,
                    scheduledUtc = AsUtc(r.ScheduledUtc), createdUtc = AsUtc(r.CreatedUtc), processedUtc = AsUtc(r.ProcessedUtc)
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

    /// Đánh dấu DateTime là UTC → System.Text.Json serialize kèm 'Z' → JS parse đúng UTC (không lệch +7h).
    /// DateTime từ SQL (Dapper) có Kind=Unspecified nên mặc định serialize KHÔNG có 'Z' → client hiểu nhầm local.
    private static DateTime? AsUtc(DateTime? d)
        => d.HasValue ? DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) : null;

    /// OptionsJson (string) → object cho JSON response (null nếu rỗng/parse lỗi).
    private static object? ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(optionsJson); }
        catch { return null; }
    }
}

/// Request body cho PUT /workflows/{type}. Options = điều kiện ĐỘNG tùy workflow (object tùy ý).
public sealed record WorkflowConfigRequest(bool Enabled, int IntervalMinutes, System.Text.Json.JsonElement? Options = null);

/// Request body cho POST /workflows/service-account (tài khoản tự động per-tenant).
public sealed record WorkflowServiceAccountRequest(string Username, string Password);
