using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Deals;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflows;

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
            TourKitApiClient api, TkSessionStore sessions, WorkflowRepository workflows,
            ILogger<Program> log, int? page, int? pageSize, string? q,
            int? trangThai, int? nguon, int? nhanVienPhuTrach,
            string? rank, int? minRank, int? maxRank,
            int? maxAge, long? minValue, long? maxValue, bool? cooling) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            var pIdx  = page is > 0 ? page.Value : 1;
            var pSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            // Cooling policy per-tenant (deal-auto-review PerTenant → Username=""). Chưa cấu hình → default {coolingDays:7, coolingStatuses:[]}.
            var coolOpt = DealAutoReviewOptions.Parse(workflows.Get(sess.TenantId, "", "deal-auto-review")?.OptionsJson);
            // Lọc "chỉ nguội" (server-side): deal nguội là subset nhỏ → fetch rộng (page 1, cap 200) rồi lọc proxy-side.
            if (cooling == true) { pIdx = 1; pSize = MaxPageSize; }
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
                // Pre-warm JWT cache 1 lần (re-login nếu soft-expire) → listTask.ListPagedAsync +
                // LoadReferenceAsync bên dưới đều trúng cache, tránh 2 lần re-login song song đua nhau.
                await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                // maxAge (tuổi ≤ N ngày) → startDate = hôm nay − N (upstream lọc InsDttm >= startDate).
                // minValue/maxValue → minPrice/maxPrice (upstream lọc TotalPrice computed). Tất cả server-side toàn DB.
                string? startDate = maxAge is > 0 ? DateTime.Today.AddDays(-maxAge.Value).ToString("yyyy-MM-dd") : null;
                var listTask    = client.ListPagedAsync(sid!, pIdx, pSize, ctx.RequestAborted,
                                      q, trangThai, nguon, nhanVienPhuTrach, rankInt, minRank, maxRank,
                                      startDate, minValue, maxValue);
                // Reference PHẢI resilient với JWT hết hạn GIỐNG list. Trước đây gọi api.GetAsync THẲNG
                // (không retry 401): khi JWT soft-expire, listTask tự re-login + retry → list vẫn render OK,
                // còn refTask fault → lookups rỗng → dropdown NV/Trạng thái/Nguồn trống INTERMITTENT ("nhiều
                // lúc mất dữ liệu"). Bọc cùng pattern re-login-on-401 như DealOpportunityClient.GetAsync.
                async Task<JsonElement> LoadReferenceAsync()
                {
                    var j = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                    try { return await api.GetAsync(j, "/api/ai/reference", ctx.RequestAborted); }
                    catch (TourKitApiException ex) when (ex.Status == 401)
                    {
                        j = await sessions.ForceReloginAsync(sid!, ctx.RequestAborted);
                        return await api.GetAsync(j, "/api/ai/reference", ctx.RequestAborted);
                    }
                }
                var refTask     = LoadReferenceAsync();
                await Task.WhenAll(listTask, refTask.ContinueWith(_ => { }, TaskScheduler.Default));
                var res         = await listTask;
                // Reference resilience: nếu vẫn fault sau retry → log để chẩn đoán, rồi fail-soft
                // (lookups rỗng, FE giữ last-good). Bỏ qua cancel (client tự hủy).
                if (refTask.IsFaulted && !ctx.RequestAborted.IsCancellationRequested)
                    log.LogWarning(refTask.Exception?.GetBaseException(),
                        "[Deals] /api/ai/reference lỗi sau retry → lookups rỗng (FE giữ last-good)");
                // Bug fix "Đã chấm/Chưa chấm ngược" (QA sheet 2026-07-09):
                // TRƯỚC: scoreStatus = (proxy cache hit) → khi worker sync lag, upstream Rank=NULL
                //        (trả về khi user pick "Chưa chấm") NHƯNG proxy cache có → FE hiển thị "Đã chấm" ✗
                // GIỜ:   scoreStatus = upstream Rank (source of truth cho filter). Cache proxy chỉ dùng để enrich
                //        `score` object (winRate/level/signals/risks/nextAction) cho drawer chi tiết.
                //        Deal có Rank>0 nhưng cache proxy trống → FE fallback dùng chính Rank% + level heuristic.
                // Verdict "nguội" 1 nguồn (DealCooling) — override cờ upstream thô để badge/KPI/alert nhất quán.
                bool CoolVerdict(DealOpportunity d) => DealCooling.IsCooling(
                    d.Status, d.StatusName, d.CoolingDays, coolOpt.CoolingDays, coolOpt.CoolingStatuses);
                var sourceItems = cooling == true ? res.Items.Where(CoolVerdict).ToList() : res.Items.ToList();
                if (cooling == true && sourceItems.Count >= MaxPageSize)
                    log.LogWarning("[Deals] cooling filter chạm cap {Cap} — có thể còn deal nguội chưa hiện trang này", MaxPageSize);
                var items = sourceItems.Select(it =>
                {
                    var cached = repo.PeekCached(sess.TenantId, it.Id);
                    bool hasRank = it.Rank > 0;
                    // Prefer cached score khi có (đủ signals/risks/nextAction cho drawer). Fallback: dựng score
                    // tối thiểu từ upstream Rank khi cache trống (worker synced từ tenant khác/proxy cache mất).
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
                    else if (hasRank)
                    {
                        var (priority, ev) = DealHeuristic.FinalPriority(it.Rank, it.TotalPrice, it.AgeDays);
                        var level = it.Rank >= 60 ? "cao" : it.Rank >= 35 ? "trung_binh" : "thap";
                        scoreObj = new
                        {
                            WinRate = it.Rank, Level = level,
                            Signals = Array.Empty<string>(), Risks = Array.Empty<string>(),
                            NextAction = "", Reason = "",
                            AiModel = (string?)null, AiProvider = (string?)null,
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
                        it.CoolingDays, IsCooling = CoolVerdict(it),
                        rank = it.Rank,
                        scoreStatus = hasRank ? "fresh" : "none",
                        score = scoreObj
                    };
                }).ToList();
                // Bug fix "Bộ lọc Trạng thái thiếu option" (QA sheet 2026-07-09):
                // /api/ai/reference chỉ có BookingTicketStatuses hardcoded Range(1,6). Nếu tenant setup
                // status ID ngoài 1-6 hoặc reference lỗi → dropdown chỉ có "Tất cả" + 1-2 option.
                // Union thêm distinct (status, statusName) + (source, sourceName) từ items page hiện tại.
                var lookups     = BuildDealLookups(refTask.IsCompletedSuccessfully ? refTask.Result : default, res.Items);
                // coolingConfigured: tenant ĐÃ chọn trạng thái tính nguội chưa → FE chỉ hiện badge/KPI/chip "nguội"
                // khi = true (nguội là feature opt-in: chưa khai báo trạng thái thì không show gì trên trang Cơ hội).
                return Results.Json(new { items, total = cooling == true ? sourceItems.Count : res.Total, page = pIdx, pageSize = pSize, lookups,
                    coolingConfigured = coolOpt.CoolingStatuses.Count > 0 });
            }
            // Client tự hủy request (điều hướng/unmount/đổi filter) → CancellationToken cascade xuống
            // upstream call (SocketException 995). Benign: không ai chờ response → KHÔNG log error,
            // KHÔNG 500. Guard IsCancellationRequested để PHÂN BIỆT với upstream-timeout (vẫn log thật).
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(499);   // 499 Client Closed Request (response bị bỏ, client đã đóng)
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

        // ─── POST /deals/{id}/rescore ─── chấm lại 1 deal (SYNC, cho nút "Chấm lại" ở drawer) ──
        // Khác batch analyze: sync/1 deal/không SSE — response trả luôn DealBoardItem đã update
        // để FE swap vào state. Mirror /reviews/customer/{id}/refresh của Customer.
        v1.MapPost("/deals/{id:int}/rescore", async (int id, HttpContext ctx,
            DealOpportunityClient client, DealScoringService scorer, DealRepository repo,
            TourkitAiProxy.Services.AiCallContext aiCtx,
            TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();

            // Bọc AI call: trừ quota tenant + log feature="deals".
            using var _ = aiCtx.Push(AiFeatures.Deals, sess.TenantId, sid);

            try
            {
                // Fetch context (base + comments + customer profile enrich) — DÙNG CHUNG với batch/workflow.
                var contexts = await client.GetContextsAsync(sid!, new[] { id }, ctx.RequestAborted);
                var dwc = contexts.FirstOrDefault();
                if (dwc == null) return Results.NotFound(new { error = $"Không tìm thấy cơ hội #{id}" });

                var deal = dwc.Deal;
                var context = dwc.Context;
                var score = await scorer.ScoreAsync(context.Profile, null, null, null, ctx.RequestAborted);
                repo.SaveScore(sess.TenantId, deal.Id, context.Fingerprint, score);

                var (priority, ev) = DealHeuristic.FinalPriority(score.WinRate, deal.TotalPrice, deal.AgeDays);
                var item = new DealBoardItem(
                    Id: deal.Id, Code: deal.Code, CustomerName: deal.CustomerName ?? "(không tên)",
                    Phone: deal.Phone, Title: deal.Title, TotalPrice: deal.TotalPrice,
                    StatusName: deal.StatusName, SourceName: deal.SourceName, Assignees: deal.Assignees,
                    AgeDays: deal.AgeDays, WinRate: score.WinRate, Level: score.Level,
                    PriorityScore: priority, ExpectedValue: ev, Deep: true,
                    RiskFlag: DealHeuristic.RiskFlag(deal.AgeDays), Analysis: score);
                return Results.Json(new { item });
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex)
            {
                log.LogError(ex, "Rescore deal {Id} lỗi", id);
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized() => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

    /// Trích `statuses + sources + staffs` từ payload `/api/ai/reference` của TourKit,
    /// UNION với distinct `(status, statusName)` + `(source, sourceName)` xuất hiện trong items
    /// của page hiện tại (fallback khi reference thiếu enum của tenant setup ngoài Range(1,6)).
    /// Fail-soft: reference lỗi + items rỗng → 3 list rỗng, FE vẫn render "Tất cả".
    private static object BuildDealLookups(JsonElement root, IReadOnlyList<DealOpportunity> items)
    {
        static List<KeyValuePair<int, string>> Extract(JsonElement r, string path1, string path2)
        {
            var list = new List<KeyValuePair<int, string>>();
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(path1, out var p1) ||
                p1.ValueKind != JsonValueKind.Object || !p1.TryGetProperty(path2, out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                int id = 0;
                if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vn)) id = vn;
                else if (e.TryGetProperty("id", out var id2) && id2.ValueKind == JsonValueKind.Number && id2.TryGetInt32(out var idn)) id = idn;
                string name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (id > 0 && !string.IsNullOrWhiteSpace(name)) list.Add(new(id, name));
            }
            return list;
        }
        static object Union(List<KeyValuePair<int, string>> refList, IEnumerable<(int Id, string? Name)> fromItems)
        {
            // Dict Id → Name — reference đặt trước (đảm bảo thứ tự ổn định), items chỉ bổ sung Id mới.
            var dict = new Dictionary<int, string>();
            foreach (var kv in refList) if (!dict.ContainsKey(kv.Key)) dict[kv.Key] = kv.Value;
            foreach (var (id, name) in fromItems)
            {
                if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                if (!dict.ContainsKey(id)) dict[id] = name!;
            }
            return dict.Select(kv => new { id = kv.Key, name = kv.Value }).ToList();
        }
        var refStatuses = Extract(root, "enums", "bookingTicketStatuses");
        var refSources  = Extract(root, "enums", "bookingTicketSources");
        var refStaffs   = Extract(root, "lookups", "sellers");
        return new
        {
            statuses = Union(refStatuses, items.Select(it => (it.Status, it.StatusName))),
            sources  = Union(refSources,  items.Select(it => (it.Source,  it.SourceName))),
            // staffs: giữ nguyên reference (Assignees là CSV tên, không có ID → không union được).
            staffs   = refStaffs.Select(kv => new { id = kv.Key, name = kv.Value }).ToList(),
        };
    }
}
