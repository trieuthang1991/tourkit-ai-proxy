using System.Collections.Concurrent;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews;

/// In-memory store cho batch jobs đang chạy. Không persist qua restart — sau restart job
/// pending sẽ mất, client cần trigger lại. Đủ cho scope MVP.
public class BatchJobStore
{
    private readonly ConcurrentDictionary<string, BatchJob> _jobs = new();

    public BatchJob Create(IEnumerable<string> customerIds)
    {
        var job = new BatchJob { CustomerIds = customerIds.ToList() };
        _jobs[job.Id] = job;
        return job;
    }

    public BatchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public void Remove(string id) => _jobs.TryRemove(id, out _);
}
