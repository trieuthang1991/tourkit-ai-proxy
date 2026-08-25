using System.Text.Json;
using TourkitAiProxy.Services.Deals;
using TourkitAiProxy.Shared.Text;
using TourkitAiProxy.Infrastructure.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests.TourKit;

/// <summary>
/// Ghi chú chăm sóc + ghi chú khách + mô tả cơ hội trong CRM được nhập bằng ô soạn thảo, nên lưu
/// dưới dạng HTML. Upstream có gỡ THẺ nhưng KHÔNG giải mã KÝ TỰ ĐẶC BIỆT, nên tiếng Việt về tới đây
/// vẫn ở dạng "khong c&amp;oacute; nhu cầu".
///
/// Hỏng hai lớp: (1) người dùng đọc panel thấy chữ vô nghĩa; (2) AI đọc phải tiếng Việt méo rồi
/// chép nguyên vào lời khuyên. Đã thấy thật trên staging ngày 15/08.
/// </summary>
public class PlainTextTests
{
    [Fact]
    public void Giai_ma_ky_tu_tieng_Viet_bi_ma_hoa()
    {
        // Ca thật lấy từ nhật ký chăm sóc trên staging.
        Assert.Equal("khong có nhu cầu", PlainText.FromHtml("khong c&oacute; nhu cầu"));
        Assert.Equal("bình luân daya", PlainText.FromHtml("b&igrave;nh lu&acirc;n daya"));
    }

    [Fact]
    public void Nbsp_thanh_khoang_trang_chu_khong_giu_nguyen_ma()
    {
        var s = PlainText.FromHtml("CH0587 &nbsp; Hoa12");
        Assert.DoesNotContain("&nbsp;", s);
        Assert.Equal("CH0587 Hoa12", s);
    }

    [Fact]
    public void Go_the_html()
    {
        Assert.Equal("xin chào", PlainText.FromHtml("<p>xin chào</p>"));
        Assert.Equal("dòng 1\ndòng 2", PlainText.FromHtml("dòng 1<br/>dòng 2"));
    }

    [Fact]
    public void Chu_thuong_khong_bi_dong_vao()
    {
        // Phần lớn ghi chú là chữ thuần — không được đổi gì.
        Assert.Equal("Khách hẹn gọi lại thứ 3", PlainText.FromHtml("Khách hẹn gọi lại thứ 3"));
    }

    [Fact]
    public void Giai_ma_dung_MOT_lan()
    {
        // Giải mã hai lần thì "&amp;lt;" hoá thành "<" — mở đường cho HTML lọt vào chỗ hiển thị.
        Assert.Equal("&lt;script&gt;", PlainText.FromHtml("&amp;lt;script&amp;gt;"));
    }

    [Fact]
    public void Rong_va_null_tra_chuoi_rong()
    {
        Assert.Equal("", PlainText.FromHtml(null));
        Assert.Equal("", PlainText.FromHtml(""));
        Assert.Equal("", PlainText.FromHtml("   "));
    }

    // ─── Áp dụng thật vào luồng khách hàng ────────────────────────────────────────

    [Fact]
    public void Nhat_ky_cham_soc_va_ghi_chu_khach_duoc_giai_ma()
    {
        var json = """
        {
          "id": 37691, "fullName": "em thủy", "phone": "0877731419",
          "note": "kh&aacute;ch quen &nbsp; ưu ti&ecirc;n",
          "careLogs": [
            { "date": "2026-06-12", "content": "CH0530 khong c&oacute; nhu cầu", "userName": "Quản trị HOA" }
          ]
        }
        """;
        var c = TourKitCustomerSource.MapContext(JsonDocument.Parse(json).RootElement);

        Assert.Equal("khách quen ưu tiên", c.Note);
        Assert.Equal("CH0530 khong có nhu cầu", c.CareLogs[0].Summary);
    }

    [Fact]
    public void Ghi_chu_co_hoi_ban_hang_cung_duoc_giai_ma()
    {
        // Cùng một ô soạn thảo CRM → cùng lỗi. Trước đây chỉ gỡ thẻ, không giải mã.
        Assert.Equal("khách muốn đi Đà Nẵng",
            DealOpportunityClient.StripHtml("<p>kh&aacute;ch muốn đi Đ&agrave; Nẵng</p>"));
    }
}
