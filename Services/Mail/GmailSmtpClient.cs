using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace TourkitAiProxy.Services.Mail;

/// IMailSender qua SMTP Gmail (MailKit). Gửi TỪ chính hộp thư Gmail công ty (App Password) →
/// không dính SPF/DKIM/spam như khi giả mạo domain. Mỗi lần gửi mở/đóng kết nối riêng.
public class GmailSmtpClient : IMailSender
{
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;

    private readonly MailAccountStore _account;
    private readonly ILogger<GmailSmtpClient> _log;

    public GmailSmtpClient(MailAccountStore account, ILogger<GmailSmtpClient> log)
    {
        _account = account; _log = log;
    }

    public async Task SendAsync(string tenantId, string toEmail, string? toName, string subject, string body, string? inReplyToMessageId, CancellationToken ct)
    {
        var creds = _account.Get(tenantId);
        if (creds is not { } c0 || string.IsNullOrWhiteSpace(c0.Address) || string.IsNullOrWhiteSpace(c0.AppPassword))
            throw new InvalidOperationException("Chưa cấu hình hộp thư Gmail.");
        var (address, appPassword) = (c0.Address, c0.AppPassword);
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new InvalidOperationException("Thiếu địa chỉ người nhận.");

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(address));
        msg.To.Add(new MailboxAddress(toName ?? "", toEmail.Trim()));
        msg.Subject = string.IsNullOrWhiteSpace(subject) ? "(không tiêu đề)" : subject.Trim();

        // Editor TinyMCE trả HTML → gửi multipart (HTML + text thuần fallback). Ngược lại gửi plain.
        if (LooksHtml(body))
            msg.Body = new BodyBuilder { HtmlBody = body, TextBody = HtmlToText(body) }.ToMessageBody();
        else
            msg.Body = new TextPart("plain") { Text = body };

        // Threading khi trả lời.
        if (!string.IsNullOrWhiteSpace(inReplyToMessageId))
        {
            var mid = inReplyToMessageId.StartsWith("<") ? inReplyToMessageId : $"<{inReplyToMessageId}>";
            msg.InReplyTo = mid;
            msg.References.Add(mid);
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(address, appPassword, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
        _log.LogInformation("SMTP gửi tới {To}", toEmail);
    }

    private static bool LooksHtml(string s) => Regex.IsMatch(s ?? "", "<[a-z][\\s\\S]*>", RegexOptions.IgnoreCase);

    /// HTML → text thuần (fallback cho client không đọc HTML): <br>/<p>/<li> → xuống dòng, gỡ thẻ, decode.
    private static string HtmlToText(string html)
    {
        var s = Regex.Replace(html ?? "", "<\\s*(br|/p|/div|/li)\\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        return Regex.Replace(s, "\n{3,}", "\n\n").Trim();
    }
}
