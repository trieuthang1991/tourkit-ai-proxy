// Services/Chat/Channels/MessengerChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Facebook Messenger (và Instagram Direct — cùng một nền tảng Messenger Platform của Meta,
/// khác mỗi trang kết nối).
///
/// <para>Khoá cần khai cho mỗi công ty: <c>pageId</c> · <c>pageAccessToken</c> · <c>appSecret</c>
/// (để kiểm chữ ký) · <c>verifyToken</c> (để Meta xác minh địa chỉ webhook lần đầu).</para>
///
/// <para>Tham khảo cách bóc sự kiện của ChatbotX (<c>integrations/messenger</c>).</para>
/// </summary>
public class MessengerChatAdapter : IChatChannelAdapter
{
    private const string GraphBase = "https://graph.facebook.com/v21.0";

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly ILogger<MessengerChatAdapter> _log;

    public MessengerChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        ILogger<MessengerChatAdapter> log)
    { _http = http; _cred = cred; _log = log; }

    public ChatChannel Channel => ChatChannel.Messenger;

    /// <summary>
    /// Meta xác minh địa chỉ webhook bằng một lượt GET kèm <c>hub.challenge</c> — phải trả lại đúng
    /// chuỗi đó thì mới đăng ký được. Đây là bước RIÊNG, không phải chữ ký của tin nhắn.
    /// </summary>
    public async Task<string?> XacMinhDangKyAsync(string tenantId, string? mode, string? token,
        string? challenge, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, ct);
        if (c is null || !c.TryGetValue("verifyToken", out var mong)) return null;
        if (mode != "subscribe" || token != mong) return null;
        return challenge;
    }

    /// <summary>
    /// Chữ ký Meta: <c>X-Hub-Signature-256: sha256=&lt;HMAC-SHA256(appSecret, thânThô)&gt;</c>.
    ///
    /// <para>HMAC chứ không phải SHA thường như Zalo — hai kênh hai kiểu, đừng chép qua lại.</para>
    /// </summary>
    public async Task<bool> VerifyAsync(string tenantId, string rawBody, IHeaderDictionary headers,
        CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, ct);
        if (c is null || !c.TryGetValue("appSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
        {
            _log.LogWarning("[chat/messenger] tenant={T} chưa khai appSecret — bỏ webhook", tenantId);
            return false;
        }

        var header = headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return false;
        var phan = header.Split('=', 2);
        if (phan.Length != 2 || !phan[0].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mong = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

        var a = Encoding.ASCII.GetBytes(mong);
        var b = Encoding.ASCII.GetBytes(phan[1].Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        // Meta gói nhiều sự kiện trong một lần gọi: entry[] × messaging[]. Bóc thiếu vòng lặp là
        // mất tin khi khách nhắn dồn.
        if (goc?["entry"] is not JsonArray entries) return ra;
        foreach (var e in entries)
        {
            if (e?["messaging"] is not JsonArray ms) continue;
            foreach (var m in ms)
            {
                if (m is null) continue;
                var msg = m["message"];
                if (msg is null) continue;   // delivery/read/postback — chưa dùng ở đợt này

                // is_echo = tin do CHÍNH trang gửi. Nhân viên trả lời từ Trang hoặc từ ứng dụng
                // Meta Business thì mình chỉ biết qua đây — bỏ là hộp thư thiếu nửa cuộc trò chuyện.
                var vong = msg["is_echo"]?.GetValue<bool>() ?? false;
                var uid = vong ? m["recipient"]?["id"]?.ToString() : m["sender"]?["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(uid)) continue;

                var loai = ChatKind.Chu;
                string? att = null;
                if (msg["attachments"] is JsonArray a && a.Count > 0)
                {
                    att = a.ToJsonString();
                    loai = a[0]?["type"]?.ToString() switch
                    {
                        "image" => ChatKind.Anh,
                        "audio" => ChatKind.AmThanh,
                        "video" or "file" => ChatKind.Tep,
                        "location" => ChatKind.ViTri,
                        _ => ChatKind.Tep,
                    };
                }

                var luc = long.TryParse(m["timestamp"]?.ToString(), out var ts)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime : DateTime.UtcNow;

                ra.Add(new(ChatChannel.Messenger, uid!, msg["mid"]?.ToString(), loai,
                    msg["text"]?.ToString(), att, luc, IsEcho: vong));
            }
        }
        return ra;
    }

    public async Task<SendResult> SendTextAsync(string tenantId, string externalUserId, string text,
        CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, ct);
        if (c is null || !c.TryGetValue("pageAccessToken", out var token) || string.IsNullOrWhiteSpace(token))
            return new(false, false, null, "Công ty chưa khai Trang Facebook (thiếu page access token)");

        try
        {
            var http = _http.CreateClient();
            var body = new
            {
                recipient = new { id = externalUserId },
                // RESPONSE = đang trả lời khách trong cửa sổ 24 giờ. Gửi ngoài cửa sổ phải dùng
                // message_tag, mà cái đó Meta duyệt theo từng mục đích — chưa làm ở đợt này.
                messaging_type = "RESPONSE",
                message = new { text },
            };
            using var res = await http.PostAsJsonAsync($"{GraphBase}/me/messages?access_token={token}", body, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                return (int)res.StatusCode >= 500
                    ? new(false, true, null, $"Meta lỗi tạm thời {(int)res.StatusCode}")
                    : new(false, false, null, $"Meta từ chối {(int)res.StatusCode}: {Cat(raw)}");

            var o = JsonNode.Parse(raw)?.AsObject();
            return new(true, false, o?["message_id"]?.ToString(), null);
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);   // mạng chập chờn → thử lại
        }
    }

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];
}
