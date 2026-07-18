using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.TourPrices;

/// Lấy ứng viên giá theo NGUỒN người dùng chọn (mẫu / thật / cả 2 ưu tiên thật).
/// Both: giữ hết dòng thật, lấp mẫu vào cặp (điểm đến+loại) mà thật thiếu (qua PriceMerge).
public class TourPriceRetriever
{
    private const int Cap = 60;
    private readonly TourPriceCatalogRepository _repo;
    private readonly ILogger<TourPriceRetriever> _log;

    public TourPriceRetriever(TourPriceCatalogRepository repo, ILogger<TourPriceRetriever> log)
    { _repo = repo; _log = log; }

    public async Task<List<PriceCandidate>> RetrieveAsync(string tenantId, PriceQuery q, PriceSource source, CancellationToken ct)
    {
        switch (source)
        {
            case PriceSource.Sample:
                return Tag(await _repo.QueryAsync(SampleCatalog.TenantId, q, Cap, ct), "sample");
            case PriceSource.Real:
                return Tag(await _repo.QueryAsync(tenantId, q, Cap, ct), "real");
            default: // Both — ưu tiên thật, lấp mẫu
                var real = await _repo.QueryAsync(tenantId, q, Cap, ct);
                var sample = await _repo.QueryAsync(SampleCatalog.TenantId, q, Cap, ct);
                var merged = PriceMerge.PreferReal(real, sample);
                _log.LogDebug("[price-retriever] tenant={T} both: thật={R} mẫu-lấp={S}", tenantId, real.Count, merged.Count - real.Count);
                return merged;
        }
    }

    private static List<PriceCandidate> Tag(List<CatalogRow> rows, string src)
        => rows.Select(r => new PriceCandidate(r, src)).ToList();

    /// Dải giá theo LOẠI dịch vụ (p25/p50/p75) — bơm mốc giá cho AI dựng giá tour.
    /// Both: mỗi loại ưu tiên dải THẬT của tenant; loại nào tenant chưa có → lấp bằng dải MẪU.
    /// cityNorm gồm cả loại city-less (vé máy bay/vận chuyển/HDV) nên AI có mốc cho các mục lớn.
    public async Task<List<PriceBand>> BandsAsync(string tenantId, string? cityNorm, PriceSource source, CancellationToken ct)
    {
        switch (source)
        {
            case PriceSource.Sample:
                return TagBands(await _repo.CategoryBandsAsync(SampleCatalog.TenantId, cityNorm, ct), "sample");
            case PriceSource.Real:
                return TagBands(await _repo.CategoryBandsAsync(tenantId, cityNorm, ct), "real");
            default: // Both — mỗi loại ưu tiên thật, lấp mẫu
                var real = TagBands(await _repo.CategoryBandsAsync(tenantId, cityNorm, ct), "real");
                var sample = TagBands(await _repo.CategoryBandsAsync(SampleCatalog.TenantId, cityNorm, ct), "sample");
                var realCats = new HashSet<int>(real.Select(b => b.CategoryId));
                var merged = new List<PriceBand>(real);
                merged.AddRange(sample.Where(b => !realCats.Contains(b.CategoryId)));
                _log.LogDebug("[price-retriever] tenant={T} bands both: thật={R} mẫu-lấp={S}", tenantId, real.Count, merged.Count - real.Count);
                return merged;
        }
    }

    private static List<PriceBand> TagBands(List<PriceBand> bands, string src)
        => bands.Select(b => b with { Source = src }).ToList();
}
