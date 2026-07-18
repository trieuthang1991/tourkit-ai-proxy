using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourPrices;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class PriceMergeTests
{
    static CatalogRow Row(string tenant, int id, string cityNorm, int cat) => new(
        TenantId: tenant, PricingId: id, ProviderServiceId: id, ProviderId: id,
        ProviderName: "NCC" + id, ProviderCode: null, City: cityNorm, CityNorm: cityNorm,
        CategoryId: cat, CategoryName: "Khách sạn", PriceName: null, Description: null,
        ContractPrice: 1_000_000m, PublicPrice: 1_200_000m, Stars: null);

    [Fact]
    public void ThatThieu_thi_lap_bang_mau_dung_cap()
    {
        var real = new[] { Row("t", 1, "da nang", 1) };                 // thật: Đà Nẵng/KS
        var sample = new[] { Row("__sample__", 10, "da nang", 1),       // mẫu: Đà Nẵng/KS → BỎ (thật có)
                             Row("__sample__", 11, "nha trang", 1) };   // mẫu: Nha Trang/KS → GIỮ (thật thiếu)
        var m = PriceMerge.PreferReal(real, sample);
        Assert.Equal(2, m.Count);
        Assert.Contains(m, c => c.Source == "real" && c.Row.PricingId == 1);
        Assert.Contains(m, c => c.Source == "sample" && c.Row.PricingId == 11);
        Assert.DoesNotContain(m, c => c.Row.PricingId == 10);
    }

    [Fact]
    public void That_rong_thi_toan_mau()
    {
        var sample = new[] { Row("__sample__", 10, "hue", 1) };
        var m = PriceMerge.PreferReal(System.Array.Empty<CatalogRow>(), sample);
        Assert.Single(m);
        Assert.Equal("sample", m[0].Source);
    }

    [Fact]
    public void Mau_rong_thi_toan_that()
    {
        var real = new[] { Row("t", 1, "da nang", 1), Row("t", 2, "da nang", 2) };
        var m = PriceMerge.PreferReal(real, System.Array.Empty<CatalogRow>());
        Assert.Equal(2, m.Count);
        Assert.All(m, c => Assert.Equal("real", c.Source));
    }
}
