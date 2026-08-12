using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Bản tin điều hành. Ranh giới quan trọng nhất: SỐ do máy chủ tính, AI chỉ viết lời.
/// Và AI lỗi thì bản tin vẫn phải ra được — mất bản tin tệ hơn bản tin khô.
public class CeoBriefBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 11);

    private static CeoBriefData Data() => new(
        ThisMtd: new(1_000_000_000m, 700_000_000m, 300_000_000m),
        PrevMtd: new(800_000_000m, 600_000_000m, 200_000_000m),
        TopSellers: new() { "An — 500tr", "Bình — 300tr" },
        NewDealsYesterday: 4, OpenPaymentAlerts: 2,
        TodayAppointments: 8, OverdueAppointments: 3);

    [Theory]
    [InlineData(120, 100, "+20%")]
    [InlineData(80, 100, "-20%")]
    [InlineData(100, 0, "n/a")]
    public void PctChange_dung(decimal cur, decimal prev, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.PctChange(cur, prev));

    [Fact]
    public void Prompt_chua_so_thuc_va_lenh_cam_bia()
    {
        var p = CeoBriefBuilder.BuildPrompt(Data(), Today);
        Assert.Contains("1.000.000.000", p.Replace(",", "."));
        Assert.Contains("không bịa", p.ToLowerInvariant());
    }

    [Fact]
    public void Fallback_render_du_3_so_chinh()
    {
        var m = CeoBriefBuilder.RenderFallback(Data(), Today);
        Assert.Contains("Doanh thu", m.BodyMarkdown);
        Assert.Contains("+25%", m.BodyMarkdown);   // 1000 so với 800
        Assert.Equal(BriefTypes.Ceo, m.Kind);
    }

    [Fact]
    public void WrapAiReply_giu_prose_va_gan_so_goc()
    {
        var m = CeoBriefBuilder.WrapAiReply("Doanh thu tăng tốt.", Data(), Today);
        Assert.StartsWith("Doanh thu tăng tốt.", m.BodyMarkdown);
        Assert.Contains("Doanh thu:", m.BodyMarkdown);   // bảng số gốc đính kèm dưới lời AI
    }

    // ── Ca thêm ngoài plan ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, "-100%")]     // sạch doanh thu
    [InlineData(100, 100, "+0%")]     // đứng yên — không được ra "0%" trơ hay "n/a"
    [InlineData(0, 0, "n/a")]         // cả hai kỳ đều 0
    [InlineData(-50, 100, "-150%")]   // âm (hoàn tiền nhiều hơn thu)
    public void PctChange_cac_ca_bien(decimal cur, decimal prev, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.PctChange(cur, prev));

    [Fact]
    public void Khong_co_top_seller_thi_ghi_na_chu_khong_de_trong()
    {
        var d = Data() with { TopSellers = new() };
        Assert.Contains("n/a", CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown);
    }

    [Fact]
    public void Chi_lay_toi_da_3_top_seller()
    {
        var d = Data() with { TopSellers = new() { "A", "B", "C", "D", "E" } };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("C", md);
        Assert.DoesNotContain("; D", md);
    }

    [Fact]
    public void AI_tra_loi_co_the_HTML_thi_bi_vo_hieu_hoa()
    {
        // AI bị chèn lệnh (prompt injection) trả về thẻ script — không được lọt vào email.
        var m = CeoBriefBuilder.WrapAiReply("<script>alert(1)</script>", Data(), Today);
        Assert.DoesNotContain("<script>", m.BodyHtml);
        Assert.Contains("&lt;script&gt;", m.BodyHtml);
    }

    [Fact]
    public void Fallback_va_WrapAiReply_deu_co_cung_bang_so()
    {
        // Hai đường ra khác nhau nhưng bảng số PHẢI giống — nếu lệch thì giám đốc thấy số
        // khác nhau tuỳ hôm đó AI có chạy được hay không.
        var d = Data();
        var fb = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        var ai = CeoBriefBuilder.WrapAiReply("Nhận định.", d, Today).BodyMarkdown;
        foreach (var line in fb.Split('\n'))
            Assert.Contains(line.Trim(), ai);
    }

    [Fact]
    public void Lich_hen_hom_nay_hien_ca_so_qua_han()
    {
        var md = CeoBriefBuilder.RenderFallback(Data(), Today).BodyMarkdown;
        Assert.Contains("Lịch hẹn hôm nay: 8 cuộc", md);
        Assert.Contains("3 cuộc QUÁ HẠN", md);
    }

    [Fact]
    public void Khong_co_hen_thi_noi_thang_chu_khong_de_trong()
    {
        var d = Data() with { TodayAppointments = 0, OverdueAppointments = 0 };
        Assert.Contains("Lịch hẹn hôm nay: không có cuộc nào", CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown);
    }

    [Fact]
    public void Khong_qua_han_thi_khong_nhac_qua_han()
    {
        var d = Data() with { TodayAppointments = 5, OverdueAppointments = 0 };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("5 cuộc", md);
        Assert.DoesNotContain("QUÁ HẠN", md);
    }

    [Fact]
    public void Tieu_de_co_ngay()
        => Assert.Contains("11/08", CeoBriefBuilder.RenderFallback(Data(), Today).Title);
}
