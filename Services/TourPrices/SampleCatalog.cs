namespace TourkitAiProxy.Services.TourPrices;

/// NCC mẫu (dữ liệu hệ thống) lưu trong dbo.TourPriceCatalog với TenantId dành riêng này.
/// Reserved — tenant thật là domain (vd erp.tourkit.vn) nên không bao giờ trùng "__sample__".
public static class SampleCatalog
{
    public const string TenantId = "__sample__";
    public static bool IsSample(string? tenantId) => tenantId == TenantId;
}
