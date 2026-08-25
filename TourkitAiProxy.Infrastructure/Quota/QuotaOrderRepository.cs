using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Dapper repo cho dbo.QuotaOrders với fallback in-memory.
///   • Insert/Get/MarkPaid/Cancel/ExpireStale/List — primary path = SQL.
///   • Fallback: nếu SQL connect/query throw SqlException → flip `_sqlDown` sticky → switch sang in-mem dict.
///     Tránh: nếu DB liên tục timeout 15s/request → toàn bộ tenant đông cứng. Sticky cho phép dev local
///     không có SQL vẫn test full luồng nạp quota; prod có DB thì luôn hit primary.
///   • TryMarkPaid vẫn atomic-by-lock cho in-mem (Interlocked compare).
/// Sticky reset: 60s sau lần fail đầu — cho phép retry SQL nếu DB lên lại trong session.
/// </summary>
public class QuotaOrderRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<QuotaOrderRepository> _log;

    // Fallback store khi SQL down. Key = OrderId.
    private readonly ConcurrentDictionary<string, OrderRow> _mem = new();
    private readonly object _stickyLock = new();
    private DateTime _sqlDownUntil = DateTime.MinValue;

    public QuotaOrderRepository(TourkitAiDb db, ILogger<QuotaOrderRepository> log)
    { _db = db; _log = log; }

    public record OrderRow(
        string Id, string TenantId, string TierId, long AmountVnd, int QuotaUnits,
        string Status, string? QrPayload, string? BankBin, string? AccountNumber, string? AccountName,
        string Memo, DateTime ExpiresAt, DateTime CreatedAt, DateTime? PaidAt,
        string? TingeeRefId, string? TingeeRaw, string? CreatedBy
    );

    private bool ShouldUseSql()
    {
        lock (_stickyLock) return DateTime.UtcNow > _sqlDownUntil;
    }

    private void MarkSqlDown(Exception ex)
    {
        lock (_stickyLock) _sqlDownUntil = DateTime.UtcNow.AddSeconds(60);
        _log.LogWarning(ex, "[QuotaOrders] SQL không kết nối được — fallback in-memory 60s. (Dev local? Khởi động SQL Server.)");
    }

    // ─── Insert ─────────────────────────────────────────────────────────────────
    public async Task InsertAsync(OrderRow row, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
INSERT INTO dbo.QuotaOrders
    (Id, TenantId, TierId, AmountVnd, QuotaUnits, Status, QrPayload,
     BankBin, AccountNumber, AccountName, Memo, ExpiresAt, CreatedAt,
     PaidAt, TingeeRefId, TingeeRaw, CreatedBy)
VALUES
    (@Id, @TenantId, @TierId, @AmountVnd, @QuotaUnits, @Status, @QrPayload,
     @BankBin, @AccountNumber, @AccountName, @Memo, @ExpiresAt, @CreatedAt,
     @PaidAt, @TingeeRefId, @TingeeRaw, @CreatedBy);";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, row);
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        _mem[row.Id] = row;
    }

    // ─── GetById ────────────────────────────────────────────────────────────────
    public async Task<OrderRow?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = "SELECT * FROM dbo.QuotaOrders WHERE Id = @id;";
                await using var c = await _db.OpenAsync(ct);
                return await c.QuerySingleOrDefaultAsync<OrderRow>(sql, new { id });
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        return _mem.GetValueOrDefault(id);
    }

    // ─── TryMarkPaid (atomic) ───────────────────────────────────────────────────
    public async Task<OrderRow?> TryMarkPaidAsync(string id, string? tingeeRefId, string? rawJson, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string atomicSql = @"
UPDATE dbo.QuotaOrders
   SET Status = 'paid', PaidAt = SYSUTCDATETIME(),
       TingeeRefId = @tingeeRefId, TingeeRaw = @rawJson
   OUTPUT inserted.*
 WHERE Id = @id AND Status = 'pending';";
                await using var c = await _db.OpenAsync(ct);
                return await c.QuerySingleOrDefaultAsync<OrderRow>(atomicSql, new { id, tingeeRefId, rawJson });
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        // In-mem atomic: ConcurrentDictionary.TryUpdate so sánh full row → chỉ 1 caller thắng.
        if (!_mem.TryGetValue(id, out var current)) return null;
        if (current.Status != "pending") return null;
        var paid = current with { Status = "paid", PaidAt = DateTime.UtcNow,
                                  TingeeRefId = tingeeRefId, TingeeRaw = rawJson };
        return _mem.TryUpdate(id, paid, current) ? paid : null;
    }

    // ─── Cancel ─────────────────────────────────────────────────────────────────
    public async Task CancelAsync(string id, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
UPDATE dbo.QuotaOrders SET Status = 'cancelled'
 WHERE Id = @id AND Status = 'pending';";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, new { id });
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        if (_mem.TryGetValue(id, out var row) && row.Status == "pending")
            _mem.TryUpdate(id, row with { Status = "cancelled" }, row);
    }

    // ─── ExpireStale ────────────────────────────────────────────────────────────
    public async Task ExpireStaleAsync(CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
UPDATE dbo.QuotaOrders SET Status = 'expired'
 WHERE Status = 'pending' AND ExpiresAt < SYSUTCDATETIME();";
                await using var c = await _db.OpenAsync(ct);
                var n = await c.ExecuteAsync(sql);
                if (n > 0) _log.LogInformation("[QuotaOrders] {N} đơn quá hạn → expired", n);
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        var now = DateTime.UtcNow;
        foreach (var kv in _mem.ToArray())
            if (kv.Value.Status == "pending" && kv.Value.ExpiresAt < now)
                _mem.TryUpdate(kv.Key, kv.Value with { Status = "expired" }, kv.Value);
    }

    // ─── ListByTenant ───────────────────────────────────────────────────────────
    public async Task<List<OrderRow>> ListByTenantAsync(string tenantId, int limit = 50, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
SELECT TOP (@limit) * FROM dbo.QuotaOrders
 WHERE TenantId = @tenantId ORDER BY CreatedAt DESC;";
                await using var c = await _db.OpenAsync(ct);
                var rows = await c.QueryAsync<OrderRow>(sql, new { tenantId, limit });
                return rows.ToList();
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        return _mem.Values.Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    // ─── ListAll (admin) ────────────────────────────────────────────────────────
    public async Task<List<OrderRow>> ListAllAsync(int limit = 200, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
SELECT TOP (@limit) * FROM dbo.QuotaOrders ORDER BY CreatedAt DESC;";
                await using var c = await _db.OpenAsync(ct);
                var rows = await c.QueryAsync<OrderRow>(sql, new { limit });
                return rows.ToList();
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        return _mem.Values.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }
}
