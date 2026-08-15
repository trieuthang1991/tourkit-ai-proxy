using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Map MimeMessage (MailKit) → MailItem. Pure, không I/O → test được.
public static class MailMapper
{
    public static MailItem FromMime(MimeMessage msg, string fallbackId, bool isRead = false)
    {
        var from = msg.From.Mailboxes.FirstOrDefault();
        var id = string.IsNullOrWhiteSpace(msg.MessageId) ? fallbackId : msg.MessageId!;

        var html = msg.HtmlBody;
        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(html))
            body = HtmlToText(html);   // text sạch (cho AI phân loại/soạn + tìm kiếm + fallback hiển thị)

        // Thư CHUYỂN TIẾP DẠNG ĐÍNH KÈM: thư gốc nằm trong một phần `message/rfc822`, còn vỏ ngoài
        // thường rỗng — `msg.HtmlBody`/`msg.TextBody` chỉ trả phần vỏ nên mở lên thấy TRẮNG.
        // (Gmail bấm "Chuyển tiếp" thì chèn nội tuyến, không dính; Outlook và nhiều app doanh nghiệp
        // thì đính kèm.) Ghép nội dung bên trong vào để đọc được.
        var nested = NestedMessages(msg);

        foreach (var inner in nested)
        {
            var iHtml = inner.HtmlBody;
            var iText = inner.TextBody;
            if (string.IsNullOrWhiteSpace(iText) && !string.IsNullOrWhiteSpace(iHtml))
                iText = HtmlToText(iHtml);
            if (string.IsNullOrWhiteSpace(iText) && string.IsNullOrWhiteSpace(iHtml)) continue;

            body = Join(body, ForwardHeaderText(inner), iText);
            if (!string.IsNullOrWhiteSpace(iHtml))
                html = Join(html, ForwardHeaderHtml(inner), iHtml);
            else if (!string.IsNullOrWhiteSpace(html))
                // Vỏ có HTML mà thư trong chỉ có text → vẫn phải nối vào, không thì khung HTML hiển
                // thị thiếu đúng phần nội dung.
                html = Join(html, ForwardHeaderHtml(inner), "<pre>" + Escape(iText) + "</pre>");
        }

        // Tệp đính kèm: Hộp thư AI CHƯA tải/mở được tệp, nhưng ít nhất phải cho biết là CÓ tệp và tên
        // nó là gì — thư kiểu "Fwd: BCTC tháng 05.26" chỉ có mấy dòng chữ ký, toàn bộ báo cáo nằm
        // trong file Excel; không nói gì thì người dùng kết luận email hỏng.
        // Gom cả tệp của thư bên trong (thư chuyển tiếp thường đính kèm ở lớp trong).
        var files = AttachmentNames(msg, nested);
        if (files.Count > 0)
        {
            var line = "📎 Tệp đính kèm: " + string.Join(", ", files);
            body = Join(body, line);
            if (!string.IsNullOrWhiteSpace(html))
                html = Join(html,
                    "<div style=\"margin-top:12px;padding-top:10px;border-top:1px solid #ddd;color:#666;font-size:13px\">"
                    + Escape(line) + "</div>");
        }

        var received = msg.Date == default ? DateTimeOffset.UtcNow : msg.Date;

