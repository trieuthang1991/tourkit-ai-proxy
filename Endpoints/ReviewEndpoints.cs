using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// REST endpoints cho Customer Review — NAY dùng KHÁCH HÀNG THẬT từ TourKit (cần session).
///
///   GET  /api/v1/customers                       — list KH thật (filter segment/search) + review status
///   GET  /api/v1/customers/{id}                  — KH đầy đủ (orders→purchases/metrics) + review
///   POST /api/v1/reviews/customer/{id}           — review 1 KH (body optional {forceFresh})
///   POST /api/v1/reviews/customer/{id}/refresh   — alias forceFresh=true
///   POST /api/v1/reviews/batch                   — batch (cần session)
///   GET  /api/v1/reviews/batch/{jobId}/stream    — SSE progress
///   POST /api/v1/reviews/batch/{jobId}/cancel    — cancel
///   POST /api/v1/reviews/{customerId}/feedback   — thumbs + note
public static class ReviewEndpoints
{
    private const int ListSize = 100;

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── Customer lookups (loại/nguồn/NV) cho bộ lọc nâng cao ──────────────
        v1.MapGet("/customers/lookups", async (HttpContext ctx, TourKitCustomerSource source, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            try
            {
                var data = await source.GetLookupsAsync(sid!, ctx.RequestAborted);
                // /api/ai/reference trả {enums, lookups:{customerTypes,customerSources,sellers,...}}
                if (data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && data.TryGetProperty("lookups", out var lk)) return Results.Json(lk);
                return Results.Json(data);
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        // ─── Customer list (KH thật) ───────────────────────────────────────────
        v1.MapGet("/customers", async (
            HttpContext ctx, TourKitCustomerSource source, ReviewRepository reviews,
            TkSessionStore sessions, ILogger<Program> log,
            string? segment, string? search,
            int? customerTypeId, int? customerSourceId, int? sellerId,
            string? gender, string? careFilter, bool? birthdayThisMonth,
            string? startDate, string? endDate, string? sortOrder) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();

            try
            {
                var filter = new TourKitCustomerSource.CustomerFilter(
                    Search: search, CustomerTypeId: customerTypeId, CustomerSourceId: customerSourceId,
                    SellerId: sellerId, Gender: gender, CareFilter: careFilter,
                    BirthdayThisMonth: birthdayThisMonth, StartDate: startDate, EndDate: endDate,
                    SortOrder: sortOrder);
                var customers = await source.ListAsync(sid!, filter, ListSize, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(segment) && segment != "all")
                    customers = customers.Where(c => string.Equals(c.Segment, segment, StringComparison.OrdinalIgnoreCase)).ToList();

                var tenant = sessions.Get(sid)?.TenantId ?? "";
                var items = customers.Select(c =>
                {
                    var r = reviews.Get(tenant, c.Id);
                    return new CustomerListItem(
                        Id: c.Id, Name: c.Name, Segment: c.Segment,
                        TotalSpent: c.Metrics.TotalSpent, TotalTours: c.Metrics.TotalTours,
                        LastPurchaseDaysAgo: c.Metrics.LastPurchaseDaysAgo,
                        Rank: r?.Rank, ReviewStatus: r == null ? "none" : "fresh",
                        ReviewAgeHours: AgeHours(r), SummaryLine: r?.SummaryLine);
                });
                return Results.Json(items);
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "List KH lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // ─── Customer detail (KH thật đầy đủ) ──────────────────────────────────
        v1.MapGet("/customers/{id}", async (string id, HttpContext ctx,
            TourKitCustomerSource source, ReviewRepository reviews, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            var c = await source.GetFullAsync(sid!, id, ctx.RequestAborted);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });
            var tenant = sessions.Get(sid)?.TenantId ?? "";
            return Results.Json(new { customer = c, review = reviews.Get(tenant, id) });
        });

        // ─── Sync single review ────────────────────────────────────────────────
        v1.MapPost("/reviews/customer/{id}", async (string id, HttpContext ctx,
            TourKitCustomerSource source, ReviewService service, TkSessionStore sessions,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            var c = await source.GetFullAsync(sid!, id, ctx.RequestAborted);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });

            bool forceFresh = false;
            if (ctx.Request.ContentLength > 0)
            {
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body);
                    if (body != null && body.TryGetValue("forceFresh", out var f) && f.ValueKind == JsonValueKind.True) forceFresh = true;
                }
                catch { }
            }
            try
            {
                var tenant = sessions.Get(sid)?.TenantId ?? "";
                var (review, fromCache) = await service.ReviewAsync(c, tenant, forceFresh, null, ctx.RequestAborted);
                // Đính trace nếu user bật ?debug=1 / X-Debug header
                var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
                return Results.Json(new { review, fromCache, _trace = traceObj });
            }
            catch (Exception ex) { log.LogError(ex, "Review KH {Id} lỗi", id); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        v1.MapPost("/reviews/customer/{id}/refresh", async (string id, HttpContext ctx,
            TourKitCustomerSource source, ReviewService service, TkSessionStore sessions,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            var c = await source.GetFullAsync(sid!, id, ctx.RequestAborted);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });
            try
            {
                var tenant = sessions.Get(sid)?.TenantId ?? "";
                var (review, _) = await service.ReviewAsync(c, tenant, true, null, ctx.RequestAborted);
                var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
                return Results.Json(new { review, fromCache = false, _trace = traceObj });
            }
            catch (Exception ex) { log.LogError(ex, "Refresh KH {Id} lỗi", id); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // ─── Batch ──────────────────────────────────────────────────────────────
        v1.MapPost("/reviews/batch", (BatchReviewRequest req, HttpContext ctx, BatchService batch, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            if (req.CustomerIds == null || req.CustomerIds.Count == 0) return Results.BadRequest(new { error = "customerIds rỗng" });
            if (req.CustomerIds.Count > 200) return Results.BadRequest(new { error = "Tối đa 200 KH/batch" });

            var job = batch.Start(req.CustomerIds, req.ForceFresh, sid!);
            return Results.Json(new
            {
                jobId = job.Id, total = job.Total,
                streamUrl = $"/api/v1/reviews/batch/{job.Id}/stream",
                cancelUrl = $"/api/v1/reviews/batch/{job.Id}/cancel",
                status = job.Status
            });
        });

        v1.MapGet("/reviews/batch/{jobId}/stream", async (string jobId, HttpContext ctx, BatchJobStore jobs, ILogger<Program> log) =>
        {
            var job = jobs.Get(jobId);
            if (job == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = $"Không tìm thấy job {jobId}" }); return; }

            ctx.Response.Headers["Content-Type"]      = "text/event-stream";
            ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.StartAsync(ctx.RequestAborted);

            async Task Write(object payload)
            {
                var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(payload) + "\n\n");
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            try
            {
                await foreach (var evt in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
                    await Write(new { type = evt.Type, customerId = evt.CustomerId, payload = evt.Payload, error = evt.Error });
                jobs.Remove(jobId);
            }
            catch (OperationCanceledException) { log.LogInformation("[batch-stream] client {Id} ngắt", jobId); }
        });

        v1.MapPost("/reviews/batch/{jobId}/cancel", (string jobId, BatchService batch) =>
            batch.Cancel(jobId) ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "Job không tồn tại hoặc đã kết thúc" }));

