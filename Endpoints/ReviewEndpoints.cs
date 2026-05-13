using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Reviews;

namespace TourkitAiProxy.Endpoints;

/// REST endpoints cho tính năng Review khách hàng bằng AI.
///
///   GET  /api/v1/customers                       — list + filter (segment, search, lastDays)
///   GET  /api/v1/customers/{id}                  — chi tiết KH + review (nếu có)
///   POST /api/v1/reviews/customer/{id}           — review sync 1 KH (body optional {forceFresh:bool})
///   POST /api/v1/reviews/customer/{id}/refresh   — alias cho forceFresh=true
///   POST /api/v1/reviews/batch                   — start batch job, return {jobId, total, ...}
///   GET  /api/v1/reviews/batch/{jobId}/stream    — SSE progress
///   POST /api/v1/reviews/batch/{jobId}/cancel    — cancel batch
///   POST /api/v1/reviews/{customerId}/feedback   — thumbs up/down + note
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── Customer list + detail ────────────────────────────────────────────
        v1.MapGet("/customers", (
            CustomerRepository customers, ReviewRepository reviews,
            string? segment, string? search, int? lastDays) =>
        {
            var list = customers.Filter(segment, search, lastDays);
            var items = list.Select(c =>
            {
                var r = reviews.Get(c.Id);
                string status = "none";
                int? ageHours = null;
                if (r != null)
                {
                    var currentFp = ReviewRepository.FingerprintFor(c);
                    status = currentFp == r.DataFingerprint ? "fresh" : "stale";
                    if (DateTime.TryParse(r.GeneratedAt, out var dt))
                        ageHours = (int)((DateTime.UtcNow - dt.ToUniversalTime()).TotalHours);
                }
                return new CustomerListItem(
                    Id:                  c.Id,
                    Name:                c.Name,
                    Segment:             c.Segment,
                    TotalSpent:          c.Metrics.TotalSpent,
                    TotalTours:          c.Metrics.TotalTours,
                    LastPurchaseDaysAgo: c.Metrics.LastPurchaseDaysAgo,
                    Rank:                r?.Rank,
                    ReviewStatus:        status,
                    ReviewAgeHours:      ageHours,
                    SummaryLine:         r?.SummaryLine
                );
            });
            return Results.Json(items);
        });

        v1.MapGet("/customers/{id}", (string id, CustomerRepository customers, ReviewRepository reviews) =>
        {
            var c = customers.Get(id);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });
            return Results.Json(new { customer = c, review = reviews.Get(id) });
        });

        // ─── Sync single-customer review ──────────────────────────────────────
        v1.MapPost("/reviews/customer/{id}", async (
            string id, HttpContext ctx,
            CustomerRepository customers, ReviewService service, ILogger<Program> log) =>
        {
            var c = customers.Get(id);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });

            bool forceFresh = false;
            if (ctx.Request.ContentLength > 0)
            {
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body);
                    if (body != null && body.TryGetValue("forceFresh", out var f) && f.ValueKind == JsonValueKind.True)
                        forceFresh = true;
                }
                catch { /* body optional */ }
            }

            try
            {
                var (review, fromCache) = await service.ReviewAsync(c, forceFresh, onStage: null, ctx.RequestAborted);
                return Results.Json(new { review, fromCache });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Review KH {Id} failed", id);
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        v1.MapPost("/reviews/customer/{id}/refresh", async (
            string id, HttpContext ctx,
            CustomerRepository customers, ReviewService service, ILogger<Program> log) =>
        {
            var c = customers.Get(id);
            if (c == null) return Results.NotFound(new { error = $"Không tìm thấy KH {id}" });
            try
            {
                var (review, _) = await service.ReviewAsync(c, forceFresh: true, onStage: null, ctx.RequestAborted);
                return Results.Json(new { review, fromCache = false });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Refresh review KH {Id} failed", id);
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // ─── Batch ────────────────────────────────────────────────────────────
        v1.MapPost("/reviews/batch", (BatchReviewRequest req, BatchService batch) =>
        {
            if (req.CustomerIds == null || req.CustomerIds.Count == 0)
                return Results.BadRequest(new { error = "customerIds rỗng" });
            if (req.CustomerIds.Count > 200)
                return Results.BadRequest(new { error = "Tối đa 200 KH/batch" });

            var job = batch.Start(req.CustomerIds, req.ForceFresh);
            return Results.Json(new
            {
                jobId       = job.Id,
                total       = job.Total,
                streamUrl   = $"/api/v1/reviews/batch/{job.Id}/stream",
                cancelUrl   = $"/api/v1/reviews/batch/{job.Id}/cancel",
                status      = job.Status
            });
        });

        v1.MapGet("/reviews/batch/{jobId}/stream", async (string jobId, HttpContext ctx, BatchJobStore jobs, ILogger<Program> log) =>
        {
            var job = jobs.Get(jobId);
            if (job == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = $"Không tìm thấy job {jobId}" });
                return;
            }

            ctx.Response.Headers["Content-Type"]      = "text/event-stream";
            ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var bodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            bodyFeature?.DisableBuffering();
            await ctx.Response.StartAsync(ctx.RequestAborted);

            async Task WriteEventAsync(object payload)
            {
                var line = "data: " + JsonSerializer.Serialize(payload) + "\n\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                await foreach (var evt in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await WriteEventAsync(new
                    {
                        type       = evt.Type,
                        customerId = evt.CustomerId,
                        payload    = evt.Payload,
                        error      = evt.Error
                    });
                }
                // Cleanup sau khi job xong
                jobs.Remove(jobId);
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("[batch-stream] client {Id} ngắt kết nối", jobId);
            }
        });

        v1.MapPost("/reviews/batch/{jobId}/cancel", (string jobId, BatchService batch) =>
        {
            return batch.Cancel(jobId)
                ? Results.Json(new { ok = true })
                : Results.BadRequest(new { error = "Job không tồn tại hoặc đã kết thúc" });
        });

        // ─── Feedback ─────────────────────────────────────────────────────────
        v1.MapPost("/reviews/{customerId}/feedback", (string customerId, FeedbackRequest fb, ReviewRepository reviews) =>
        {
            if (fb.Rating != "helpful" && fb.Rating != "not_helpful")
                return Results.BadRequest(new { error = "rating phải là helpful|not_helpful" });

            var entry = new ReviewFeedback(fb.Rating, fb.Note, DateTime.UtcNow.ToString("o"));
            var saved = reviews.SetFeedback(customerId, entry);
            return saved ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Chưa có review cho KH này" });
        });

        return routes;
    }
}
