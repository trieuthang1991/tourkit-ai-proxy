using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TourkitAiProxy.Services.Deals;

/// 1 lần chạy phân tích pipeline. Events stream qua Channel → SSE. In-memory, mất khi restart (MVP).
public class DealBatchJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = "";       // set ở DealBatchService.Start — stream/cancel verify đúng tenant
    public string Status { get; set; } = "queued";   // queued/processing/done/cancelled/error
    public int Total { get; set; }                    // số deal được chấm sâu (biết sau khi rank)
    public int Done { get; set; }
    public int Errors { get; set; }
    public DateTime? FinishedAt { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public Channel<DealBatchEvent> Events { get; } = Channel.CreateUnbounded<DealBatchEvent>();
}

public record DealBatchEvent(string Type, object? Payload = null, string? Error = null);

public class DealBatchJobStore
{
    private readonly ConcurrentDictionary<string, DealBatchJob> _jobs = new();
    public DealBatchJob Create() { var j = new DealBatchJob(); _jobs[j.Id] = j; return j; }
    public DealBatchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public void Remove(string id) => _jobs.TryRemove(id, out _);
}
