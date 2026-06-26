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

    /// HTML → text SẠCH: bỏ HẲN nội dung &lt;style&gt;/&lt;script&gt;/&lt;head&gt; + comment (tránh CSS lọt vào text),
    /// đổi &lt;br&gt;/&lt;/p&gt;/&lt;/div&gt;/&lt;/tr&gt;/&lt;/li&gt; thành xuống dòng, gỡ thẻ còn lại, decode entity, gộp khoảng trắng.
    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;
        s = Regex.Replace(s, "<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, "<(style|script|head)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<\\s*(br|/p|/div|/tr|/li|/h[1-6])\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "[ \\t\\r\\f]+", " ");
        s = Regex.Replace(s, " *\\n *", "\n");
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }
}
