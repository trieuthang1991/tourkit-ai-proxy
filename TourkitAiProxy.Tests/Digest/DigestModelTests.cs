using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

public class DigestModelTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(23, 23)]
    [InlineData(7, 7)]
    [InlineData(-1, 7)]
    [InlineData(24, 7)]
    [InlineData(99, 7)]
    public void ClampHour_gioi_han_0_23_ngoai_khoang_ve_7(int input, int expected)
        => Assert.Equal(expected, DigestSubscription.ClampHour(input));

    [Theory]
    [InlineData("sale-brief", true)]
    [InlineData("ceo-brief", true)]
    [InlineData("payment-alert", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BriefTypes_chi_nhan_2_loai(string? t, bool expected)
        => Assert.Equal(expected, BriefTypes.IsValid(t));
}
