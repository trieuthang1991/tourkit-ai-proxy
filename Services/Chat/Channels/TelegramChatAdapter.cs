// Services/Chat/Channels/TelegramChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Telegram.
///
/// <para><b>Khác hai kênh kia ở hai điểm quan trọng:</b></para>
/// <list type="bullet">
/// <item><b>Không có cửa sổ thời gian.</b> Nhắn lại lúc nào cũng được, miễn người dùng chưa chặn
/// bot. Nên đây là kênh duy nhất trong ba kênh mà nhân viên chủ động mở lời được.</item>
/// <item><b>Không ký chữ ký.</b> Telegram xác thực bằng một chuỗi bí mật khai lúc đăng ký địa chỉ
/// webhook, gửi lại trong header <c>X-Telegram-Bot-Api-Secret-Token</c>. Không khai chuỗi đó thì
/// <b>ai biết địa chỉ webhook cũng bơm tin vào được</b> — nên ở đây bắt buộc phải có.</item>
/// </list>
///
/// <para>Bot token có thể lấy từ khoá riêng của công ty, hoặc rơi về <c>Telegram:BotToken</c> dùng
/// chung — bot Telegram miễn phí nên hệ thống cấp sẵn một con, khác hẳn Zalo OA (phải là OA riêng
/// vì tin hiện tên người gửi).</para>
/// </summary>
public class TelegramChatAdapter : IChatChannelAdapter
{
    private const string ApiBase = "https://api.telegram.org";

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TelegramChatAdapter> _log;

    public TelegramChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<TelegramChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.Telegram;

    private async Task<string?> TokenAsync(string tenantId, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, ct);
        if (c is not null && c.TryGetValue("botToken", out var rieng) && !string.IsNullOrWhiteSpace(rieng))
            return rieng;
        var chung = _cfg["Telegram:BotToken"];
        return string.IsNullOrWhiteSpace(chung) ? null : chung;
    }

    public async Task<bool> VerifyAsync(string tenantId, string rawBody, IHeaderDictionary headers,
        CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, ct);
        if (c is null || !c.TryGetValue("webhookSecret", out var mong) || string.IsNullOrWhiteSpace(mong))
        {
            // KHÔNG cho qua khi thiếu chuỗi bí mật. Telegram không ký nội dung, nên chuỗi này là
            // thứ DUY NHẤT ngăn người ngoài bơm tin giả vào hộp thư.
            _log.LogWarning("[chat/telegram] tenant={T} chưa khai webhookSecret — bỏ webhook", tenantId);
            return false;
        }

        var gui = headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(gui)) return false;

        var a = Encoding.UTF8.GetBytes(mong);
        var b = Encoding.UTF8.GetBytes(gui);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        // edited_message: khách sửa lại tin đã gửi. Coi như tin mới — id khác nên không trùng, và
        // nội dung sửa thường là ý họ thật sự muốn nói.
        var msg = goc?["message"] ?? goc?["edited_message"];
        if (msg is null) return ra;

        var chatId = msg["chat"]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(chatId)) return ra;

        var loai = ChatKind.Chu;
        string? att = null;
        if (msg["photo"] is JsonArray p && p.Count > 0) { loai = ChatKind.Anh; att = p.ToJsonString(); }
        else if (msg["document"] is JsonNode d) { loai = ChatKind.Tep; att = d.ToJsonString(); }
        else if (msg["voice"] is JsonNode v) { loai = ChatKind.AmThanh; att = v.ToJsonString(); }
        else if (msg["location"] is JsonNode l) { loai = ChatKind.ViTri; att = l.ToJsonString(); }
        else if (msg["sticker"] is JsonNode s) { loai = ChatKind.Sticker; att = s.ToJsonString(); }

        var ten = string.Join(' ', new[] { msg["from"]?["first_name"]?.ToString(),
                                           msg["from"]?["last_name"]?.ToString() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        var luc = long.TryParse(msg["date"]?.ToString(), out var d2)
            ? DateTimeOffset.FromUnixTimeSeconds(d2).UtcDateTime : DateTime.UtcNow;

        ra.Add(new(ChatChannel.Telegram, chatId!,
            // Telegram đánh số tin theo từng cuộc trò chuyện, không phải toàn cục — phải ghép
            // chat id vào, không thì hai khách khác nhau đụng cùng một số và tin sau bị coi là trùng.
            $"{chatId}:{msg["message_id"]}",
            loai, msg["text"]?.ToString() ?? msg["caption"]?.ToString(), att, luc,
            DisplayName: string.IsNullOrWhiteSpace(ten) ? msg["from"]?["username"]?.ToString() : ten));
        return ra;
    }

    public async Task<SendResult> SendTextAsync(string tenantId, string externalUserId, string text,
        CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, ct);
        if (token is null)
            return new(false, false, null, "Chưa khai bot token Telegram");

        try
        {
            var http = _http.CreateClient();
            using var res = await http.PostAsJsonAsync($"{ApiBase}/bot{token}/sendMessage",
                new { chat_id = externalUserId, text }, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            if (o?["ok"]?.GetValue<bool>() == true)
                return new(true, false, o["result"]?["message_id"]?.ToString(), null);

            var moTa = o?["description"]?.ToString() ?? Cat(raw);
            // 5xx là hỏng tạm thời phía Telegram; 4xx là mình sai (khách chặn bot, sai chat id) —
            // thử lại chỉ tốn công.
            return new(false, (int)res.StatusCode >= 500, null, $"Telegram từ chối: {moTa}");
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);
        }
    }

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];
}
