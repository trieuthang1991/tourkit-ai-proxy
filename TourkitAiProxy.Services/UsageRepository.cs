using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services;

/// <summary>
/// Dapper repo cho dbo.AiUsageCounters. Aggregate daily per-model — Snapshot rẻ.
/// AppendAsync: UPSERT (date, model) +1 call, +inTok, +outTok, +latency.
/// SnapshotAsync: SELECT tổng tất cả ngày → object cùng shape với endpoint /api/v1/usage cũ.
///
/// Tách thành class riêng để UsageTracker chỉ orchestrate (cache + delegate).
/// Không cache trong repo — UsageTracker giữ in-mem snapshot riêng.
/// </summary>
public class UsageRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<UsageRepository> _log;

    public UsageRepository(TourkitAiDb db, ILogger<UsageRepository> log)
    {
        _db = db; _log = log;
    }

    /// UPSERT 1 call vào counter của ngày hôm nay × model.
    public async Task AppendAsync(string model, int inTok, int outTok, long ms, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model)) model = "unknown";
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
MERGE dbo.AiUsageCounters AS T
USING (SELECT CAST(SYSUTCDATETIME() AS DATE) AS DateUtc, @Model AS Model) AS S
   ON T.DateUtc = S.DateUtc AND T.Model = S.Model
WHEN MATCHED THEN UPDATE SET
    Calls          = T.Calls + 1,
    InTokens       = T.InTokens + @In,
    OutTokens      = T.OutTokens + @Out,
    TotalLatencyMs = T.TotalLatencyMs + @Ms,
    UpdatedUtc     = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (DateUtc, Model, Calls, InTokens, OutTokens, TotalLatencyMs, UpdatedUtc)
VALUES
    (S.DateUtc, S.Model, 1, @In, @Out, @Ms, SYSUTCDATETIME());",
                new { Model = model, In = (long)inTok, Out = (long)outTok, Ms = ms });
        }
        catch (Exception ex)
        {
            // Không throw — usage tracking là phụ, không được làm fail AI call
            _log.LogWarning(ex, "[UsageRepo] Append model={Model} lỗi", model);
        }
    }

    public sealed record CounterRow(string Model, long Calls, long InTokens, long OutTokens, long TotalLatencyMs);

    /// Trả về list rows aggregate trên N ngày gần đây (default 30).
    /// UsageTracker dùng để build Snapshot.
    public async Task<List<CounterRow>> ReadAggregateAsync(int daysBack = 30, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<CounterRow>(@"
SELECT Model,
       SUM(Calls)          AS Calls,
       SUM(InTokens)       AS InTokens,
       SUM(OutTokens)      AS OutTokens,
       SUM(TotalLatencyMs) AS TotalLatencyMs
FROM dbo.AiUsageCounters
WHERE DateUtc >= DATEADD(DAY, -@d, CAST(SYSUTCDATETIME() AS DATE))
GROUP BY Model
ORDER BY Calls DESC;",
                new { d = daysBack });
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[UsageRepo] ReadAggregate lỗi");
            return new List<CounterRow>();
        }
    }
}
