using System.Diagnostics;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TextUtil;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflows;

namespace TourkitAiProxy.Services.TourPrices;

/// Option per-tenant (OptionsJson).
public record TourPriceSyncOptions(int PageSize = 500, string[]? BlockedCategories = null);

/// <summary>
/// Đồng bộ bảng giá NCC từ TourKit về `dbo.TourPriceCatalog`. PerTenant, mặc định 1 lần/ngày.
///
/// Auth = service account (`dbo.TenantServiceAccounts`) → tự login, KHÔNG cần user online.
/// Tenant chưa cấu hình service account → workflow báo lỗi rõ, không crash.
///
/// Loại trừ (spec §4.3): "Vé máy bay" (chứa tên hành khách thật) + giá &lt; 50k (rác nhập tay).
///
/// KHÔNG gọi AI → không cần AiCallContext. Chỉ ĐỌC từ TourKit, không ghi ngược.
/// </summary>
public class TourPriceCatalogSyncWorkflow : IScheduledWorkflow
{
    private readonly TourKitNccClient _ncc;
    private readonly TkSessionStore _sessions;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly TourPriceCatalogRepository _repo;
    private readonly ILogger<TourPriceCatalogSyncWorkflow> _log;

    public string Type => "tour-price-catalog-sync";
    public string Label => "Đồng bộ bảng giá nhà cung cấp";
    public string Description => "Kéo bảng giá NCC từ TourKit về để AI dựng giá tour bằng số thật thay vì ước lượng.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public TourPriceCatalogSyncWorkflow(TourKitNccClient ncc, TkSessionStore sessions,
        TenantServiceAccountStore serviceAccounts, TourPriceCatalogRepository repo,
        ILogger<TourPriceCatalogSyncWorkflow> log)
    {
        _ncc = ncc; _sessions = sessions; _serviceAccounts = serviceAccounts; _repo = repo; _log = log;
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new WorkflowRunResult(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var sw = Stopwatch.StartNew();
        var opt = ParseOptions(optionsJson);
        var blocked = opt.BlockedCategories?.Length > 0
            ? opt.BlockedCategories
            : PriceCatalogRules.DefaultBlockedCategories;

        _log.LogInformation("[tour-price-sync] START tenant={T} pageSize={P} blocked=[{B}]",
            tenantId, opt.PageSize, string.Join(",", blocked));

        // Service account: bắt buộc cấu hình + bật (giống DealAutoReviewWorkflow).
        var svc = _serviceAccounts.Get(tenantId);
        if (svc == null || !svc.Enabled)
        {
            _log.LogWarning("[tour-price-sync] tenant={T} DỪNG: chưa cấu hình tài khoản tự động (svc={Svc} enabled={En})",
                tenantId, svc?.Username ?? "null", svc?.Enabled);
            return new WorkflowRunResult(false, null, "Chưa cấu hình tài khoản tự động cho tenant (POST /api/v1/workflows/service-account)");
        }

        string sessionId;
        try
        {
            sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[tour-price-sync] tenant={T} LOGIN FAIL user={U}: {Err}", tenantId, svc.Username, ex.Message);
            return new WorkflowRunResult(false, null, "Đăng nhập TourKit thất bại — kiểm tra tài khoản tự động.");
        }

        var syncStartUtc = DateTime.UtcNow;
        int page = 0, total = -1, fetched = 0, saved = 0, skipped = 0;

        while (!ct.IsCancellationRequested)
        {
            var data = await _ncc.ProviderPricesAsync(sessionId, page, opt.PageSize, ct);
            if (total < 0 && data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                total = t.GetInt32();
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            var n = items.GetArrayLength();
            if (n == 0) break;
            fetched += n;

            var rows = new List<CatalogRow>(n);
            foreach (var it in items.EnumerateArray())
            {
                var row = MapRow(tenantId, it);
                if (row is null) { skipped++; continue; }
                if (PriceCatalogRules.IsExcluded(row.CategoryName, row.ContractPrice, blocked)) { skipped++; continue; }
                rows.Add(row);
            }
            saved += await _repo.UpsertBatchAsync(rows, ct);

            _log.LogDebug("[tour-price-sync] tenant={T} trang {P}: lấy {N}, lưu {S}, bỏ {K}", tenantId, page, n, rows.Count, n - rows.Count);
            page++;
            if (total >= 0 && fetched >= total) break;
            if (page > 500) { _log.LogWarning("[tour-price-sync] tenant={T} vượt 500 trang — dừng phòng lặp vô tận", tenantId); break; }
        }

        var deactivated = await _repo.DeactivateMissingAsync(tenantId, syncStartUtc, ct);
        var active = await _repo.CountAsync(tenantId, ct);
        sw.Stop();

        var summary = $"Lấy {fetched} dòng, lưu {saved}, bỏ {skipped}, tắt {deactivated}. Đang hiệu lực: {active}.";
        _log.LogInformation("[tour-price-sync] FINISH tenant={T} ({Ms}ms) {Sum}", tenantId, sw.ElapsedMilliseconds, summary);
        return new WorkflowRunResult(true, summary, null);
    }

    private static TourPriceSyncOptions ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TourPriceSyncOptions();
        try
        {
            return JsonSerializer.Deserialize<TourPriceSyncOptions>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TourPriceSyncOptions();
        }
        catch { return new TourPriceSyncOptions(); }
    }

    /// Map 1 item JSON → CatalogRow. Thiếu field bắt buộc → null (bỏ dòng, không ném).
    private static CatalogRow? MapRow(string tenantId, JsonElement e)
    {
        int? I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

        var pricingId = I("pricingId");
        var providerId = I("providerId");
        var categoryId = I("categoryId");
        var providerName = S("providerName");
        if (pricingId is null || providerId is null || categoryId is null || string.IsNullOrWhiteSpace(providerName))
            return null;

        var city = S("city");
        return new CatalogRow(
            TenantId: tenantId,
            PricingId: pricingId.Value,
            ProviderServiceId: I("providerServiceId") ?? 0,
            ProviderId: providerId.Value,
            ProviderName: providerName!,
            ProviderCode: S("providerCode"),
            City: city,
            CityNorm: VietnameseText.Norm(city),
            CategoryId: categoryId.Value,
            CategoryName: S("categoryName"),
            PriceName: S("priceName"),
            Description: S("description"),
            ContractPrice: D("contractPrice"),
            PublicPrice: D("publicPrice"),
            Stars: PriceCatalogRules.ParseStars(providerName));
    }
}
