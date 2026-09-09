using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Mail;

/// <summary>
/// Ô xem trước bản nháp thư phải hiện CHỮ ĐỌC ĐƯỢC, không hiện mã HTML.
///
/// <para><b>Chuyện đã xảy ra (09/09/2026).</b> Bản nháp AI trả về là HTML. Lúc chữ còn đang chảy,
/// ô xem trước in thẳng chuỗi đó ra dạng văn bản, nên người dùng đọc phải
/// <c>&lt;p&gt;Kính gửi anh Nam,&lt;/p&gt;&lt;br/&gt;</c>. Câu hỏi duy nhất ô này sinh ra để trả
/// lời — "thư sắp gửi trông thế nào" — thành không trả lời được.</para>
///
/// <para><b>Vì sao không dựng thẳng HTML ra màn hình.</b> Chữ đang chảy nên thẻ luôn dở dang;
/// dựng lại từng nhịp thì bố cục nhảy liên tục. Đó chính là lý do ô xem trước được tách khỏi
/// trình soạn thảo ngay từ đầu. Nên lối đúng là BỎ THẺ, không phải dựng thẻ.</para>
/// </summary>
public class MailDraftPreviewGuardTests
{
    private static string Nguon()
        => BoChuThich(Chat.ChatSchemaGuardTests.DocFile("wwwroot/pages/mail.jsx"));

    /// Bỏ dòng chú thích trước khi soi — chú thích của chính hàm này nhắc tới các dạng mã bị cấm.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", System.StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("*", System.StringComparison.Ordinal)));

    [Fact]
    public void O_xem_truoc_phai_di_qua_bo_chuyen_HTML_thanh_chu()
    {
        var src = Nguon();
        var m = Regex.Match(src, @"function MailDraftEditor\((.{0,400}?)\n\}", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy MailDraftEditor");
        var than = m.Groups[1].Value;

        // Nhánh đang-soạn phải đi qua bộ chuyển. In thẳng {value} là quay lại đúng lỗi cũ.
        Assert.Contains("chuTuHtml(value)", than);
        Assert.DoesNotMatch(@"mail-draft-stream[^>]*>\{value", than);
    }

    [Fact]
    public void Bo_chuyen_phai_doi_the_xuong_dong_TRUOC_khi_bo_the()
    {
        // Thứ tự là cả vấn đề: bỏ hết thẻ trước rồi mới tính xuống dòng thì không còn gì để tính
        // — cả lá thư dính thành MỘT khối chữ, đọc còn khó hơn lúc thấy thẻ.
        var src = Nguon();
        var m = Regex.Match(src, @"function chuTuHtml\(html\)(.{0,1200}?)\n\}", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy chuTuHtml");
        var than = m.Groups[1].Value;

        var iXuongDong = than.IndexOf(@"<\s*br", System.StringComparison.Ordinal);
        var iBoThe = than.IndexOf(@"<[^>]*>/g", System.StringComparison.Ordinal);
        Assert.True(iXuongDong > 0, "Thiếu bước đổi <br> thành xuống dòng");
        Assert.True(iBoThe > iXuongDong, "Bỏ thẻ phải nằm SAU bước đổi thẻ xuống dòng");

        // Thẻ khối cũng phải thành xuống dòng, không riêng <br>.
        Assert.Contains("p|div|li|tr|h[1-6]", than);

        // Và phải xử thẻ CHƯA ĐÓNG ở cuối chuỗi: khi chữ đang chảy thì đuôi luôn dở dang, mà
        // biểu thức bỏ thẻ thường không khớp nó vì thiếu dấu '>' — để nguyên là người dùng thấy
        // "<stro" nhấp nháy ở cuối dòng suốt lượt soạn.
        Assert.Contains(@"<[^>]*$", than);
    }
}
