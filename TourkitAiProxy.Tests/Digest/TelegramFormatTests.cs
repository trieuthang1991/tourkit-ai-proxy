using TourkitAiProxy.Services.Digest.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class TelegramFormatTests
{
    [Fact]
    public void Escape_html_dac_biet()
    {
        var s = TelegramFormat.ToTelegramHtml("A & B", "x < y > z");
        Assert.Contains("A &amp; B", s);
        Assert.Contains("x &lt; y &gt; z", s);
    }

    [Fact]
    public void Bold_markdown_thanh_the_b()
        => Assert.Contains("<b>Deal</b>", TelegramFormat.ToTelegramHtml("T", "**Deal** can goi"));

    [Fact]
    public void Cat_4096_ky_tu()
    {
        var s = TelegramFormat.ToTelegramHtml("T", new string('x', 9000));
        Assert.True(s.Length <= 4096);
    }

    // ── Ca thêm ngoài plan ────────────────────────────────────────────────────

    [Fact]
    public void Escape_TRUOC_khi_doi_bold_nen_the_b_khong_bi_escape()
    {
        // Nếu escape SAU khi đổi **x** thì <b> thành &lt;b&gt; → Telegram in ra chữ "<b>" thay vì in đậm.
        var s = TelegramFormat.ToTelegramHtml("T", "**Quan trọng**");
        Assert.Contains("<b>Quan trọng</b>", s);
        Assert.DoesNotContain("&lt;b&gt;", s);
    }

    [Fact]
    public void Nguoi_dung_go_the_html_thi_bi_vo_hieu_hoa()
    {
        // Tên khách chứa "<script>" không được lọt thành thẻ thật.
        var s = TelegramFormat.ToTelegramHtml("T", "<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", s);
        Assert.Contains("&lt;script&gt;", s);
    }

    [Fact]
    public void Tieu_de_cung_duoc_escape()
    {
        var s = TelegramFormat.ToTelegramHtml("<b>gian lận</b>", "x");
        Assert.Contains("&lt;b&gt;gian lận&lt;/b&gt;", s);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Null_hoac_rong_khong_vo(string? title, string? body)
    {
        var s = TelegramFormat.ToTelegramHtml(title!, body!);
        Assert.NotNull(s);
    }
}
