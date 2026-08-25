using MimeKit;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Domain.Mail;
using Xunit;

namespace TourkitAiProxy.Tests.Mail;

/// Thư CHUYỂN TIẾP. Người dùng báo 14/08: "mail chuyển tiếp đang không đọc được".
///
/// Có hai kiểu chuyển tiếp và chúng khác nhau về cấu trúc:
///   1. Chuyển tiếp NỘI TUYẾN (Gmail bấm "Chuyển tiếp") — nội dung gốc được chèn thẳng vào thân thư
///      mới. `msg.HtmlBody` có đủ → đọc được.
///   2. Chuyển tiếp DẠNG ĐÍNH KÈM (Outlook, "forward as attachment", nhiều app doanh nghiệp) — thư
///      gốc nằm trong một phần `message/rfc822`. `msg.HtmlBody` khi đó CHỈ trả phần vỏ, thường rỗng
///      → mở lên trắng trơn, đúng như người dùng mô tả.
public class MailMapperForwardTests
{
    /// Dựng thư "chuyển tiếp dạng đính kèm": vỏ ngoài gần như rỗng, thư thật nằm trong message/rfc822.
    private static MimeMessage ForwardAsAttachment(string wrapperText, string innerHtml)
    {
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("Khách Hàng", "khach@example.com"));
        inner.To.Add(new MailboxAddress("Sale", "sale@tourkit.vn"));
        inner.Subject = "Báo giá tour Nhật Bản";
        inner.Date = new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero);
        inner.Body = new TextPart("html") { Text = innerHtml };

        var multipart = new Multipart("mixed");
        if (wrapperText.Length > 0) multipart.Add(new TextPart("plain") { Text = wrapperText });
        multipart.Add(new MessagePart { Message = inner });

        var outer = new MimeMessage();
        outer.From.Add(new MailboxAddress("Nhân viên", "nv@tourkit.vn"));
        outer.To.Add(new MailboxAddress("Sếp", "sep@tourkit.vn"));
        outer.Subject = "Fwd: Báo giá tour Nhật Bản";
        outer.Date = new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero);
        outer.MessageId = "fwd-1@tourkit.vn";
        outer.Body = multipart;
        return outer;
    }

    /// Kiểu 1 vẫn phải đọc được — chốt lại để bản sửa không phá cái đang chạy đúng.
    [Fact]
    public void Chuyen_tiep_noi_tuyen_van_doc_duoc()
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Nhân viên", "nv@tourkit.vn"));
        msg.Subject = "Fwd: Báo cáo tuần";
        msg.MessageId = "inline-1@tourkit.vn";
        msg.Body = new TextPart("html")
        {
            Text = "<div dir=\"ltr\"><br><div class=\"gmail_quote\">---------- Forwarded message ---------<br>"
                 + "<blockquote class=\"gmail_quote\">Doanh thu tuần này đạt 250 triệu.</blockquote></div></div>"
        };

        var item = MailMapper.FromMime(msg, "fallback");
        Assert.Contains("250 triệu", item.Body);
        Assert.NotNull(item.BodyHtml);
    }

    /// ĐÂY LÀ CA HỎNG. Vỏ rỗng + thư thật nằm trong message/rfc822 → phải lấy được nội dung bên trong,
    /// nếu không người dùng mở lên chỉ thấy trắng.
    [Fact]
    public void Chuyen_tiep_dang_dinh_kem_phai_lay_duoc_noi_dung_ben_trong()
    {
        var msg = ForwardAsAttachment("", "<p>Chị gửi em báo giá tour Nhật 25 triệu/khách nhé.</p>");
        var item = MailMapper.FromMime(msg, "fallback");

        Assert.Contains("25 triệu", item.Body);
        Assert.False(string.IsNullOrWhiteSpace(item.Body), "Thân thư rỗng — người dùng mở lên sẽ thấy trắng");
    }

    /// Vỏ có một dòng ("Em xem giúp anh") nhưng nội dung thật vẫn ở bên trong. Chỉ hiện dòng vỏ là
    /// coi như không đọc được — phải có CẢ hai.
    [Fact]
    public void Vo_ngoai_co_mot_dong_thi_van_phai_kem_noi_dung_ben_trong()
    {
        var msg = ForwardAsAttachment("Em xem giúp anh cái này.",
            "<p>Chị gửi em báo giá tour Nhật 25 triệu/khách nhé.</p>");
        var item = MailMapper.FromMime(msg, "fallback");

        Assert.Contains("Em xem giúp anh", item.Body);
        Assert.Contains("25 triệu", item.Body);
    }

    /// Nội dung bên trong là HTML nên phần HTML hiển thị cũng phải có, không chỉ mỗi text.
    [Fact]
    public void Noi_dung_ben_trong_phai_ra_ca_ban_HTML_de_hien_thi()
    {
        var msg = ForwardAsAttachment("", "<p>Báo giá tour Nhật <b>25 triệu</b>/khách.</p>");
        var item = MailMapper.FromMime(msg, "fallback");

        Assert.NotNull(item.BodyHtml);
        Assert.Contains("25 triệu", item.BodyHtml!);
    }

    /// Chuyển tiếp lồng nhiều lớp (A gửi B, B chuyển cho C, C chuyển cho mình) vẫn phải xuống tới đáy.
    [Fact]
    public void Chuyen_tiep_long_nhieu_lop_van_lay_duoc_noi_dung()
    {
        var deepest = new MimeMessage();
        deepest.From.Add(new MailboxAddress("Khách", "khach@example.com"));
        deepest.Subject = "Hỏi tour";
        deepest.Body = new TextPart("plain") { Text = "Cho mình hỏi tour Hàn Quốc tháng 10 còn chỗ không?" };

        var middleWrap = new Multipart("mixed") { new MessagePart { Message = deepest } };
        var middle = new MimeMessage();
        middle.From.Add(new MailboxAddress("Sale", "sale@tourkit.vn"));
        middle.Subject = "Fwd: Hỏi tour";
        middle.Body = middleWrap;

        var outerWrap = new Multipart("mixed") { new MessagePart { Message = middle } };
        var outer = new MimeMessage();
        outer.From.Add(new MailboxAddress("Trưởng nhóm", "tn@tourkit.vn"));
        outer.Subject = "Fwd: Fwd: Hỏi tour";
        outer.MessageId = "deep@tourkit.vn";
        outer.Body = outerWrap;

        var item = MailMapper.FromMime(outer, "fallback");
        Assert.Contains("Hàn Quốc", item.Body);
    }

    /// Thư thường (không chuyển tiếp) không được đổi hành vi.
    [Fact]
    public void Thu_thuong_khong_bi_anh_huong()
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Khách", "khach@example.com"));
        msg.Subject = "Hỏi giá";
        msg.MessageId = "plain@x";
        msg.Body = new TextPart("plain") { Text = "Tour Đà Nẵng bao nhiêu tiền?" };

        var item = MailMapper.FromMime(msg, "fallback");
        Assert.Equal("Tour Đà Nẵng bao nhiêu tiền?", item.Body);
        Assert.Null(item.BodyHtml);
    }
}
