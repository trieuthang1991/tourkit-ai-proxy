using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services;

/// <summary>
/// Dapper repo cho dbo.AiUsageHistory — granular per-request log (1 AI call = 1 row).
///
/// AppendAsync: fire-and-forget INSERT 1 row (gọi từ hot path). KHÔNG throw — tracking là phụ.
/// BulkInsertAsync: SqlBulkCopy cho one-shot migration JSONL → SQL (1653 rows in ~1 giây thay vì 16 giây).
/// GetMaxTsAsync: dedup migration — chỉ import rows có Ts > maxTs trong SQL.
/// ReadRecentAsync: SELECT TOP N ORDER BY Ts DESC → endpoint dashboard, snapshot.
/// </summary>
public class AiUsageHistoryRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<AiUsageHistoryRepository> _log;

    public AiUsageHistoryRepository(TourkitAiDb db, ILogger<AiUsageHistoryRepository> log)
    {
        _db = db; _log = log;
    }

    /// Insert 1 row. Caller dùng fire-and-forget: `_ = repo.AppendAsync(...)`.
    public async Task AppendAsync(AiUsageLog.Entry e, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
INSERT INTO dbo.AiUsageHistory
    (Ts, Feature, SessionId, Tenant, Provider, Model, InTok, OutTok, LatencyMs, CostVnd, Cached, Status)
VALUES
    (@Ts, @Feature, @SessionId, @Tenant, @Provider, @Model, @InTok, @OutTok, @LatencyMs, @CostVnd, @Cached, @Status);",
                new
                {
                    Ts = ParseTs(e.Timestamp),
                    e.Feature,
                    e.SessionId,
                    e.Tenant,
                    e.Provider,
                    e.Model,
                    InTok = e.InputTokens,
                    OutTok = e.OutputTokens,
                    e.LatencyMs,
                    e.CostVnd,
                    e.Cached,
                    e.Status
                });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AiUsageHistory] Append ts={Ts} model={Model} lỗi", e.Timestamp, e.Model);
        }
    }

    /// SqlBulkCopy — nhanh hơn N×INSERT khi import file legacy (1653 rows trong ~1 giây).
    /// Trả số rows đã insert.
    public async Task<int> BulkInsertAsync(IEnumerable<AiUsageLog.Entry> entries, CancellationToken ct = default)
    {
        var tbl = new DataTable();
        tbl.Columns.Add("Ts", typeof(DateTime));
        tbl.Columns.Add("Feature", typeof(string));
        tbl.Columns.Add("SessionId", typeof(string));
        tbl.Columns.Add("Tenant", typeof(string));
        tbl.Columns.Add("Provider", typeof(string));
        tbl.Columns.Add("Model", typeof(string));
        tbl.Columns.Add("InTok", typeof(int));
        tbl.Columns.Add("OutTok", typeof(int));
        tbl.Columns.Add("LatencyMs", typeof(long));
        tbl.Columns.Add("CostVnd", typeof(long));
        tbl.Columns.Add("Cached", typeof(bool));
        tbl.Columns.Add("Status", typeof(string));

        int n = 0;
        foreach (var e in entries)
        {
            tbl.Rows.Add(
                ParseTs(e.Timestamp),
                e.Feature ?? "unknown",
                (object?)e.SessionId ?? DBNull.Value,
                (object?)e.Tenant ?? DBNull.Value,
                e.Provider ?? "?",
                e.Model ?? "?",
                e.InputTokens,
                e.OutputTokens,
                e.LatencyMs,
                e.CostVnd,
                e.Cached,
                e.Status ?? "ok");
            n++;
        }
        if (n == 0) return 0;

        try
        {
            await using var c = await _db.OpenAsync(ct);
            using var bulk = new SqlBulkCopy(c) { DestinationTableName = "dbo.AiUsageHistory", BatchSize = 500 };
            foreach (DataColumn col in tbl.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(tbl, ct);
            return n;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AiUsageHistory] BulkInsert {N} rows lỗi", n);
            return 0;
        }
    }

    /// Trả Ts lớn nhất trong bảng (UTC). null nếu bảng rỗng. Dùng để dedup migration.
    public async Task<DateTime?> GetMaxTsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.QueryFirstOrDefaultAsync<DateTime?>("SELECT MAX(Ts) FROM dbo.AiUsageHistory;");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AiUsageHistory] GetMaxTs lỗi");
            return null;
        }
    }

    /// Đọc N rows gần nhất (Ts DESC), trả về theo Entry shape (Timestamp = ISO-8601 UTC).
    public async Task<List<AiUsageLog.Entry>> ReadRecentAsync(int max, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<Row>(@"
SELECT TOP (@max) Ts, Feature, SessionId, Tenant, Provider, Model, InTok, OutTok, LatencyMs, CostVnd, Cached, Status
FROM dbo.AiUsageHistory
ORDER BY Ts DESC;", new { max });
            return rows
                .Reverse()  // caller endpoint sort lại DESC; trả về asc giống Read JSONL cũ
                .Select(r => new AiUsageLog.Entry(
                    DateTime.SpecifyKind(r.Ts, DateTimeKind.Utc).ToString("o"),
                    r.Feature, r.SessionId, r.Tenant, r.Provider, r.Model,
                    r.InTok, r.OutTok, r.LatencyMs, r.CostVnd, r.Cached, r.Status))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AiUsageHistory] ReadRecent lỗi");
            return new List<AiUsageLog.Entry>();
        }
    }

    private static DateTime ParseTs(string iso)
    {
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return DateTime.UtcNow;
    }

    private sealed class Row
    {
        public DateTime Ts { get; set; }
        public string Feature { get; set; } = "";
        public string? SessionId { get; set; }
        public string? Tenant { get; set; }
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public int InTok { get; set; }
        public int OutTok { get; set; }
        public long LatencyMs { get; set; }
        public long CostVnd { get; set; }
        public bool Cached { get; set; }
        public string Status { get; set; } = "ok";
    }
}
