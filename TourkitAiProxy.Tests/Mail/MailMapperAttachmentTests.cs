using MimeKit;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Domain.Mail;
using Xunit;

namespace TourkitAiProxy.Tests.Mail;

/// Tệp đính kèm. Hộp thư AI KHÔNG tải/mở được tệp (Phase 2), nhưng nửa còn lại của vấn đề rẻ hơn
/// nhiều và quan trọng không kém: cho người dùng BIẾT là có tệp và tên nó là gì.
///
/// Vì sao đáng làm: thư kiểu "Fwd: BCTC tháng 05.26" chỉ có ~989 ký tự chữ ký, toàn bộ báo cáo nằm
/// trong file Excel. Không có dòng nào nói "có tệp" thì người dùng kết luận email hỏng, đúng như báo
/// cáo 14/08 "mail chuyển tiếp không đọc được".
///
/// Cố ý KHÔNG thêm cột vào dbo.Mails: tên tệp được ghép vào chính thân thư lúc bóc, nên không đụng
/// bảng cũ mà vẫn hiển thị được ở cả khung text lẫn khung HTML.
public class MailMapperAttachmentTests
{
    private static MimePart File(string name, string mime = "application/pdf")
        => new(ContentType.Parse(mime))
        {
            FileName = name,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name },
            Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })),
        };

    private static MimeMessage Msg(MimeEntity body, string subject = "Thư thử")
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Người gửi", "ng@example.com"));
        m.Subject = subject;
        m.MessageId = "att@tourkit.vn";
        m.Body = body;
        return m;
    }

    [Fact]
    public void Co_tep_dinh_kem_thi_bao_ten_tep()
    {
        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Anh xem file nhé." },
            File("BCTC-thang-05.xlsx", "application/vnd.ms-excel"),
        };
        var item = MailMapper.FromMime(Msg(body), "fb");

        Assert.Contains("BCTC-thang-05.xlsx", item.Body);
        Assert.Contains("Anh xem file nhé", item.Body);
    }

    [Fact]
    public void Nhieu_tep_thi_liet_ke_het()
    {
        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Gửi anh." },
            File("baocao.pdf"), File("phuluc.docx"),
        };
        var item = MailMapper.FromMime(Msg(body), "fb");

        Assert.Contains("baocao.pdf", item.Body);
        Assert.Contains("phuluc.docx", item.Body);
    }

    /// Ca thật đã gặp: thư được chuyển tiếp, còn báo cáo thì đính kèm ở THƯ BÊN TRONG.
    /// Chỉ nhìn lớp ngoài sẽ kết luận "thư này chẳng có gì".
    [Fact]
    public void Tep_nam_trong_thu_duoc_chuyen_tiep_van_phai_thay()
    {
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("Kế toán", "ketoan@tourkit.vn"));
        inner.Subject = "BCTC tháng 05.26";
        inner.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Gửi sếp báo cáo." },
            File("BCTC-05-2026.xlsx", "application/vnd.ms-excel"),
        };

        var outer = Msg(new Multipart("mixed") { new MessagePart { Message = inner } }, "Fwd: BCTC tháng 05.26");
        var item = MailMapper.FromMime(outer, "fb");

        Assert.Contains("BCTC-05-2026.xlsx", item.Body);
        Assert.Contains("Gửi sếp báo cáo", item.Body);
    }

    /// Logo trong chữ ký là ảnh gắn kèm (inline), KHÔNG phải tệp người ta gửi cho mình.
    /// Liệt kê nó thì gần như mọi email công ty đều hiện "Tệp đính kèm: image001.png" — nhiễu tới mức
    /// dòng đó mất hết ý nghĩa, và lúc có tệp thật thì không ai để ý nữa.
    [Fact]
    public void Anh_inline_trong_chu_ky_khong_bi_tinh_la_tep_dinh_kem()
    {
        var logo = new MimePart("image", "png")
        {
            FileName = "image001.png",
            ContentId = "logo123",
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "image001.png" },
            Content = new MimeContent(new MemoryStream(new byte[] { 1 })),
        };
        var body = new Multipart("related")
        {
            new TextPart("html") { Text = "<p>Trân trọng,</p><img src=\"cid:logo123\">" },
            logo,
        };
        var item = MailMapper.FromMime(Msg(body), "fb");

        Assert.DoesNotContain("image001.png", item.Body);
    }

    [Fact]
    public void Khong_co_tep_thi_khong_them_dong_nao()
    {
        var item = MailMapper.FromMime(Msg(new TextPart("plain") { Text = "Chào anh." }), "fb");

        Assert.DoesNotContain("đính kèm", item.Body);
        Assert.Equal("Chào anh.", item.Body);
    }

    /// Thư HTML hiển thị trong khung riêng — dòng báo tệp phải có ở CẢ đó, không thì mở thư HTML lên
    /// vẫn không biết là có tệp.
    [Fact]
    public void Thu_HTML_thi_khung_HTML_cung_phai_bao_co_tep()
    {
        var body = new Multipart("mixed")
        {
            new TextPart("html") { Text = "<p>Anh xem file nhé.</p>" },
            File("hopdong.pdf"),
        };
        var item = MailMapper.FromMime(Msg(body), "fb");

        Assert.NotNull(item.BodyHtml);
        Assert.Contains("hopdong.pdf", item.BodyHtml!);
    }

    /// Tên tệp do người ngoài đặt → phải escape trước khi nhét vào HTML, không thì mở thư là dính
    /// chèn mã vào khung hiển thị.
    [Fact]
    public void Ten_tep_co_ky_tu_HTML_phai_duoc_escape()
    {
        var body = new Multipart("mixed")
        {
            new TextPart("html") { Text = "<p>Xem nhé.</p>" },
            File("<script>alert(1)</script>.pdf"),
        };
        var item = MailMapper.FromMime(Msg(body), "fb");

        Assert.DoesNotContain("<script>", item.BodyHtml!);
        Assert.Contains("&lt;script&gt;", item.BodyHtml!);
    }
}
