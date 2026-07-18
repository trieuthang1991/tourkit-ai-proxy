using TourkitAiProxy.Services.TextUtil;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class VietnameseTextTests
{
    [Theory]
    [InlineData("Đà Nẵng", "da nang")]
    [InlineData("Khánh Hòa", "khanh hoa")]
    [InlineData("Thừa Thiên - Huế", "thua thien - hue")]
    [InlineData("Bà Rịa - Vũng Tàu", "ba ria - vung tau")]
    [InlineData("Hà  Nội", "ha noi")]          // 2 khoảng trắng (có thật trong DB demo2)
    [InlineData("  NHA TRANG  ", "nha trang")]  // hoa + thừa khoảng trắng (có thật)
    public void Norm_bo_dau_va_thuong_hoa(string input, string expected)
        => Assert.Equal(expected, VietnameseText.Norm(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Norm_rong_tra_chuoi_rong(string? input)
        => Assert.Equal("", VietnameseText.Norm(input));
}
