using System.Text.Json;

namespace TourkitAiProxy.Services.TourKit;

/// Đọc NCC (nhà cung cấp) THẬT từ TourKit (picker dịch vụ dựng tour). Read-only, qua session JWT.
///   • CategoriesAsync       — `/api/tours/service-categories`  (loại DV: Khách sạn, Vận chuyển…)
///   • ProvidersByServiceAsync — `/api/tours/providers-by-service?serviceId=`  (NCC theo loại DV)
///   • ProviderServicesAsync — `/api/tours/providers/{id}/services`  (giá hợp đồng dịch vụ con của 1 NCC)
///   • ProvidersAsync        — `/api/tours/providers?marketId=`  (full NCC / HDV)
public class TourKitNccClient
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;

    public TourKitNccClient(TourKitApiClient api, TkSessionStore sessions)
    {
        _api = api; _sessions = sessions;
    }

    public Task<JsonElement> CategoriesAsync(string sessionId, CancellationToken ct)
        => GetAsync(sessionId, "/api/tours/service-categories", ct);

    public Task<JsonElement> ProvidersByServiceAsync(string sessionId, int serviceId, CancellationToken ct)
        => GetAsync(sessionId, $"/api/tours/providers-by-service?serviceId={serviceId}", ct);

    public Task<JsonElement> ProviderServicesAsync(string sessionId, int providerId, int? categoryId, CancellationToken ct)
        => GetAsync(sessionId, $"/api/tours/providers/{providerId}/services" + (categoryId.HasValue ? $"?serviceCategoryId={categoryId}" : ""), ct);

    public Task<JsonElement> ProvidersAsync(string sessionId, int? marketId, CancellationToken ct)
        => GetAsync(sessionId, "/api/tours/providers" + (marketId.HasValue ? $"?marketId={marketId}" : ""), ct);

    private async Task<JsonElement> GetAsync(string sessionId, string path, CancellationToken ct)
    {
        var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
        try { return await _api.GetAsync(jwt, path, ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            jwt = await _sessions.ForceReloginAsync(sessionId, ct);
            return await _api.GetAsync(jwt, path, ct);
        }
    }
}
