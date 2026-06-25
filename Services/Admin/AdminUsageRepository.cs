using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Aggregate cross-tenant trên dbo.AiUsageHistory (granular per-request).
/// Schema: Ts DATETIME2, Feature, SessionId, Tenant NVARCHAR(128) NULL, Provider, Model,
///         InTok INT, OutTok INT, LatencyMs BIGINT, CostVnd BIGINT, Cached BIT, Status NVARCHAR(32).
/// Index sẵn: IX_AiUsageHistory_Ts(DESC) + IX_AiUsageHistory_Tenant_Ts.
///
/// 4 query: totals / byModel / byTenant / byDay. Tất cả filter Status='ok' + Ts trong range.
/// Tenant IS NULL → group thành '(system)' (call không có session — system task).
/// </summary>
public class AdminUsageRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<AdminUsageRepository> _log;
    public const string SystemTenantKey = "(system)";

    public AdminUsageRepository(TourkitAiDb db, ILogger<AdminUsageRepository> log)
    {
        _db = db; _log = log;
    }

    public sealed record TotalsRow(long Calls, long InTokens, long OutTokens, long CostVnd);
    public sealed record ModelRow(string Model, long Calls, long InTokens, long OutTokens, long CostVnd);
    public sealed record TenantRow(string TenantId, long Calls, long InTokens, long OutTokens, long CostVnd, DateTime? LastCallAt);
    public sealed record DayRow(DateTime Date, long Calls, long CostVnd);

    public async Task<TotalsRow> GetTotalsAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var row = await c.QueryFirstOrDefaultAsync<TotalsRow>($@"
SELECT COUNT_BIG(*) AS Calls,
       ISNULL(SUM(CAST(InTok  AS BIGINT)), 0) AS InTokens,
       ISNULL(SUM(CAST(OutTok AS BIGINT)), 0) AS OutTokens,
       ISNULL(SUM(CostVnd), 0)                AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where};", parms);
            return row ?? new TotalsRow(0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetTotals lỗi");
            return new TotalsRow(0, 0, 0, 0);
        }
    }

    public async Task<List<ModelRow>> GetByModelAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<ModelRow>($@"
SELECT Model,
       COUNT_BIG(*) AS Calls,
       SUM(CAST(InTok  AS BIGINT)) AS InTokens,
       SUM(CAST(OutTok AS BIGINT)) AS OutTokens,
       SUM(CostVnd)                AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY Model
ORDER BY CostVnd DESC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByModel lỗi");
            return new();
        }
    }

    public async Task<List<TenantRow>> GetByTenantAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        // tenantId filter: nếu có → chỉ group 1 tenant (TenantRow đơn lẻ).
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<TenantRow>($@"
SELECT ISNULL(Tenant, '{SystemTenantKey}') AS TenantId,
       COUNT_BIG(*) AS Calls,
       SUM(CAST(InTok  AS BIGINT)) AS InTokens,
       SUM(CAST(OutTok AS BIGINT)) AS OutTokens,
       SUM(CostVnd)                AS CostVnd,
       MAX(Ts)                     AS LastCallAt
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY ISNULL(Tenant, '{SystemTenantKey}')
ORDER BY CostVnd DESC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByTenant lỗi");
            return new();
        }
    }

    public async Task<List<DayRow>> GetByDayAsync(DateTime fromUtc, DateTime toUtc, string? tenantId, CancellationToken ct = default)
    {
        var (where, parms) = BuildFilter(fromUtc, toUtc, tenantId);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<DayRow>($@"
SELECT CAST(Ts AS DATE) AS [Date],
       COUNT_BIG(*)     AS Calls,
       SUM(CostVnd)     AS CostVnd
FROM dbo.AiUsageHistory
WHERE {where}
GROUP BY CAST(Ts AS DATE)
ORDER BY [Date] ASC;", parms);
            return rows.AsList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AdminUsageRepo] GetByDay lỗi");
            return new();
        }
    }

    private static (string Where, object Parms) BuildFilter(DateTime fromUtc, DateTime toUtc, string? tenantId)
    {
        // Status='ok' để khỏi double-count nếu provider retry (failed call cũng append vào history).
        var where = "Ts >= @from AND Ts < @to AND Status = 'ok'";
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            // tenantId='(system)' → match Tenant IS NULL
            if (string.Equals(tenantId, SystemTenantKey, StringComparison.Ordinal))
                where += " AND Tenant IS NULL";
            else
                where += " AND Tenant = @tenant";
        }
        return (where, new { from = fromUtc, to = toUtc, tenant = tenantId });
    }
}
