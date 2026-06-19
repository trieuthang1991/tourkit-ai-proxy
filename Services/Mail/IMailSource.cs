using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// <summary>Nguồn mail per-(tenant, user) — pull email mới hơn lần sync trước theo creds của user.</summary>
public interface IMailSource
{
    /// Pull N email mới nhất cho (tenant, user). Incremental: chỉ email có UID > lần trước.
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(string tenantId, string username, int max, CancellationToken ct);
}
