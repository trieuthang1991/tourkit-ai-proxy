using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews;

/// Chạy batch review song song. Mỗi customer → 1 task; concurrency cap 10 (configurable).
/// Result push qua BatchJob.Events channel → SSE handler đọc và gửi event xuống client.
/// Cancel: token nội bộ; client gọi POST /batch/{id}/cancel hoặc đóng SSE → ngừng.
public class BatchService
{
    private readonly CustomerRepository _customers;
    private readonly ReviewService _reviews;
    private readonly BatchJobStore _jobs;
    private readonly ILogger<BatchService> _log;

    private const int CONCURRENCY = 10;

    public BatchService(CustomerRepository customers, ReviewService reviews, BatchJobStore jobs, ILogger<BatchService> log)
    {
        _customers = customers; _reviews = reviews; _jobs = jobs; _log = log;
    }

    public BatchJob Start(IEnumerable<string> customerIds, bool forceFresh)
    {
        var job = _jobs.Create(customerIds);
        job.Status = "processing";

        _ = RunAsync(job, forceFresh);   // fire-and-forget; SSE handler consume Events channel
        return job;
    }

    private async Task RunAsync(BatchJob job, bool forceFresh)
    {
        var ct = job.Cts.Token;

        await job.Events.Writer.WriteAsync(new BatchEvent("start", Payload: new { total = job.Total }));

        try
        {
            await Parallel.ForEachAsync(
                job.CustomerIds,
                new ParallelOptions { MaxDegreeOfParallelism = CONCURRENCY, CancellationToken = ct },
                async (id, innerCt) =>
                {
                    if (innerCt.IsCancellationRequested) return;

                    var customer = _customers.Get(id);
                    if (customer == null)
                    {
                        Interlocked.Increment(ref _errors);
                        job.Errors++;
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

                        var (review, fromCache) = await _reviews.ReviewAsync(customer, forceFresh, OnStage, innerCt);

                        if (fromCache) job.Cached++;
                        job.Done++;

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
                        job.Errors++;
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

    // Field placeholder cho Interlocked usage (not actually needed since job.Errors is single-writer in this design).
#pragma warning disable CS0414
    private int _errors;
#pragma warning restore CS0414
}
