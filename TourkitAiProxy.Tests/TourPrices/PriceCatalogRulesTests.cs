using TourkitAiProxy.Services.TourPrices;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class PriceCatalogRulesTests
{
    // Tên NCC thật lấy từ TopTour (đo 2026-07-15).
    [Theory]
    [InlineData("Khu Nghỉ Dưỡng 5* Alibu Resort Nha Trang", 5)]
    [InlineData("Khách sạn 4* Mường Thanh Grand Cửa Lò", 4)]
    [InlineData("Khách Sạn 3* Minh Chiến Đà Lạt", 3)]
    [InlineData("Romana Resort & Spa 4* ", 4)]
    [InlineData("P & T Hotel Vũng Tàu - 3 sao", 3)]
    [InlineData("KHÁCH SẠN 5* ROYAL HA LONG HOTEL", 5)]
    public void ParseStars_boc_duoc_hang_sao(string ten, int expected)
        => Assert.Equal(expected, PriceCatalogRules.ParseStars(ten));

    // 41% KHÔNG bóc được — phải trả null, KHÔNG được đoán bừa.
    [Theory]
    [InlineData("Golden Lotus Luxury - Đà Nẵng")]
    [InlineData("NOVELA MUINE RESORT")]
    [InlineData("Affa Boutique Hotel")]
    [InlineData("Swandor Resort Cam Ranh")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseStars_khong_ro_tra_null(string? ten)
        => Assert.Null(PriceCatalogRules.ParseStars(ten));

    // "Hanasa Pu Luong Resort - 2025" — số 2025 KHÔNG phải hạng sao.
    [Fact]
    public void ParseStars_khong_nham_nam_thanh_hang_sao()
    {
        Assert.Null(PriceCatalogRules.ParseStars("Hanasa Pu Luong Resort - 2025"));
        Assert.Null(PriceCatalogRules.ParseStars("HALIOS HẠ LONG HOTEL - 2025"));
    }

    [Fact]
    public void IsExcluded_chan_ve_may_bay_vi_chua_ten_hanh_khach()
    {
        var blocked = PriceCatalogRules.DefaultBlockedCategories;
        Assert.True(PriceCatalogRules.IsExcluded("Vé máy bay HHK", 4_452_000m, blocked));
        Assert.True(PriceCatalogRules.IsExcluded("Vé máy bay", 1_000_000m, blocked));
        Assert.True(PriceCatalogRules.IsExcluded("VÉ MÁY BAY", 1_000_000m, blocked));   // hoa
        Assert.True(PriceCatalogRules.IsExcluded("Ve may bay", 1_000_000m, blocked));   // không dấu
    }

    [Fact]
    public void IsExcluded_giu_lai_loai_DV_hop_le()
    {
        var blocked = PriceCatalogRules.DefaultBlockedCategories;
        Assert.False(PriceCatalogRules.IsExcluded("Khách sạn", 1_650_000m, blocked));
        Assert.False(PriceCatalogRules.IsExcluded("LandTour", 38_990_000m, blocked));
        Assert.False(PriceCatalogRules.IsExcluded("Nhà Hàng", 450_000m, blocked));
    }

    // Rác nhập tay: 25/9.460 dòng khách sạn TopTour có giá < 50k (25đ, 330đ, 700đ).
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(330)]
    [InlineData(700)]
    [InlineData(49_999)]
    public void IsExcluded_chan_gia_rac(decimal gia)
        => Assert.True(PriceCatalogRules.IsExcluded("Khách sạn", gia, PriceCatalogRules.DefaultBlockedCategories));

    [Fact]
    public void IsExcluded_giu_gia_hop_le_tu_50k()
        => Assert.False(PriceCatalogRules.IsExcluded("Khách sạn", 50_000m, PriceCatalogRules.DefaultBlockedCategories));
}
