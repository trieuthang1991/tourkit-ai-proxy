using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Kênh Telegram — gửi qua <c>api.telegram.org/bot{token}/sendMessage</c>.
///
/// <para><b>Một bot dùng chung cho cả hệ thống</b> (<c>Telegram:BotToken</c>), KHÔNG phải mỗi công ty
/// một bot. Lý do: bot Telegram miễn phí và không có hạn mức đáng kể, nên bắt từng công ty tự tạo
/// bot chỉ thêm một bước cản mà chẳng tiết kiệm gì. Khác hẳn Zalo OA — cái đó tốn tiền và hạn mức
/// tính theo từng OA nên buộc phải tách per-tenant.</para>
///
/// <para>Chưa khai token → kênh tự tắt và chỉ log MỘT lần, không log mỗi lượt gửi cho khỏi rác log.</para>
/// </summary>
public class TelegramChannel : IDigestChannel
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TelegramChannel> _log;
    private readonly string _token;
    private int _warnedNoToken;   // 0/1 — chỉ log cảnh báo thiếu token 1 lần

    public TelegramChannel(IHttpClientFactory http, IConfiguration cfg, ILogger<TelegramChannel> log)
    {
        _http = http; _log = log;
        _token = cfg["Telegram:BotToken"] ?? "";
    }

    public string Id => "telegram";

    public bool IsConfigured(DigestSubscription sub)
    {
        if (!sub.ChannelTelegram || string.IsNullOrWhiteSpace(sub.TelegramChatId)) return false;
        if (string.IsNullOrWhiteSpace(_token))
        {
            if (Interlocked.Exchange(ref _warnedNoToken, 1) == 0)
                _log.LogWarning("[digest/telegram] chưa cấu hình Telegram:BotToken — kênh Telegram TẮT cho mọi tenant");
            return false;
        }
        return true;
    }

    public Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
        => SendToChatAsync(sub.TelegramChatId ?? "", m.Title, m.BodyMarkdown, sub.TenantId, sub.Username, ct);

    /// <summary>
    /// Gửi lõi — chỉ cần nơi nhận + nội dung, KHÔNG cần bản đăng ký. Tách ra để hàng đợi
    /// (<see cref="OutboundChannelDrainer"/>) gửi được: lúc đó bản tin đã dựng xong từ trước,
    /// trong tay chỉ còn chat id lưu trong dòng hàng đợi.
    /// </summary>
    public async Task<bool> SendToChatAsync(string chatId, string title, string? bodyMd,
        string tenantId, string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            if (Interlocked.Exchange(ref _warnedNoToken, 1) == 0)
                _log.LogWarning("[digest/telegram] chưa cấu hình Telegram:BotToken — kênh Telegram TẮT cho mọi tenant");
            return false;
        }
        if (string.IsNullOrWhiteSpace(chatId))
        {
            _log.LogWarning("[digest/telegram] thiếu chat id tenant={T} user={U} — bỏ qua", tenantId, username);
            return false;
        }

        try
        {
            var text = TelegramFormat.ToTelegramHtml(title, bodyMd ?? "");
            var body = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true,
            });

            var c = _http.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await c.PostAsync(
                $"https://api.telegram.org/bot{_token}/sendMessage",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);

            if (resp.IsSuccessStatusCode) return true;

            // Đọc nội dung lỗi vì Telegram nói rất rõ nguyên nhân ("chat not found", "bot was blocked
            // by the user") — không đọc thì chỉ thấy 400 trơ trọi, không biết bảo người dùng làm gì.
            var err = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("[digest/telegram] gửi hỏng {Status} tenant={T} user={U}: {Err}",
                (int)resp.StatusCode, tenantId, username, err.Length > 300 ? err[..300] : err);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[digest/telegram] lỗi mạng tenant={T} user={U}", tenantId, username);
            return false;
        }
    }
}