        return new MailItem(
            Id:         id,
            From:       new MailContact(
                            Name:  from?.Name ?? from?.Address ?? "(không rõ)",
                            Email: from?.Address ?? ""),
            Subject:    string.IsNullOrWhiteSpace(msg.Subject) ? "(không tiêu đề)" : msg.Subject!,
            Body:       (body ?? "").Trim(),
            ReceivedAt: received.UtcDateTime.ToString("o"),
            IsRead:     isRead,
            Category:   null,
            Status:     "moi",
            AiSummary:  null,
            Draft:      null,
            BodyHtml:   string.IsNullOrWhiteSpace(html) ? null : html,
            IsBulk:     IsBulkMail(msg, from?.Address)
        );
    }

    /// <summary>
    /// Mọi thư nằm lồng bên trong (phần `message/rfc822`), duyệt theo chiều sâu.
    /// Chặn ở 5 lớp: thư chuyển tiếp qua tay 5 người đã là hiếm, còn thư cố tình lồng vô hạn thì
    /// đây là chỗ duy nhất chặn được trước khi nó ăn hết bộ nhớ lúc đồng bộ.
    /// </summary>
    private static List<MimeMessage> NestedMessages(MimeMessage msg)
    {
        var found = new List<MimeMessage>();
        if (msg.Body != null) Walk(msg.Body, 0);
        return found;

        void Walk(MimeEntity entity, int depth)
        {
            if (depth > 5 || found.Count >= 10) return;
            switch (entity)
            {
                case MessagePart mp when mp.Message != null:
                    found.Add(mp.Message);
                    if (mp.Message.Body != null) Walk(mp.Message.Body, depth + 1);
                    break;
                case Multipart multi:
                    foreach (var child in multi) Walk(child, depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// Tên các tệp NGƯỜI TA GỬI KÈM, của cả thư ngoài lẫn thư được chuyển tiếp bên trong.
    ///
    /// <para>CỐ Ý bỏ qua phần <c>inline</c> (logo trong chữ ký, ảnh nhúng qua <c>cid:</c>) và phần
    /// <c>message/rfc822</c> (chính là thư chuyển tiếp — nội dung của nó đã được ghép vào ở trên).
    /// Nếu liệt kê cả logo thì gần như mọi email công ty đều hiện "Tệp đính kèm: image001.png", nhiễu
    /// tới mức lúc có tệp thật thì không ai để ý nữa.</para>
    /// </summary>
    private static List<string> AttachmentNames(MimeMessage msg, List<MimeMessage> nested)
    {
        var names = new List<string>();
        foreach (var m in new[] { msg }.Concat(nested))
        {
            foreach (var part in m.Attachments.OfType<MimePart>())
            {
                if (part.ContentDisposition?.IsAttachment == false) continue;   // inline → bỏ
                var name = part.FileName ?? part.ContentDisposition?.FileName ?? part.ContentType?.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!names.Contains(name)) names.Add(name);   // thư lồng nhau dễ lặp lại cùng một tệp
            }
        }
        return names;
    }

    /// Dòng phân cách cho phần chuyển tiếp — người đọc phải biết đoạn dưới là thư của người khác,
    /// không phải lời của người gửi cho mình.
    private static string ForwardHeaderText(MimeMessage inner)
    {
        var from = inner.From.Mailboxes.FirstOrDefault();
        var sb = new StringBuilder("---------- Thư được chuyển tiếp ----------\n");
        if (from != null) sb.Append("Từ: ").Append(from.Name ?? from.Address).Append(" <").Append(from.Address).Append(">\n");
        if (inner.Date != default) sb.Append("Ngày: ").Append(inner.Date.ToString("dd/MM/yyyy HH:mm")).Append('\n');
        if (!string.IsNullOrWhiteSpace(inner.Subject)) sb.Append("Tiêu đề: ").Append(inner.Subject).Append('\n');
        return sb.ToString();
    }

    private static string ForwardHeaderHtml(MimeMessage inner)
        => "<div style=\"margin:16px 0 8px;padding-top:12px;border-top:1px solid #ddd;color:#666;font-size:13px\">"
           + Escape(ForwardHeaderText(inner)).Replace("\n", "<br>") + "</div>";

    /// Nối các mảnh, bỏ mảnh rỗng — tránh đẻ ra một rừng dòng trống khi vỏ ngoài không có gì.
    private static string Join(string? existing, params string[] parts)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing)) all.Add(existing!.TrimEnd());
        all.AddRange(parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.Join("\n\n", all);
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// Mail bulk/newsletter (gửi hàng loạt) → KHÔNG đáng tốn token phân loại AI.
    /// Tín hiệu chuẩn RFC: header List-Unsubscribe / List-Id, hoặc Precedence: bulk/list;
    /// fallback: địa chỉ gửi kiểu no-reply / newsletter / notifications / mailer / bounce.
    private static bool IsBulkMail(MimeMessage msg, string? fromAddress)
    {
        if (msg.Headers.Contains(HeaderId.ListUnsubscribe) || msg.Headers.Contains(HeaderId.ListId))
            return true;
        var prec = msg.Headers[HeaderId.Precedence];
        if (!string.IsNullOrEmpty(prec) &&
            (prec.Contains("bulk", StringComparison.OrdinalIgnoreCase) ||
             prec.Contains("list", StringComparison.OrdinalIgnoreCase) ||
             prec.Contains("junk", StringComparison.OrdinalIgnoreCase)))
            return true;
        var local = (fromAddress ?? "").Split('@')[0].ToLowerInvariant();
        string[] bulkLocals = { "no-reply", "noreply", "no_reply", "donotreply", "do-not-reply",
                                "newsletter", "news", "notifications", "notification", "notify",
                                "mailer", "mailer-daemon", "bounce", "bounces", "marketing" };
        return bulkLocals.Any(b => local == b || local.StartsWith(b + "+") || local.StartsWith(b + "."));
    }

    /// HTML → text SẠCH. Bản gốc của hàm này nay nằm ở <see cref="Services.Html.PlainText.FromHtml"/>
    /// — cùng một việc còn cần cho ghi chú khách hàng và cơ hội bán hàng, giữ ba bản chép tay thì
    /// chúng lệch nhau (đã xảy ra: bản của khách hàng quên giải mã ký tự suốt nhiều tháng).
    private static string HtmlToText(string html) => Services.Html.PlainText.FromHtml(html);
}
