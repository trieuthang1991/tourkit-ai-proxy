using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.TourPrices;

/// Logic thuần trộn nguồn giá — tách khỏi retriever/DB để test không cần SQL.
public static class PriceMerge
{
    /// "Cả 2, ưu tiên thật": giữ hết dòng thật (nhãn "real"); lấp mẫu vào cặp
    /// (CityNorm, CategoryId) mà thật KHÔNG có dòng nào (nhãn "sample").
    public static List<PriceCandidate> PreferReal(IReadOnlyList<CatalogRow> real, IReadOnlyList<CatalogRow> sample)
    {
        var result = new List<PriceCandidate>(real.Count + sample.Count);
        foreach (var r in real) result.Add(new PriceCandidate(r, "real"));
        var realKeys = new HashSet<(string, int)>(real.Select(r => (r.CityNorm ?? "", r.CategoryId)));
        foreach (var s in sample)
            if (!realKeys.Contains((s.CityNorm ?? "", s.CategoryId)))
                result.Add(new PriceCandidate(s, "sample"));
        return result;
    }
}
