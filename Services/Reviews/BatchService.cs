using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews;

/// Chạy batch review song song. Mỗi customer → 1 task; concurrency cap 10 (configurable).
/// Result push qua BatchJob.Events channel → SSE handler đọc và gửi event xuống client.
/// Cancel: token nội bộ; client gọi POST /batch/{id}/cancel hoặc đóng SSE → ngừng.
public class BatchService
{
    private readonly TourKitCustomerSource _source;
    private readonly ReviewService _reviews;
    private readonly BatchJobStore _jobs;
    private readonly TkSessionStore _sessions;
    private readonly IWorkflowTraceAccessor _traceAccessor;
    private readonly WorkflowTraceLog _traceLog;
    private readonly AiCallContext _ctx;
    private readonly ILogger<BatchService> _log;

    private const int CONCURRENCY = 10;

    public BatchService(TourKitCustomerSource source, ReviewService reviews, BatchJobStore jobs,
        TkSessionStore sessions, IWorkflowTraceAccessor traceAccessor, WorkflowTraceLog traceLog,
        AiCallContext ctx, ILogger<BatchService> log)
    {
        _source = source; _reviews = reviews; _jobs = jobs; _sessions = sessions;
        _traceAccessor = traceAccessor; _traceLog = traceLog; _ctx = ctx; _log = log;
    }

    public BatchJob Start(IEnumerable<string> customerIds, bool forceFresh, string sessionId,
        string? providerOverride = null, string? modelOverride = null, string? apiKeyOverride = null)
    {
        var job = _jobs.Create(customerIds);
        job.TenantId = _sessions.Get(sessionId)?.TenantId ?? "";   // stream/cancel verify tenant
        job.Status = "processing";

        // 3 override apply CHO TẤT CẢ KH trong batch — nhất quán result, dễ so sánh batch.
        _ = RunAsync(job, forceFresh, sessionId, providerOverride, modelOverride, apiKeyOverride);
        return job;
    }

    private async Task RunAsync(BatchJob job, bool forceFresh, string sessionId,
        string? providerOverride, string? modelOverride, string? apiKeyOverride)
    {
        var ct = job.Cts.Token;
        var tenantId = _sessions.Get(sessionId)?.TenantId ?? "";

        // AsyncLocal: endpoint return rồi → HttpContext null → providers thấy unknown/null tenant.
        // Push override để AI usage log đúng "reviews" + quota consume đúng tenant per AI call.
        using var _ctxScope = _ctx.Push("reviews", tenantId, sessionId);

        // Trace cho batch (AsyncLocal flow từ endpoint /reviews/batch). Mỗi review per-KH set
        // SetWorkflow("CustomerReview") sẽ ghi đè — set lần cuối ở đây thành "CustomerReviewBatch"
        // KHÔNG hoạt động vì ReviewService set sau. Cho nên dùng SetMeta để mark "batch" thay vì SetWorkflow.
        var trace = _traceAccessor.Current;
        trace?.SetMeta("batchJobId", job.Id);
        trace?.SetMeta("batchTotal", job.Total);
        trace?.SetMeta("tenant", tenantId);
        if (providerOverride != null) trace?.SetMeta("batchProviderOverride", providerOverride);
        if (modelOverride    != null) trace?.SetMeta("batchModelOverride", modelOverride);

        await job.Events.Writer.WriteAsync(new BatchEvent("start", Payload: new { total = job.Total }));

        try
        {
            await Parallel.ForEachAsync(
                job.CustomerIds,
                new ParallelOptions { MaxDegreeOfParallelism = CONCURRENCY, CancellationToken = ct },
                async (id, innerCt) =>
                {
                    if (innerCt.IsCancellationRequested) return;

                    // DÙNG CONTEXT (base+orders+comments) — đồng nhất với /reviews/customer/{id} + workflow
                    // → fingerprint match giữa 3 luồng → không re-review nhầm.
                    var contexts = await _source.GetContextsAsync(sessionId, new[] { id }, innerCt);
                    var customer = contexts.FirstOrDefault();
                    if (customer == null)
                    {
                        Interlocked.Increment(ref job.Errors);
                        await job.Events.Writer.WriteAsync(new BatchEvent("error", CustomerId: id, Error: "Không tìm thấy KH"), innerCt);
                        return;
                    }

                    try
                    {
                        // Lifecycle callback: bridge ReviewService stage → BatchEvent qua channel.
                        // Mỗi KH sẽ phát preparing → calling → chunk... → parsing → progress.
                        async Task OnStage(string stage, string? delta)
                        {
                            await job.Events.Writer.WriteAsync(new BatchEvent(
                                Type:       stage,                            // preparing/calling/chunk/parsing
                                CustomerId: id,
                                Payload:    stage == "chunk" ? new { delta } : null
                            ), innerCt);
                        }

                        var (review, fromCache) = await _reviews.ReviewAsync(
                            customer, tenantId, forceFresh, OnStage,
                            providerOverride: providerOverride,
                            modelOverride:    modelOverride,
                            apiKeyOverride:   apiKeyOverride,
                            ct:               innerCt);

                        // Atomic — concurrency=10 threads cùng update; ++ thường mất tick
                        if (fromCache) Interlocked.Increment(ref job.Cached);
                        Interlocked.Increment(ref job.Done);

                        await job.Events.Writer.WriteAsync(new BatchEvent(
                            Type: fromCache ? "cached" : "progress",
                            CustomerId: id,
                            Payload: new
                            {
                                rank        = review.Rank,
                                summaryLine = review.SummaryLine,
                                alertLevel  = review.Alert.Level,
                                done        = job.Done,
                                total       = job.Total
                            }
                        ), innerCt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[batch] review KH {Id} failed", id);
                        Interlocked.Increment(ref job.Errors);
                        await job.Events.Writer.WriteAsync(new BatchEvent("error", CustomerId: id, Error: ex.Message), innerCt);
                    }
                });

            job.Status = "done";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            _log.LogInformation("[batch] {Id} cancelled tại {Done}/{Total}", job.Id, job.Done, job.Total);
        }
        catch (Exception ex)
        {
            job.Status = "error";
            _log.LogError(ex, "[batch] {Id} crashed", job.Id);
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            await job.Events.Writer.WriteAsync(new BatchEvent(
                Type: job.Status,
                Payload: new { done = job.Done, errors = job.Errors, cached = job.Cached, total = job.Total }
            ));

            // Trace cho batch: AsyncLocal flow qua Task.Run từ endpoint → traceFinal.Current giờ KHÔNG null.
            // Manually log vào workflow-traces.jsonl + emit qua SSE channel cuối (nếu debug ON).
            var traceFinal = _traceAccessor.Current;
            if (traceFinal?.Enabled == true)
            {
                try
                {
                    var built = traceFinal.Build();
                    _traceLog.Append(ctx: null, built);   // log to JSONL (background, no HttpContext)
                    await job.Events.Writer.WriteAsync(new BatchEvent(
                        Type: "trace", Payload: built
                    ));
                }
                catch (Exception ex) { _log.LogWarning(ex, "[batch] log trace fail"); }
            }

            job.Events.Writer.Complete();
        }
    }

    public bool Cancel(string jobId)
    {
        var job = _jobs.Get(jobId);
        if (job == null) return false;
        if (job.Status != "processing") return false;
        job.Cts.Cancel();
        return true;
    }

}
