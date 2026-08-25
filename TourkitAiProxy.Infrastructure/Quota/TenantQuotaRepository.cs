using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Dapper repo cho dbo.TenantQuota. CRUD thuần cho TenantQuotaStore — KHÔNG cache, KHÔNG fallback.
/// Caller (Store) giữ in-mem cache + lo race condition trong-process.
///
/// Cross-instance race-safe nhờ atomic UPDATE ở DB level (Consume/TopUp dùng OUTPUT inserted để
/// đọc giá trị mới nhất sau khi cộng → tránh dirty read).
/// </summary>
public class TenantQuotaRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantQuotaRepository> _log;

    public TenantQuotaRepository(TourkitAiDb db, ILogger<TenantQuotaRepository> log)
    {
        _db = db; _log = log;
    }

    private sealed class Row
    {
        public string TenantId { get; set; } = "";
        public int Limit { get; set; }
        public int Used { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// Load toàn bộ quota → dict in-mem (chạy lúc startup).
    public async Task<Dictionary<string, QuotaState>> LoadAllAsync(CancellationToken ct = default)
    {
        var dict = new Dictionary<string, QuotaState>(StringComparer.Ordinal);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<Row>(
                "SELECT TenantId, [Limit], Used, UpdatedAt FROM dbo.TenantQuota");
            foreach (var r in rows)
                dict[r.TenantId] = new QuotaState(r.Limit, r.Used, r.UpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantQuotaRepo] LoadAll lỗi");
        }
        return dict;
    }

    /// Seed tenant mới với DefaultLimit nếu chưa có. Idempotent. Trả state hiện tại.
    public async Task<QuotaState> SeedIfMissingAsync(string tenantId, int defaultLimit, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var row = await c.QueryFirstOrDefaultAsync<Row>(@"
MERGE dbo.TenantQuota AS T
USING (SELECT @tenantId AS TenantId) AS S ON T.TenantId = S.TenantId
WHEN NOT MATCHED THEN INSERT (TenantId, [Limit], Used, UpdatedAt)
    VALUES (@tenantId, @defaultLimit, 0, SYSUTCDATETIME())
OUTPUT inserted.TenantId, inserted.[Limit], inserted.Used, inserted.UpdatedAt;",
                new { tenantId, defaultLimit });

            // MERGE OUTPUT chỉ trả khi WHEN NOT MATCHED fire. Nếu đã tồn tại → query lại.
            if (row != null) return new QuotaState(row.Limit, row.Used, row.UpdatedAt);

            var existing = await c.QueryFirstAsync<Row>(
                "SELECT TenantId, [Limit], Used, UpdatedAt FROM dbo.TenantQuota WHERE TenantId = @tenantId",
                new { tenantId });
            return new QuotaState(existing.Limit, existing.Used, existing.UpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantQuotaRepo] SeedIfMissing {Tenant} lỗi", tenantId);
            // Fallback: trả state mặc định in-mem để store vẫn chạy được khi DB tạm lỗi.
            return new QuotaState(defaultLimit, 0, DateTime.UtcNow);
        }
    }

    /// Atomic Consume: UPDATE SET Used = Used + @count → trả state mới. Race-safe cross-instance.
    /// Nếu tenant chưa có → SeedIfMissing trước rồi UPDATE.
    public async Task<QuotaState> ConsumeAsync(string tenantId, int count, int defaultLimit, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            // Một roundtrip: MERGE seed nếu chưa có; sau đó UPDATE + OUTPUT.
            // Dùng 2 statement riêng để OUTPUT clean (MERGE OUTPUT phức tạp khi có UPDATE+INSERT).
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.TenantQuota WHERE TenantId = @tenantId)
    INSERT INTO dbo.TenantQuota (TenantId, [Limit], Used, UpdatedAt)
    VALUES (@tenantId, @defaultLimit, 0, SYSUTCDATETIME());",
                new { tenantId, defaultLimit });

            var row = await c.QueryFirstAsync<Row>(@"
UPDATE dbo.TenantQuota
SET Used = Used + @count, UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.TenantId, inserted.[Limit], inserted.Used, inserted.UpdatedAt
WHERE TenantId = @tenantId;",
                new { tenantId, count });

            return new QuotaState(row.Limit, row.Used, row.UpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TenantQuotaRepo] Consume {Tenant} +{Count} lỗi", tenantId, count);
            throw;
        }
    }

    /// Atomic TopUp: UPDATE SET Limit = Limit + @add. Seed nếu chưa có (Limit = defaultLimit + add).
    public async Task<QuotaState> TopUpAsync(string tenantId, int add, int defaultLimit, CancellationToken ct = default)
    {
        if (add <= 0) throw new ArgumentOutOfRangeException(nameof(add), "add phải > 0");
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.TenantQuota WHERE TenantId = @tenantId)
    INSERT INTO dbo.TenantQuota (TenantId, [Limit], Used, UpdatedAt)
    VALUES (@tenantId, @defaultLimit, 0, SYSUTCDATETIME());",
                new { tenantId, defaultLimit });

            var row = await c.QueryFirstAsync<Row>(@"
UPDATE dbo.TenantQuota
SET [Limit] = [Limit] + @add, UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.TenantId, inserted.[Limit], inserted.Used, inserted.UpdatedAt
WHERE TenantId = @tenantId;",
                new { tenantId, add });

            return new QuotaState(row.Limit, row.Used, row.UpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TenantQuotaRepo] TopUp {Tenant} +{Add} lỗi", tenantId, add);
            throw;
        }
    }

    /// Set state cho 1 tenant (upsert). Dùng cho migration từ file legacy.
    public async Task UpsertAsync(string tenantId, QuotaState state, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
MERGE dbo.TenantQuota AS T
USING (SELECT @tenantId AS TenantId) AS S ON T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    [Limit]   = @Limit,
    Used      = @Used,
    UpdatedAt = @UpdatedAt
WHEN NOT MATCHED THEN INSERT (TenantId, [Limit], Used, UpdatedAt)
    VALUES (@tenantId, @Limit, @Used, @UpdatedAt);",
                new { tenantId, state.Limit, state.Used, state.UpdatedAt });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TenantQuotaRepo] Upsert {Tenant} lỗi", tenantId);
            throw;
        }
    }
}
