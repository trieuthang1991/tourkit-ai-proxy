using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Deals;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Ưu tiên Deal AI — phân tích cơ hội bán hàng (booking-ticket), chấm khả năng thắng, xếp hạng ưu tiên.
///   POST /api/v1/deals/analyze              — start batch (cần session) → {jobId, streamUrl, cancelUrl}
///   GET  /api/v1/deals/analyze/{jobId}/stream — SSE tiến trình (scanning→ranked→scored→done)
///   POST /api/v1/deals/analyze/{jobId}/cancel — hủy
///   GET  /api/v1/deals/board                — bảng xếp hạng đã cache (mở lại không cần chạy lại)
/// </summary>
public static class DealEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize     = 200;
    private static readonly JsonSerializerOptions Sse = new(JsonSerializerDefaults.Web);

    public static void MapDealEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── GET /deals ─── danh sách paginated (mirror /customers) ─────────────
        // Response: items = DealOpportunity + scoreStatus: 'none' | 'fresh' (server-computed
        // bằng cách peek deal-cache theo tenant+id). FE chỉ filter scoreStatus === 'none'
        // → tường minh giống /customers (không cần merge client-side với board).
        v1.MapGet("/deals", async (HttpContext ctx, DealOpportunityClient client, DealRepository repo, TkSessionStore sessions,
            ILogger<Program> log, int? page, int? pageSize) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            var pIdx  = page is > 0 ? page.Value : 1;
            var pSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            try
            {
                var res = await client.ListPagedAsync(sid!, pIdx, pSize, ctx.RequestAborted);
                // Augment: mỗi item kèm scoreStatus từ cache (none/fresh — KHÔNG check fingerprint
                // ở đây để khỏi gọi GetContextAsync cho từng deal; "stale" khái niệm chỉ có nghĩa
                // khi deal đổi profile, xử lý sau).
                var items = res.Items.Select(it =>
                {
                    var cached = repo.PeekCached(sess.TenantId, it.Id);
                    // Compute heuristic priority server-side để FE chỉ render — không lặp logic.
                    object? scoreObj = null;
                    if (cached != null)
                    {
                        var (priority, ev) = DealHeuristic.FinalPriority(cached.Score.WinRate, it.TotalPrice, it.AgeDays);
                        scoreObj = new
                        {
                            cached.Score.WinRate, cached.Score.Level,
                            cached.Score.Signals, cached.Score.Risks, cached.Score.NextAction,
                            cached.Score.Reason,
                            cached.Score.AiModel, cached.Score.AiProvider,
                            PriorityScore = priority,
                            ExpectedValue = ev,
                            RiskFlag = DealHeuristic.RiskFlag(it.AgeDays)
                        };
                    }
                    return new
                    {
                        it.Id, it.Code, it.CustomerName, it.Phone, it.Title, it.TotalPrice,
                        it.Status, it.StatusName, it.Source, it.SourceName, it.MarketName, it.Assignees,
                        it.CreatedAt, it.AgeDays,
                        it.LatestComment, it.LatestCommentBy, it.LatestCommentDate, it.LastInteractionAt,
                        it.CoolingDays, it.IsCooling,
                        scoreStatus = cached != null ? "fresh" : "none",
                        score = scoreObj
                    };
                });
                return Results.Json(new { items, total = res.Total, page = pIdx, pageSize = pSize });
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "List deals lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // ─── POST /deals/analyze ─── start phân tích ─────────────────────────────
        v1.MapPost("/deals/analyze", (DealAnalyzeRequest req, HttpContext ctx, DealBatchService batch, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();

            var job = batch.Start(sid!, sess.TenantId, req);
            return Results.Json(new
            {
                jobId = job.Id,
                streamUrl = $"/api/v1/deals/analyze/{job.Id}/stream",
                cancelUrl = $"/api/v1/deals/analyze/{job.Id}/cancel",
                status = job.Status
            });
        });

        // ─── GET /deals/analyze/{jobId}/stream ─── SSE ───────────────────────────
        v1.MapGet("/deals/analyze/{jobId}/stream", async (string jobId, HttpContext ctx, DealBatchJobStore jobs, ILogger<Program> log) =>
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
                var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(payload, Sse) + "\n\n");
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            try
            {
                await foreach (var evt in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
                    await Write(new { type = evt.Type, payload = evt.Payload, error = evt.Error });
                jobs.Remove(jobId);
            }
            catch (OperationCanceledException) { log.LogInformation("[deals-stream] client {Id} ngắt", jobId); }
        });

        // ─── POST /deals/analyze/{jobId}/cancel ──────────────────────────────────
        v1.MapPost("/deals/analyze/{jobId}/cancel", (string jobId, DealBatchService batch) =>
            batch.Cancel(jobId) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Job không tồn tại hoặc đã xong" }));

        // ─── GET /deals/board ─── bảng đã cache ──────────────────────────────────
        v1.MapGet("/deals/board", (HttpContext ctx, DealRepository repo, TkSessionStore sessions) =>
        {
            var sess = sessions.Get(Sid(ctx));
            if (sess == null) return Unauthorized();
            var board = repo.GetBoard(sess.TenantId);
            return Results.Json(board ?? new DealBoard(new(), "", 0, 0));
        });
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized() => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
