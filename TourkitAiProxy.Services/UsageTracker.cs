namespace TourkitAiProxy.Services;

/// <summary>
/// AI usage tracker — daily counter SQL (`dbo.AiUsageCounters`) cho cross-process.
/// In-mem snapshot cache (TTL 10s) để endpoint /api/v1/usage không hit DB mỗi request.
/// Cost estimate hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) — giữ shape cũ.
///
/// Track(): fire-and-forget UPSERT vào SQL (KHÔNG block AI call).
/// Snapshot(): đọc cache nếu còn hạn; nếu không → load SQL → cache lại.
/// </summary>
public class UsageTracker
{
    private readonly UsageRepository _repo;
    private readonly ILogger<UsageTracker> _log;

    // Cache snapshot 10s — đủ tươi cho dashboard, đủ hiệu quả để khỏi nuốt DB
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly object _cacheLock = new();
    private object? _cachedSnapshot;
    private DateTime _cachedAt = DateTime.MinValue;

    public UsageTracker(UsageRepository repo, ILogger<UsageTracker> log)
    {
        _repo = repo; _log = log;
    }

    /// Append 1 call vào SQL counter. Fire-and-forget: không await trong AiEndpoints
    /// vì lỗi log usage KHÔNG được phép fail AI call.
    public void Track(string model, int inTok, int outTok, long ms)
    {
        _ = _repo.AppendAsync(model, inTok, outTok, ms);
        lock (_cacheLock) _cachedAt = DateTime.MinValue;
    }

    /// Snapshot tổng hợp 30 ngày gần nhất, format giống endpoint cũ.
    public object Snapshot()
    {
        lock (_cacheLock)
        {
            if (_cachedSnapshot != null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cachedSnapshot;
        }

        List<UsageRepository.CounterRow> rows;
        try
        {
            rows = _repo.ReadAggregateAsync(daysBack: 30).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[UsageTracker] Read SQL fail → trả snapshot rỗng");
            rows = new List<UsageRepository.CounterRow>();
        }

        var snap = FormatSnapshot(rows);
        lock (_cacheLock) { _cachedSnapshot = snap; _cachedAt = DateTime.UtcNow; }
        return snap;
    }

    /// Pure-logic format helper — tách ra để unit test (UsageSnapshotFormatTests).
    /// Giữ shape giống endpoint cũ: { calls, inputTokens, outputTokens, avgLatencyMs, estimatedCostUsd, byModel }.
    public static object FormatSnapshot(List<UsageRepository.CounterRow> rows)
    {
        long calls = 0, inTok = 0, outTok = 0, totMs = 0;
        var byModel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            calls  += r.Calls;
            inTok  += r.InTokens;
            outTok += r.OutTokens;
            totMs  += r.TotalLatencyMs;
            byModel[r.Model] = (byModel.TryGetValue(r.Model, out var v) ? v : 0) + r.Calls;
        }
        var costUsd = (inTok * 0.27 + outTok * 1.10) / 1_000_000.0;
        return new
        {
            calls            = calls,
            inputTokens      = inTok,
            outputTokens     = outTok,
            avgLatencyMs     = calls == 0 ? 0L : totMs / calls,
            estimatedCostUsd = Math.Round(costUsd, 4),
            byModel          = byModel
        };
    }
}
