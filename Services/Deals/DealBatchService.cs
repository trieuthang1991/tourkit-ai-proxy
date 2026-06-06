using TourkitAiProxy.Models;

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
    private readonly ILogger<DealBatchService> _log;

    public DealBatchService(DealOpportunityClient client, DealScoringService scorer, DealRepository repo,
        DealBatchJobStore jobs, ILogger<DealBatchService> log)
    {
        _client = client; _scorer = scorer; _repo = repo; _jobs = jobs; _log = log;
    }

    public DealBatchJob Start(string sessionId, string tenant, DealAnalyzeRequest req)
    {
        var job = _jobs.Create();
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

        try
        {
            await Emit(job, "scanning");
            var all = await _client.ListOpenAsync(sessionId, req.Assignee, req.Source, ScanPageSize, ct);

            // Tầng 1: heuristic xếp sơ bộ → top N
            var ranked = all.OrderByDescending(DealHeuristic.QuickScore).Take(topN).ToList();
            job.Total = ranked.Count;
            await Emit(job, "ranked", new { scanned = all.Count, total = ranked.Count });

            if (ranked.Count == 0)
            {
                await Finish(job, "done", new { scanned = all.Count, board = EmptyBoard(all.Count) });
                _repo.SaveBoard(tenant, EmptyBoard(all.Count));
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
                        var score = _repo.GetScore(tenant, deal.Id, ctx.Fingerprint);
                        if (score == null)
                        {
                            score = await _scorer.ScoreAsync(ctx.Profile, req.Provider, req.Model, req.ApiKey, innerCt);
                            _repo.SaveScore(tenant, deal.Id, ctx.Fingerprint, score);
                        }

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
            var board = new DealBoard(sorted, DateTime.UtcNow.ToString("o"), all.Count, sorted.Count);
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

    private static async Task Finish(DealBatchJob job, string status, object? payload, string? error = null)
    {
        job.Status = status;
        job.FinishedAt = DateTime.UtcNow;
        await job.Events.Writer.WriteAsync(new DealBatchEvent(status, payload, error));
        job.Events.Writer.Complete();
    }
}
