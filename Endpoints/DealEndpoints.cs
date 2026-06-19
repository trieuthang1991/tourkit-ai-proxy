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
        v1.MapGet("/deals", async (HttpContext ctx, DealOpportunityClient client, DealRepository repo,
            TourKitApiClient api, TkSessionStore sessions,
            ILogger<Program> log, int? page, int? pageSize, string? q,
            int? trangThai, int? nguon, int? nhanVienPhuTrach,
            string? rank, int? minRank, int? maxRank) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            var pIdx  = page is > 0 ? page.Value : 1;
            var pSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            try
            {
                // q + trangThai/nguon/nhanVienPhuTrach đẩy thẳng upstream `/api/ai/booking-tickets`
                // (upstream support sẵn các param này — không cần fetch-all + post-filter).
                // `level` (AI rating cao/TB/thấp), `risk` (cooling), `minValue/maxValue/maxAge/sortBy`:
                // upstream KHÔNG support → FE tạm bỏ chip AI rating (chờ user bổ sung backend param);
                // min/max/age/sort giữ client-side với label "(trên trang)".
                //
                // Lookups (statuses/sources/staffs cho dropdown filter) đính kèm vào response
                // — fetch upstream `/api/ai/reference` SONG SONG với list để không cộng latency.
                // Fail-soft: nếu reference lỗi, trả lookups rỗng — FE vẫn render được.
                // rank token (FE): "any"=đã chấm bất kỳ · "-1"=chưa chấm · "0"/rỗng=bỏ qua
                int? rankInt = null;
                if (!string.IsNullOrWhiteSpace(rank))
                {
                    var rt = rank.Trim().ToUpperInvariant();
                    rankInt = rt switch
                    {
                        "ANY"  => 1,    // sentinel > 0 → upstream lọc Rank > 0
                        _ => int.TryParse(rt, out var rn) ? rn : 0
                    };
                    if (rankInt == 0) rankInt = null;
                }
                var listTask    = client.ListPagedAsync(sid!, pIdx, pSize, ctx.RequestAborted,
                                      q, trangThai, nguon, nhanVienPhuTrach, rankInt, minRank, maxRank);
                var refTask     = api.GetAsync(sid!, "/api/ai/reference", ctx.RequestAborted);
                await Task.WhenAll(listTask, refTask.ContinueWith(_ => { }, TaskScheduler.Default));
                var res         = await listTask;
                var lookups     = BuildDealLookups(refTask.IsCompletedSuccessfully ? refTask.Result : default);
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
                return Results.Json(new { items, total = res.Total, page = pIdx, pageSize = pSize, lookups });
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
        v1.MapGet("/deals/analyze/{jobId}/stream", async (string jobId, HttpContext ctx, DealBatchJobStore jobs, TkSessionStore sessions, ILogger<Program> log) =>
        {
            // Auth + tenant scope: stream lộ deal/KH/winRate → PHẢI verify session + đúng tenant.
            var sess = sessions.Get(Sid(ctx));
            if (sess == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Phiên không hợp lệ — đăng nhập lại" }); return; }
            var job = jobs.Get(jobId);
            if (job == null || job.TenantId != sess.TenantId) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = $"Không tìm thấy job {jobId}" }); return; }

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
            catch (OperationCanceledException)
            {
                // Client ngắt → cancel job.Cts để dừng chấm (nếu không, batch chạy hết topN call AI =
                // đốt token cho người đã thoát) + Remove khỏi store (trước đây chỉ log → vừa đốt token
                // vừa leak entry). Cancel idempotent. Cancel TRƯỚC Remove.
                job.Cts.Cancel();
                log.LogInformation("[deals-stream] client {Id} ngắt → cancel batch", jobId);
                jobs.Remove(jobId);
            }
        });

        // ─── POST /deals/analyze/{jobId}/cancel ──────────────────────────────────
        v1.MapPost("/deals/analyze/{jobId}/cancel", (string jobId, HttpContext ctx, DealBatchJobStore jobs, DealBatchService batch, TkSessionStore sessions) =>
        {
            var sess = sessions.Get(Sid(ctx));
            if (sess == null) return Unauthorized();
            var job = jobs.Get(jobId);
            if (job == null || job.TenantId != sess.TenantId) return Results.NotFound(new { error = "Job không tồn tại hoặc đã xong" });
            return batch.Cancel(jobId) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Job không tồn tại hoặc đã xong" });
        });

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

    /// Trích `statuses + sources + staffs` từ payload `/api/ai/reference` của TourKit.
    /// Fail-soft: nếu `root` rỗng (ref call lỗi), trả 3 list rỗng — FE vẫn render được dropdown
    /// (chỉ có option "Tất cả").
    private static object BuildDealLookups(JsonElement root)
    {
        static List<object> Extract(JsonElement r, string path1, string path2)
        {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(path1, out var p1) ||
                p1.ValueKind != JsonValueKind.Object || !p1.TryGetProperty(path2, out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return new();
            var items = new List<object>();
            foreach (var e in arr.EnumerateArray())
            {
                int id = e.TryGetProperty("value", out var v) ? v.GetInt32() :
                         e.TryGetProperty("id", out var id2) ? id2.GetInt32() : 0;
                string name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (id > 0 && !string.IsNullOrWhiteSpace(name)) items.Add(new { id, name });
            }
            return items;
        }
        return new
        {
            statuses = Extract(root, "enums",   "bookingTicketStatuses"),
            sources  = Extract(root, "enums",   "bookingTicketSources"),
            staffs   = Extract(root, "lookups", "sellers"),
        };
    }
}
