using TourkitAiProxy.Services.TourPrices;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class SampleCatalogTests
{
    [Fact] public void TenantId_la_sample_reserved() => Assert.Equal("__sample__", SampleCatalog.TenantId);

    [Theory]
    [InlineData("__sample__", true)]
    [InlineData("erp.tourkit.vn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSample_dung(string? t, bool expected) => Assert.Equal(expected, SampleCatalog.IsSample(t));
}
