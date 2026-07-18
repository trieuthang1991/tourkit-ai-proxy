using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.TourPrices;

/// <summary>
/// Dapper CRUD cho `dbo.TourPriceCatalog` — 1 nguồn persistence của catalog.
/// Chỉ đọc từ TourKit, không bao giờ ghi ngược.
/// </summary>
public class TourPriceCatalogRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TourPriceCatalogRepository> _log;

    public TourPriceCatalogRepository(TourkitAiDb db, ILogger<TourPriceCatalogRepository> log)
    {
        _db = db; _log = log;
    }

    /// Upsert 1 mẻ. MERGE theo PK (TenantId, PricingId). Trả số dòng đã ghi.
    public async Task<int> UpsertBatchAsync(IReadOnlyList<CatalogRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const string sql = @"
MERGE dbo.TourPriceCatalog AS t
USING (SELECT @TenantId AS TenantId, @PricingId AS PricingId) AS s
    ON t.TenantId = s.TenantId AND t.PricingId = s.PricingId
WHEN MATCHED THEN UPDATE SET
    ProviderServiceId = @ProviderServiceId, ProviderId = @ProviderId,
    ProviderName = @ProviderName, ProviderCode = @ProviderCode,
    City = @City, CityNorm = @CityNorm,
    CategoryId = @CategoryId, CategoryName = @CategoryName,
    PriceName = @PriceName, Description = @Description,
    ContractPrice = @ContractPrice, PublicPrice = @PublicPrice,
    Stars = @Stars, IsActive = 1, SyncedUtc = @SyncedUtc
WHEN NOT MATCHED THEN INSERT
    (TenantId, PricingId, ProviderServiceId, ProviderId, ProviderName, ProviderCode,
     City, CityNorm, CategoryId, CategoryName, PriceName, Description,
     ContractPrice, PublicPrice, Stars, IsActive, SyncedUtc)
    VALUES (@TenantId, @PricingId, @ProviderServiceId, @ProviderId, @ProviderName, @ProviderCode,
     @City, @CityNorm, @CategoryId, @CategoryName, @PriceName, @Description,
     @ContractPrice, @PublicPrice, @Stars, 1, @SyncedUtc);";

        var now = DateTime.UtcNow;   // UTC — STRICT (docs/datetime-convention.md)
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, rows.Select(r => new
        {
            r.TenantId, r.PricingId, r.ProviderServiceId, r.ProviderId,
            r.ProviderName, r.ProviderCode, r.City, r.CityNorm,
            r.CategoryId, r.CategoryName, r.PriceName, r.Description,
            r.ContractPrice, r.PublicPrice, r.Stars,
            SyncedUtc = now
        }));
    }

    /// Tắt cờ các dòng KHÔNG được chạm trong lần sync này (NCC đã xóa/ngừng bên TourKit).
    /// KHÔNG xóa dòng — giữ lịch sử, và báo giá cũ còn tham chiếu PricingId.
    public async Task<int> DeactivateMissingAsync(string tenantId, DateTime syncedFromUtc, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.TourPriceCatalog SET IsActive = 0
WHERE TenantId = @tenantId AND IsActive = 1 AND SyncedUtc < @from;";
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new { tenantId, from = syncedFromUtc });
    }

    /// Đếm dòng đang hiệu lực (nghiệm thu + log).
    public async Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM dbo.TourPriceCatalog WHERE TenantId = @tenantId AND IsActive = 1",
            new { tenantId });
    }
}
