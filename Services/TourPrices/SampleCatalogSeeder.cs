using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TextUtil;

namespace TourkitAiProxy.Services.TourPrices;

/// Nạp NCC mẫu từ file seed vào rows __sample__ (idempotent: chỉ nạp khi bảng chưa có dòng mẫu nào).
/// Seed dựng 1 lần từ TopTour local (Task 7). ParseSeed thuần để test không cần DB.
public class SampleCatalogSeeder
{
    private readonly TourPriceCatalogRepository _repo;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SampleCatalogSeeder> _log;

    public SampleCatalogSeeder(TourPriceCatalogRepository repo, IHostEnvironment env, ILogger<SampleCatalogSeeder> log)
    { _repo = repo; _env = env; _log = log; }

    private sealed class SeedItem
    {
        public int PricingId { get; set; }
        public int ProviderServiceId { get; set; }
        public int ProviderId { get; set; }
        public string? ProviderName { get; set; }
        public string? ProviderCode { get; set; }
        public string? City { get; set; }
        public int CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? PriceName { get; set; }
        public string? Description { get; set; }
        public decimal ContractPrice { get; set; }
        public decimal PublicPrice { get; set; }
    }

    /// Đọc JSON → CatalogRow (TenantId ép "__sample__", CityNorm norm, Stars bóc từ tên). Thiếu tên NCC → bỏ dòng.
    public static List<CatalogRow> ParseSeed(string json)
    {
        var items = JsonSerializer.Deserialize<List<SeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var rows = new List<CatalogRow>(items.Count);
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.ProviderName)) continue;
            rows.Add(new CatalogRow(
                TenantId: SampleCatalog.TenantId, PricingId: it.PricingId,
                ProviderServiceId: it.ProviderServiceId, ProviderId: it.ProviderId,
                ProviderName: it.ProviderName!, ProviderCode: it.ProviderCode,
                City: it.City, CityNorm: VietnameseText.Norm(it.City),
                CategoryId: it.CategoryId, CategoryName: it.CategoryName,
                PriceName: it.PriceName, Description: it.Description,
                ContractPrice: it.ContractPrice, PublicPrice: it.PublicPrice,
                Stars: PriceCatalogRules.ParseStars(it.ProviderName)));
        }
        return rows;
    }

    /// Nạp nếu chưa có dòng mẫu nào. Trả số dòng đã nạp (0 nếu đã có / seed thiếu).
    public async Task<int> SeedIfEmptyAsync(CancellationToken ct)
    {
        if (await _repo.CountAsync(SampleCatalog.TenantId, ct) > 0) { _log.LogDebug("[sample-seed] đã có NCC mẫu — bỏ qua"); return 0; }
        var path = Path.Combine(_env.ContentRootPath, "data", "seed", "tour-price-sample.json");
        if (!File.Exists(path)) { _log.LogWarning("[sample-seed] thiếu file seed {Path}", path); return 0; }
        var rows = ParseSeed(await File.ReadAllTextAsync(path, ct));
        var n = await _repo.UpsertBatchAsync(rows, ct);
        _log.LogInformation("[sample-seed] nạp {N} dòng NCC mẫu vào __sample__", n);
        return n;
    }
}
