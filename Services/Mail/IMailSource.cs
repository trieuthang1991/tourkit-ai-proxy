using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// <summary>Nguồn mail per-tenant — pull email mới hơn lần sync trước theo TenantId.</summary>
public interface IMailSource
{
    /// Pull N email mới nhất cho tenant. Incremental: chỉ email có UID > lần trước.
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(string tenantId, int max, CancellationToken ct);
}
