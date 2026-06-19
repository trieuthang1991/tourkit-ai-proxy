using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Gửi email. Phase 1: GmailSmtpClient (SMTP Gmail). Tách interface để sau đổi nguồn gửi.
public interface IMailSender
{
    /// Gửi email tới `toEmail`. `inReplyToMessageId` (đã có '<>' hay chưa) để gắn luồng khi trả lời.
    /// `(tenantId, username)` để chọn đúng hộp thư Gmail của user trong tenant.
    /// Throw InvalidOperationException nếu chưa cấu hình / thiếu người nhận; exception khác nếu SMTP lỗi.
    Task SendAsync(string tenantId, string username, string toEmail, string? toName, string subject, string body, string? inReplyToMessageId, CancellationToken ct);

    /// Tiện ích trả lời 1 email gốc (giữ subject "Re:" + gắn In-Reply-To).
    Task SendReplyAsync(string tenantId, string username, MailItem original, string body, CancellationToken ct)
    {
        var subj = original.Subject ?? "";
        var reSubj = subj.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subj : "Re: " + subj;
        var mid = !string.IsNullOrWhiteSpace(original.Id) && original.Id.Contains('@') ? original.Id : null;
        return SendAsync(tenantId, username, original.From.Email, original.From.Name, reSubj, body, mid, ct);
    }
}
