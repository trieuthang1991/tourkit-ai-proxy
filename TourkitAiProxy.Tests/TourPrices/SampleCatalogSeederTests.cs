using TourkitAiProxy.Services.TourPrices;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class SampleCatalogSeederTests
{
    [Fact]
    public void ParseSeed_map_dung_va_ep_tenant_sample()
    {
        var json = "[{\"pricingId\":1,\"providerServiceId\":1,\"providerId\":1,\"providerName\":\"Khách sạn 4* A\",\"city\":\"Đà Nẵng\",\"categoryId\":1,\"categoryName\":\"Khách sạn\",\"contractPrice\":1000000,\"publicPrice\":1200000}]";
        var rows = SampleCatalogSeeder.ParseSeed(json);
        Assert.Single(rows);
        Assert.Equal("__sample__", rows[0].TenantId);   // ép về sample
        Assert.Equal("da nang", rows[0].CityNorm);       // norm từ City
        Assert.Equal(1000000m, rows[0].ContractPrice);
        Assert.Equal(4, rows[0].Stars);                  // bóc "4*" từ tên
    }

    [Fact]
    public void ParseSeed_bo_dong_thieu_ten_ncc()
    {
        var json = "[{\"pricingId\":1,\"providerName\":\"\",\"categoryId\":1,\"contractPrice\":100}]";
        Assert.Empty(SampleCatalogSeeder.ParseSeed(json));
    }

    [Fact]
    public void ParseSeed_rong_tra_list_rong() => Assert.Empty(SampleCatalogSeeder.ParseSeed("[]"));
}
