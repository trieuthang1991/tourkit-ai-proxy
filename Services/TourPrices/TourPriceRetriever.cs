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
}
