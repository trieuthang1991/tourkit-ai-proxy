using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Phát hiện tuần bất thường. Cái khó không phải phép so sánh mà là KHÔNG BÁO BỪA: ngành tour lên
/// xuống theo mùa, báo động mỗi tuần thì người ta tắt tính năng sau ba lần.
/// </summary>
public class AnomalyRuleTests
{
    private static readonly decimal[] Deu = { 100_000_000m, 100_000_000m, 100_000_000m, 100_000_000m };

    [Fact]
    public void Tut_sau_qua_nguong_thi_bao()
    {
        var a = AnomalyRule.Detect(current: 50_000_000m, baseline: Deu, thresholdPercent: 30);

        Assert.NotNull(a);
        Assert.Equal(-50, a!.DeviationPercent);
        Assert.Contains("giảm", a.Text);
    }

    [Fact]
    public void Tang_vot_cung_bao_nhung_khong_phai_canh_bao()
    {
        var a = AnomalyRule.Detect(200_000_000m, Deu, 30);

        Assert.NotNull(a);
        Assert.Equal(100, a!.DeviationPercent);
        Assert.Contains("tăng", a.Text);
        // Tăng là tin vui: KHÔNG được tô mức cảnh báo, nếu không người đọc hoảng nhầm.
        Assert.Equal(0, a.Severity);
    }

    /// Giảm thì mới là cảnh báo, và giảm càng sâu càng gấp.
    [Theory]
    [InlineData(69_000_000, 1)]   // -31%, vừa qua ngưỡng
    [InlineData(40_000_000, 2)]   // -60%, sâu
    public void Giam_cang_sau_cang_gap(decimal current, int expectedSeverity)
        => Assert.Equal(expectedSeverity, AnomalyRule.Detect(current, Deu, 30)!.Severity);

    /// Dao động trong ngưỡng là chuyện thường của ngành theo mùa — im lặng.
    [Theory]
    [InlineData(80_000_000)] [InlineData(120_000_000)] [InlineData(100_000_000)]
    public void Trong_nguong_thi_im(decimal current)
        => Assert.Null(AnomalyRule.Detect(current, Deu, 30));

    /// Chưa đủ tuần nền thì KHÔNG kết luận. Một tuần làm nền thì "bình thường" chỉ là ngẫu nhiên
    /// của đúng tuần đó — so với nó là so với tiếng ồn.
    [Fact]
    public void Chua_du_tuan_nen_thi_khong_ket_luan()
        => Assert.Null(AnomalyRule.Detect(10_000_000m, new[] { 100_000_000m }, 30));

    /// Nền bằng 0 (công ty mới chạy, hoặc kỳ nghỉ dài) → phần trăm không có nghĩa, chia cho 0.
    /// Im lặng, đừng in "tăng vô hạn %".
    [Fact]
    public void Nen_bang_khong_thi_im()
        => Assert.Null(AnomalyRule.Detect(50_000_000m, new[] { 0m, 0m, 0m, 0m }, 30));

    /// Nền TÍNH BẰNG TRUNG VỊ chứ không phải trung bình: một tuần có hợp đồng lớn gấp 10 lần sẽ
    /// kéo trung bình lên và làm mọi tuần sau trông như "sụt giảm bất thường".
    [Fact]
    public void Nen_dung_trung_vi_de_mot_tuan_dot_bien_khong_lam_hong_ca_nen()
    {
        var coDotBien = new[] { 100_000_000m, 100_000_000m, 100_000_000m, 1_000_000_000m };

        // Trung bình = 325tr → tuần 100tr sẽ bị coi là -69%. Trung vị = 100tr → bình thường.
        Assert.Null(AnomalyRule.Detect(100_000_000m, coDotBien, 30));
    }

    /// Luôn kèm số THỰC của tuần này và mức nền, để người đọc tự kiểm chứ không tin mỗi tỉ lệ.
    [Fact]
    public void Luon_kem_so_that()
    {
        var a = AnomalyRule.Detect(50_000_000m, Deu, 30);

        Assert.Contains("50.000.000", a!.Text);
        Assert.Contains("100.000.000", a.Text);
    }

    /// Tuần này 0 đồng mà nền có số: phải báo, và báo nặng. Im ở đây là giấu đúng tin cần nhất.
    [Fact]
    public void Tuan_nay_khong_ban_duoc_gi_thi_bao_nang()
    {
        var a = AnomalyRule.Detect(0m, Deu, 30);

        Assert.NotNull(a);
        Assert.Equal(-100, a!.DeviationPercent);
        Assert.Equal(2, a.Severity);
    }
}
