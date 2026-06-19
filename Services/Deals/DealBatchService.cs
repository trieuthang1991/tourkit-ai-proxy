using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Deals;

/// Điều phối phân tích pipeline 2 tầng:
///   1) list cơ hội mở → 2) HEURISTIC xếp sơ bộ, lấy top N → 3) AI chấm sâu top N (song song, cache fingerprint)
///   → 4) ưu tiên cuối (EV + độ gấp) → 5) lưu bảng + stream SSE tiến trình.
public class DealBatchService
{
    private const int Concurrency = 6;
    private const int ScanPageSize = 200;
    private const int DefaultTopN = 20;

    private readonly DealOpportunityClient _client;
    private readonly DealScoringService _scorer;
    private readonly DealRepository _repo;
    private readonly DealBatchJobStore _jobs;
    private readonly IWorkflowTraceAccessor _traceAccessor;
    private readonly WorkflowTraceLog _traceLog;
    private readonly AiCallContext _ctx;
    private readonly ILogger<DealBatchService> _log;

    public DealBatchService(DealOpportunityClient client, DealScoringService scorer, DealRepository repo,
        DealBatchJobStore jobs, IWorkflowTraceAccessor traceAccessor, WorkflowTraceLog traceLog,
        AiCallContext ctx, ILogger<DealBatchService> log)
    {
        _client = client; _scorer = scorer; _repo = repo; _jobs = jobs;
        _traceAccessor = traceAccessor; _traceLog = traceLog; _ctx = ctx; _log = log;
    }

    public DealBatchJob Start(string sessionId, string tenant, DealAnalyzeRequest req)
    {
        var job = _jobs.Create();
        job.TenantId = tenant;   // stream/cancel verify tenant
        job.Status = "processing";
        _ = RunAsync(job, sessionId, tenant, req);
        return job;
    }

    public bool Cancel(string jobId)
    {
        var job = _jobs.Get(jobId);
        if (job == null || job.Status != "processing") return false;
        job.Cts.Cancel();
        return true;
    }