        // Admin: backfill TenantId cho rows legacy (migrated từ JSON cũ có TenantId="").
        // Cần session hợp lệ — TenantId lấy từ session, dọn rows TenantId='' không xung đột.
        v1.MapPost("/reviews/admin/backfill-tenant", async (HttpContext ctx, ReviewRepository reviews, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var s = sessions.Get(sid);
            if (s == null) return Unauthorized();
            var updated = await reviews.BackfillTenantIdAsync(s.TenantId, ctx.RequestAborted);
            return Results.Json(new { ok = true, tenantId = s.TenantId, updated });
        });

        // ─── Feedback ─────────────────────────────────────────────────────────────
        v1.MapPost("/reviews/{customerId}/feedback", (string customerId, FeedbackRequest fb,
            HttpContext ctx, ReviewRepository reviews, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            if (fb.Rating != "helpful" && fb.Rating != "not_helpful")
                return Results.BadRequest(new { error = "rating phải là helpful|not_helpful" });
            var tenant = sessions.Get(sid)?.TenantId ?? "";
            var entry = new ReviewFeedback(fb.Rating, fb.Note, DateTime.UtcNow.ToString("o"));
            return reviews.SetFeedback(tenant, customerId, entry)
                ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Chưa có review cho KH này" });
        });

        return routes;
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();

    private static IResult Unauthorized() => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

    private static int? AgeHours(CustomerReview? r)
        => r != null && DateTime.TryParse(r.GeneratedAt, out var dt)
           ? (int)((DateTime.UtcNow - dt.ToUniversalTime()).TotalHours) : null;
}
