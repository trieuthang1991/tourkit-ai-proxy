using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// IMailSource qua IMAP Gmail (MailKit). Đọc-only INBOX. Auth bằng App Password
/// (cần bật 2-Step Verification + bật IMAP trong Gmail).
///
/// ĐỒNG BỘ INCREMENTAL theo UID (MailSyncStore): chỉ kéo email có UID lớn hơn lần trước →
/// KHÔNG sót dù có >max email mới giữa 2 lần sync. Lần đầu (chưa có state) chỉ kéo `max` mới nhất
/// để tránh nuốt cả nghìn thư cũ. UidValidity đổi → reset, kéo lại newest `max`.
/// Trạng thái \Seen của Gmail map sang IsRead lúc kéo.
public class GmailImapClient : IMailSource
{
    private const string ImapHost = "imap.gmail.com";
    private const int ImapPort = 993;

    private readonly MailAccountStore _account;
    private readonly MailSyncStore _sync;
    private readonly ILogger<GmailImapClient> _log;

    public GmailImapClient(MailAccountStore account, MailSyncStore sync, ILogger<GmailImapClient> log)
    {
        _account = account; _sync = sync; _log = log;
    }

    public async Task<IReadOnlyList<MailItem>> FetchRecentAsync(string tenantId, int max, CancellationToken ct)
    {
        // Creds + sync state scoped theo tenant — không leak cross-tenant.
        var creds = _account.Get(tenantId);
        if (creds is not { } c0 || string.IsNullOrWhiteSpace(c0.Address) || string.IsNullOrWhiteSpace(c0.AppPassword))
            throw new InvalidOperationException(
                "Chưa cấu hình hộp thư Gmail. Nhập địa chỉ + App Password (16 ký tự) ở phần Cấu hình hộp thư.");
        var (address, appPassword) = (c0.Address, c0.AppPassword);

        using var client = new ImapClient();
        await client.ConnectAsync(ImapHost, ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(address, appPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var uidValidity = inbox.UidValidity;
        var state = _sync.Get(tenantId, address);

        // Liệt kê toàn bộ UID (rẻ — chỉ id), rồi chọn phần cần kéo.
        var allUids = await inbox.SearchAsync(SearchQuery.All, ct);
        List<UniqueId> toFetch;
        if (state != null && state.UidValidity == uidValidity && state.LastUid > 0)
            toFetch = allUids.Where(u => u.Id > state.LastUid).OrderBy(u => u.Id).ToList();   // incremental
        else
            toFetch = allUids.OrderByDescending(u => u.Id).Take(max).OrderBy(u => u.Id).ToList();  // lần đầu / reset

        var items = new List<MailItem>();
        if (toFetch.Count > 0)
        {
            // Lấy cờ \Seen (cho IsRead) 1 lần cho cả lô.
            var summaries = await inbox.FetchAsync(toFetch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
            var seen = summaries.ToDictionary(s => s.UniqueId, s => s.Flags?.HasFlag(MessageFlags.Seen) ?? false);

            foreach (var uid in toFetch.OrderByDescending(u => u.Id))   // mới nhất trước
            {
                ct.ThrowIfCancellationRequested();
                var msg = await inbox.GetMessageAsync(uid, ct);
                var isRead = seen.TryGetValue(uid, out var s) && s;
                items.Add(MailMapper.FromMime(msg, fallbackId: $"{address}:{uid.Id}", isRead: isRead));
            }
        }

        // Cập nhật mốc UID cao nhất đã thấy (kể cả khi lần đầu chỉ kéo newest `max`).
        if (allUids.Count > 0)
            _sync.Set(tenantId, address, uidValidity, allUids.Max(u => u.Id));

        await client.DisconnectAsync(true, ct);
        _log.LogInformation("IMAP kéo {N} email mới từ {Addr} (incremental={Inc})",
            items.Count, address, state != null && state.UidValidity == uidValidity);
        return items;
    }
}