    private async Task RunAsync(DealBatchJob job, string sessionId, string tenant, DealAnalyzeRequest req)
    {
        var ct = job.Cts.Token;
        var topN = req.TopN is > 0 and <= 50 ? req.TopN.Value : DefaultTopN;
        var items = new List<DealBoardItem>();
        var gate = new object();

        // AsyncLocal: HttpContext gone sau khi endpoint return → providers gọi _ctx.Resolve() sẽ trả
        // feature=unknown + tenant=null (bypass quota). Push override để mọi AI call trong batch log
        // "deals" + đếm quota đúng tenant. Flow qua Parallel.ForEachAsync vì AsyncLocal capture.
        using var _ctxScope = _ctx.Push("deals", tenant, sessionId);

        // Trace flow qua AsyncLocal từ endpoint /deals/analyze → batch background work.
        var trace = _traceAccessor.Current;
        trace?.SetWorkflow("DealBatch");
        trace?.SetMeta("tenant", tenant);
        trace?.SetMeta("topN", topN);
        trace?.SetMeta("assignee", req.Assignee);
        trace?.SetMeta("source", req.Source);

        try
        {
            await Emit(job, "scanning");

            // 2 chế độ:
            //  - Manual: req.DealIds có giá trị → fetch ListPagedAsync (KHÔNG filter Hủy) tìm
            //    đúng deal user chọn — kể cả Hủy, đã xử lý...
            //  - Auto: ListOpenAsync (bỏ Hủy) + heuristic top N
            List<DealOpportunity> ranked;
            int scanned;
            if (req.DealIds != null && req.DealIds.Count > 0)
            {
                // Fetch toàn bộ pipeline (cap 500 để tránh upstream overload)
                var pageRes = await _client.ListPagedAsync(sessionId, 1, 500, ct);
                scanned = pageRes.Items.Count;
                var wanted = req.DealIds.Take(50).ToHashSet();
                ranked = pageRes.Items.Where(d => wanted.Contains(d.Id.ToString())).ToList();
                trace?.SetMeta("mode", "manual");
                trace?.SetMeta("requested", req.DealIds.Count);
                trace?.SetMeta("matched", ranked.Count);
                if (ranked.Count < req.DealIds.Count)
                {
                    var foundIds = ranked.Select(d => d.Id.ToString()).ToHashSet();
                    var missing  = req.DealIds.Where(id => !foundIds.Contains(id)).ToList();
                    _log.LogWarning("[deals] manual: {Found}/{Req} deal match, missing: {Missing}",
                        ranked.Count, req.DealIds.Count, string.Join(",", missing.Take(10)));
                    trace?.SetMeta("missingIds", missing);
                }
            }
            else
            {
                var all = await _client.ListOpenAsync(sessionId, req.Assignee, req.Source, ScanPageSize, ct);
                scanned = all.Count;
                ranked = all.OrderByDescending(DealHeuristic.QuickScore).Take(topN).ToList();
                trace?.SetMeta("mode", "auto");
            }
            job.Total = ranked.Count;
            await Emit(job, "ranked", new { scanned, total = ranked.Count });

            if (ranked.Count == 0)
            {
                await Finish(job, "done", new { scanned, board = EmptyBoard(scanned) });
                _repo.SaveBoard(tenant, EmptyBoard(scanned));
                return;
            }

            // Tầng 2: AI chấm sâu song song
            await Parallel.ForEachAsync(ranked,
                new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
                async (deal, innerCt) =>
                {
                    try
                    {
                        var ctx = await _client.GetContextAsync(sessionId, deal, innerCt);
                        // Cache đã BỎ (yêu cầu 2026-06-18): luôn chấm lại, KHÔNG check _repo.GetScore.
                        // DealScoring CHỈ đọc model/key từ appsettings (Models:DealScoring) — bỏ override
                        // từ frontend (cfg.provider/cfg.model trong localStorage hay làm sai vì FE từng set
                        // anthropic/haiku, đè qua config server-side grok-4.3 → log toàn haiku).
                        DealScore score = await _scorer.ScoreAsync(ctx.Profile, null, null, null, innerCt);
                        // SaveScore VẪN GIỮ: worker DealScoreSyncService đọc dbo.DealScores → sync cột [rank]
                        // xuống BookingTickets tenant DB → frontend filter rank/Win cao/thấp dùng được.
                        _repo.SaveScore(tenant, deal.Id, ctx.Fingerprint, score);

                        var (priority, ev) = DealHeuristic.FinalPriority(score.WinRate, deal.TotalPrice, deal.AgeDays);
                        var item = new DealBoardItem(
                            Id: deal.Id, Code: deal.Code, CustomerName: deal.CustomerName ?? "(không tên)",
                            Phone: deal.Phone, Title: deal.Title, TotalPrice: deal.TotalPrice,
                            StatusName: deal.StatusName, SourceName: deal.SourceName, Assignees: deal.Assignees,
                            AgeDays: deal.AgeDays, WinRate: score.WinRate, Level: score.Level,
                            PriorityScore: priority, ExpectedValue: ev, Deep: true,
                            RiskFlag: DealHeuristic.RiskFlag(deal.AgeDays), Analysis: score);

                        lock (gate) { items.Add(item); job.Done++; }
                        await Emit(job, "scored", new { done = job.Done, total = job.Total, item });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[deals] chấm deal {Id} lỗi", deal.Id);
                        lock (gate) { job.Errors++; }
                        await Emit(job, "error", new { id = deal.Id }, ex.Message);
                    }
                });

            var sorted = items.OrderByDescending(i => i.PriorityScore).ToList();
            var board = new DealBoard(sorted, DateTime.UtcNow.ToString("o"), scanned, sorted.Count);
            _repo.SaveBoard(tenant, board);
            await Finish(job, "done", new { board });
        }
        catch (OperationCanceledException)
        {
            await Finish(job, "cancelled", new { done = job.Done, total = job.Total });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[deals] job {Id} crash", job.Id);
            await Finish(job, "error", null, ex.Message);
        }
    }

    private static DealBoard EmptyBoard(int scanned)
        => new(new List<DealBoardItem>(), DateTime.UtcNow.ToString("o"), scanned, 0);

    private static async Task Emit(DealBatchJob job, string type, object? payload = null, string? error = null)
        => await job.Events.Writer.WriteAsync(new DealBatchEvent(type, payload, error));

    private async Task Finish(DealBatchJob job, string status, object? payload, string? error = null)
    {
        job.Status = status;
        job.FinishedAt = DateTime.UtcNow;
        await job.Events.Writer.WriteAsync(new DealBatchEvent(status, payload, error));

        // Trace cho batch: AsyncLocal flow qua Task.Run → trace.Current không null khi debug ON.
        var trace = _traceAccessor.Current;
        if (trace?.Enabled == true)
        {
            try
            {
                var built = trace.Build();
                _traceLog.Append(ctx: null, built);
                await job.Events.Writer.WriteAsync(new DealBatchEvent("trace", built));
            }
            catch (Exception ex) { _log.LogWarning(ex, "[deals] log trace fail"); }
        }

        job.Events.Writer.Complete();
    }
}
