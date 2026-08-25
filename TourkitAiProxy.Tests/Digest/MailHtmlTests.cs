using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Escape thân thư. Lỗi ở đây KHÔNG làm gửi thất bại — thư vẫn đi, vẫn báo thành công, chỉ là người
/// nhận mở ra thấy một dãy số. Đã dính thật hai lần nên khoá lại bằng test.
/// </summary>
public class MailHtmlTests
{
    [Fact]
    public void Chu_co_dau_phai_GIU_NGUYEN()
    {
        // Đây là ca đã hỏng thật: WebUtility.HtmlEncode biến câu này thành
        // "Những kh&#225;ch n&#224;y đ&#227; từng mua".
        const string s = "Những khách này đã từng mua nhưng lâu rồi không ai liên hệ";
        Assert.Equal(s, MailHtml.Esc(s));
    }

    [Theory]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("<script>", "&lt;script&gt;")]
    [InlineData("nói \"xin chào\"", "nói &quot;xin chào&quot;")]
    public void Chi_escape_4_ky_tu_pha_cau_truc(string vao, string ra)
        => Assert.Equal(ra, MailHtml.Esc(vao));

    [Fact]
    public void Ampersand_phai_escape_TRUOC_de_khong_escape_hai_lan()
    {
        // Đổi thứ tự là "<" thành "&amp;lt;" — người nhận đọc được đúng chuỗi đó thay vì dấu nhỏ hơn.
        Assert.Equal("&amp;lt;", MailHtml.Esc("&lt;"));
    }

    [Fact]
    public void Xuong_dong_thanh_br_va_van_giu_dau()
    {
        Assert.Equal("Khách A<br>Khách B", MailHtml.EscToHtml("Khách A\nKhách B"));
    }

    [Fact]
    public void The_br_vua_chen_KHONG_duoc_escape_lai()
    {
        // Escape phải chạy TRƯỚC khi chèn <br>. Làm ngược thì ra "&lt;br&gt;" hiện thành chữ.
        var got = MailHtml.EscToHtml("a\nb");
        Assert.Contains("<br>", got);
        Assert.DoesNotContain("&lt;br", got);
    }

    [Fact]
    public void Rong_va_null_khong_no()
    {
        Assert.Equal("", MailHtml.Esc(null));
        Assert.Equal("", MailHtml.EscToHtml(null));
    }
}
