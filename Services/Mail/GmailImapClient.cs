using System.Net.Sockets;
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
///
/// CHỐNG "An existing connection was forcibly closed" (RST) khi auto-sync:
///  1. CHECKPOINT mỗi N email (xử lý CŨ→MỚI): RST giữa chừng vẫn giữ tiến độ → lần sau chạy tiếp,
///     KHÔNG làm lại cả lô → phá vòng lặp lỗi.
///  2. CAP `max` cả nhánh incremental → mỗi run kéo tối đa `max`, drain backlog dần qua nhiều chu kỳ
///     (tránh 1 kết nối ôm cả trăm/nghìn thư → Gmail RST).
///  3. DISCONNECT "sạch" (LOGOUT) kể cả khi lỗi (try/finally) → không để session ma trên Gmail
///     (zombie session tích lại → chạm giới hạn ~15 kết nối → chính nó gây RST lần sau).
///  4. TIMEOUT 60s + RETRY/backoff cho lỗi socket tạm thời.
public class GmailImapClient : IMailSource
{
    private const string ImapHost = "imap.gmail.com";
    private const int ImapPort = 993;
    private const int CheckpointEvery = 10;   // lưu mốc UID sau mỗi N email
    private const int MaxAttempts = 3;        // 1 lần đầu + 2 retry

    private readonly MailAccountStore _account;
    private readonly MailSyncStore _sync;
    private readonly ILogger<GmailImapClient> _log;

    public GmailImapClient(MailAccountStore account, MailSyncStore sync, ILogger<GmailImapClient> log)
    {
        _account = account; _sync = sync; _log = log;
    }

    public async Task<IReadOnlyList<MailItem>> FetchRecentAsync(string tenantId, string username, int max, CancellationToken ct)
    {
        // Creds + sync state scoped theo (tenant, user) — không leak cross-tenant, cũng không leak cross-user.
        var creds = _account.Get(tenantId, username);
        if (creds is not { } c0 || string.IsNullOrWhiteSpace(c0.Address) || string.IsNullOrWhiteSpace(c0.AppPassword))
            throw new InvalidOperationException(
                "Chưa cấu hình hộp thư Gmail. Nhập địa chỉ + App Password (16 ký tự) ở phần Cấu hình hộp thư.");
        var (address, appPassword) = (c0.Address, c0.AppPassword);

        // Retry trên lỗi socket TẠM THỜI (RST/timeout/rớt giữa chừng). Nhờ checkpoint, retry không
        // làm lại từ đầu mà tiếp từ mốc đã lưu.
        Exception? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);   // backoff: 2s, 4s
            try
            {
                return await FetchOnceAsync(tenantId, address, appPassword, max, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                last = ex;
                _log.LogWarning("IMAP {Addr} attempt {A}/{N} lỗi tạm thời: {Err} — thử lại",
                    address, attempt + 1, MaxAttempts, ex.Message);
            }
        }
        throw last ?? new Exception("IMAP fetch thất bại không rõ nguyên nhân");
    }

    /// 1 lần kết nối + kéo. Tách riêng để retry wrapper gọi lại sạch.
    private async Task<IReadOnlyList<MailItem>> FetchOnceAsync(
        string tenantId, string address, string appPassword, int max, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = 60_000 };   // 60s — tránh treo vô hạn
        var items = new List<MailItem>();
        try
        {
            await client.ConnectAsync(ImapHost, ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(address, appPassword, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            var uidValidity = inbox.UidValidity;
            var state = _sync.Get(tenantId, address);

            // Liệt kê toàn bộ UID (rẻ — chỉ id), rồi chọn phần cần kéo.
            var allUids = await inbox.SearchAsync(SearchQuery.All, ct);
            if (allUids.Count == 0)
            {
                _sync.Set(tenantId, address, uidValidity, state?.LastUid ?? 0);
                return items;
            }
            var globalMax = allUids.Max(u => u.Id);

            bool incremental = state != null && state.UidValidity == uidValidity && state.LastUid > 0;
            List<UniqueId> toFetch = incremental
                // CŨ→MỚI, CAP max → drain backlog dần; resume đúng nhờ checkpoint tăng dần.
                ? allUids.Where(u => u.Id > state!.LastUid).OrderBy(u => u.Id).Take(max).ToList()
                // Lần đầu / reset: newest max, rồi xử lý ascending (cũ→mới trong lô).
                : allUids.OrderByDescending(u => u.Id).Take(max).OrderBy(u => u.Id).ToList();

            if (toFetch.Count == 0)
            {
                // Không có gì mới → đồng bộ mốc (incremental giữ lastUid; first-time/empty đặt globalMax).
                _sync.Set(tenantId, address, uidValidity, incremental ? state!.LastUid : globalMax);
                return items;
            }

            // Lấy cờ \Seen (cho IsRead) 1 lần cho cả lô.
            var summaries = await inbox.FetchAsync(toFetch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
            var seen = summaries.ToDictionary(s => s.UniqueId, s => s.Flags?.HasFlag(MessageFlags.Seen) ?? false);

            // Xử lý ASCENDING (cũ→mới) + CHECKPOINT mỗi N → RST giữa chừng vẫn giữ tiến độ.
            int sinceCheckpoint = 0;
            uint lastDone = incremental ? state!.LastUid : 0;
            foreach (var uid in toFetch)   // toFetch đã ascending
            {
                ct.ThrowIfCancellationRequested();
                var msg = await inbox.GetMessageAsync(uid, ct);
                var isRead = seen.TryGetValue(uid, out var s) && s;
                items.Add(MailMapper.FromMime(msg, fallbackId: $"{address}:{uid.Id}", isRead: isRead));
                lastDone = uid.Id;
                if (++sinceCheckpoint >= CheckpointEvery)
                {
                    _sync.Set(tenantId, address, uidValidity, lastDone);
                    sinceCheckpoint = 0;
                }
            }

            // Checkpoint cuối: nếu lô chạm đỉnh hộp thư (đã kéo hết backlog) → globalMax; else mốc đang dở.
            var finalUid = toFetch[^1].Id >= globalMax ? globalMax : lastDone;
            _sync.Set(tenantId, address, uidValidity, finalUid);

            _log.LogInformation("IMAP kéo {N} email từ {Addr} (incremental={Inc}, lastUid={Uid}, backlogCòn={More})",
                items.Count, address, incremental, finalUid, finalUid < globalMax);
            return items;
        }
        finally
        {
            // DISCONNECT SẠCH (gửi LOGOUT) kể cả khi lỗi giữa chừng → không để session ma trên Gmail.
            // Dùng CancellationToken.None: dù outer ct đã cancel/timeout vẫn cố logout cho gọn.
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); }
                catch { /* best-effort — kết nối có thể đã chết */ }
            }
        }
    }

    /// Lỗi socket/IO/giao thức rớt giữa chừng → đáng retry (khác lỗi auth/cấu hình).
    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException) return true;                 // RST / connection reset
            if (e is IOException) return true;                     // stream rớt
            if (e is ImapProtocolException) return true;           // server đóng giữa lệnh
            if (e is ServiceNotConnectedException) return true;    // mất kết nối
            var msg = e.Message;
            if (msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("connection was reset", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
