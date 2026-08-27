using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Canh thứ tự nhánh của dòng tóm tắt trên thẻ tác vụ bản tin.
///
/// <para><b>Người bị hệ thống tạm tắt cũng có <c>enabled = false</c></b>, nên nếu nhánh "chưa đăng
/// ký nhận" đứng trước thì họ đọc được câu <i>"bạn chưa đăng ký nhận"</i> — sai sự thật, và mâu
/// thuẫn thẳng với dải "đã tạm tắt" hiện ngay bên dưới trong cùng một màn hình. Thấy tận mắt khi
/// chạy thử trên staging 27/08/2026.</para>
/// </summary>
public class BriefVerdictGuardTests
{
    [Fact]
    public void Nhanh_tam_tat_phai_dung_TRUOC_nhanh_chua_dang_ky()
    {
        var src = TourkitAiProxy.Tests.Chat.ChatSchemaGuardTests.DocFile("wwwroot/pages/workflows.jsx");

        var tamTat = src.IndexOf("digestSub.notReadyLabel", System.StringComparison.Ordinal);
        // Soi ĐÚNG dạng mã (thẻ <b> trong JSX), không soi chữ trần: guard đọc cả chú thích, mà
        // chú thích ngay trên nhánh kia có trích nguyên câu này để giải thích.
        var chuaDangKy = src.IndexOf("<b>bạn chưa đăng ký nhận</b>", System.StringComparison.Ordinal);

        Assert.True(tamTat > 0, "Dòng tóm tắt chưa xử lý trường hợp bị tạm tắt");
        Assert.True(chuaDangKy > 0, "Không thấy nhánh 'chưa đăng ký nhận'");
        Assert.True(tamTat < chuaDangKy,
            "Nhánh 'đang tạm tắt' phải đứng TRƯỚC nhánh 'chưa đăng ký nhận' — không thì người bị tạm "
            + "tắt đọc được câu sai sự thật.");
    }

    [Fact]
    public void Dai_canh_bao_an_khi_nguoi_dung_da_bat_lai()
    {
        // Giữ lại thì dải vẫn bảo "bật lại công tắc bên dưới" trong khi họ vừa bật xong.
        var src = TourkitAiProxy.Tests.Chat.ChatSchemaGuardTests.DocFile("wwwroot/pages/digest.jsx");
        Assert.Contains("f.notReadyLabel && !f.enabled", src);
    }

    [Fact]
    public void Ngay_tu_ghep_khong_dua_vao_locale_trinh_duyet()
    {
        // toLocaleDateString('vi-VN') cho '24-08' (gạch nối) trên Chrome, không phải '24/08'; và
        // định dạng còn khác nhau giữa các trình duyệt.
        var src = TourkitAiProxy.Tests.Chat.ChatSchemaGuardTests.DocFile("wwwroot/pages/digest.jsx");
        var i = src.IndexOf("function ngayVn", System.StringComparison.Ordinal);
        Assert.True(i > 0);
        var than = src.Substring(i, System.Math.Min(600, src.Length - i));
        Assert.DoesNotContain("toLocaleDateString", than);
    }
}
