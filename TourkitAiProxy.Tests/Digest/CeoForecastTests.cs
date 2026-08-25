using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Dự phóng cuối tháng cho bản tin điều hành. Phép tính tầm thường, cái khó là biết KHI NÀO KHÔNG
/// nên nói: con số này nằm trong bản tin gửi giám đốc, nói bừa một lần là mất lòng tin cả bản tin.
/// </summary>
public class CeoForecastTests
{
    // Tháng 8 có 31 ngày.
    private static DateTime Aug(int day) => new(2026, 8, day);

    [Fact]
    public void Du_ngay_thi_uoc_theo_toc_do()
    {
        // 10 ngày đã qua, đạt 1 tỷ → tốc độ 100tr/ngày → cả tháng 31 ngày ≈ 3,1 tỷ.
        var f = CeoForecast.Estimate(revenueSoFar: 1_000_000_000m, target: 3_000_000_000m, todayVn: Aug(10));

        Assert.NotNull(f);
        Assert.Equal(3_100_000_000m, f!.Projected);
        Assert.Equal(103, f.PercentOfTarget);
    }

    /// Đầu tháng: một hợp đồng lớn về ngày 2 sẽ nhân tốc độ lên 15 lần → dự phóng hoang đường.
    /// Thà không nói còn hơn nói một con số sai lệch cỡ đó trong bản tin gửi giám đốc.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void Dau_thang_thi_khong_uoc(int day)
    {
        var f = CeoForecast.Estimate(500_000_000m, 3_000_000_000m, Aug(day));

        Assert.NotNull(f);
        Assert.False(f!.CanProject);
        Assert.Null(f.Projected);
        Assert.Contains("còn sớm", f.Text);
        // Vẫn phải nói số THỰC đạt — không ước không có nghĩa là im hẳn.
        Assert.Contains("500", f.Text);
    }

    [Fact]
    public void Ngay_thu_5_la_bat_dau_uoc_duoc()
        => Assert.True(CeoForecast.Estimate(500_000_000m, 3_000_000_000m, Aug(5))!.CanProject);

    /// Chưa khai chỉ tiêu → TẮT hẳn mục này. Đoán hộ một con số sẽ khiến mọi công ty đọc một dự
    /// phóng vô nghĩa (cùng nguyên tắc với "số khách tối thiểu" của kiểm tra khởi hành).
    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void Chua_khai_chi_tieu_thi_khong_hien_gi(int target)
        => Assert.Null(CeoForecast.Estimate(1_000_000_000m, target, Aug(10)));

    [Theory]
    [InlineData(4_000_000_000, "vượt kế hoạch")]
    [InlineData(2_900_000_000, "hụt nhẹ")]
    [InlineData(1_000_000_000, "khó đạt")]
    public void Ba_muc_cau_chu(decimal projectedTargetRatioSource, string expect)
    {
        // 10/31 ngày đã qua → dự phóng = đạt × 3,1.
        var soFar = projectedTargetRatioSource / 3.1m;
        var f = CeoForecast.Estimate(soFar, 3_000_000_000m, Aug(10));

        Assert.Contains(expect, f!.Text);
    }

    /// Luôn kèm số thực đạt VÀ chỉ tiêu — người đọc phải tự kiểm được, không tin một tỉ lệ trần trụi.
    [Fact]
    public void Luon_kem_so_thuc_dat_va_chi_tieu()
    {
        var f = CeoForecast.Estimate(1_000_000_000m, 3_000_000_000m, Aug(10));

        Assert.Contains("1.000.000.000", f!.Text);   // đã đạt
        Assert.Contains("3.000.000.000", f.Text);    // chỉ tiêu
    }

    /// Tháng 30 ngày và tháng 31 ngày phải ra số khác nhau — dùng cứng 30 là sai đều đặn mỗi năm 7 lần.
    [Fact]
    public void Tinh_dung_so_ngay_cua_tung_thang()
    {
        var thang8 = CeoForecast.Estimate(1_000_000_000m, 3_000_000_000m, new DateTime(2026, 8, 10));
        var thang9 = CeoForecast.Estimate(1_000_000_000m, 3_000_000_000m, new DateTime(2026, 9, 10));

        Assert.Equal(3_100_000_000m, thang8!.Projected);   // 31 ngày
        Assert.Equal(3_000_000_000m, thang9!.Projected);   // 30 ngày
    }

    /// Chưa bán được gì mà đã qua nửa tháng: vẫn phải nói, và nói là khó đạt — im lặng ở đây
    /// là giấu đúng cái tin cần báo nhất.
    [Fact]
    public void Chua_ban_duoc_gi_van_phai_bao()
    {
        var f = CeoForecast.Estimate(0m, 3_000_000_000m, Aug(15));

        Assert.NotNull(f);
        Assert.Equal(0m, f!.Projected);
        Assert.Equal(0, f.PercentOfTarget);
        Assert.Contains("khó đạt", f.Text);
    }
}
