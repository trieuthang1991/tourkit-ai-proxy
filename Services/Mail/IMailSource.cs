using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Nguồn mail trừu tượng. Phase 1: GmailImapClient (IMAP). Phase 2/sau: OAuth có thể implement
/// interface này mà không đụng phần còn lại (endpoint/service agnostic về nguồn).
public interface IMailSource
{
    /// Kéo tối đa `max` email mới nhất từ INBOX. Throw InvalidOperationException nếu chưa cấu hình,
    /// hoặc exception khác nếu kết nối/auth lỗi.
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(int max, CancellationToken ct);
}
