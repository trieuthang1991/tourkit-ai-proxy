// Services/Chat/Channels/MessengerChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Facebook Messenger (và Instagram Direct — cùng một nền tảng Messenger Platform của Meta,
/// khác mỗi trang kết nối).
///
/// <para>Khoá cần khai cho mỗi TÀI KHOẢN (= một Trang Facebook): <c>pageId</c> ·
/// <c>pageAccessToken</c> · <c>appSecret</c> (để kiểm chữ ký) · <c>verifyToken</c> (để Meta xác
/// minh địa chỉ webhook lần đầu).</para>
///
/// <para><b>Nhiều Trang dùng CHUNG một đường webhook</b> — Meta đăng ký webhook theo ỨNG DỤNG
/// (Facebook App), không theo Trang; nhiều Trang cùng App tự động cùng gọi vào một URL. Do đó
/// <c>VerifyAsync</c> không nhận sẵn mã tài khoản trên URL như Telegram, mà phải TỰ TÌM: (1) kiểm
/// chữ ký với TỪNG <c>appSecret</c> đã khai (nhiều Trang có thể chung App → chung secret, thử là
/// đủ biết có phải traffic thật của công ty này không); (2) sau khi qua được bước 1, đọc ID Trang
/// thật trong thân tin (<c>entry[].id</c>) rồi khớp với tài khoản đã khai đúng Trang đó — bước
/// này BẮT BUỘC vì hai Trang cùng App có secret giống hệt nhau, chữ ký không phân biệt nổi.</para>
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
    ///
    /// <para>Xác minh diễn ra TRƯỚC khi Meta biết Trang nào sẽ gửi tin (bước này ở cấp App), nên
    /// khớp với BẤT KỲ <c>verifyToken</c> nào công ty đã khai — không cần biết trước là Trang nào.</para>
    /// </summary>
    public async Task<string?> XacMinhDangKyAsync(string tenantId, string? mode, string? token,
        string? challenge, CancellationToken ct)
    {
        if (mode != "subscribe" || string.IsNullOrWhiteSpace(token)) return null;
        var taiKhoan = await _cred.ListAccountsAsync(tenantId, Channel, ct);
        var khop = taiKhoan.Any(t => t.GiaTri.TryGetValue("verifyToken", out var mong) && mong == token);
        return khop ? challenge : null;
    }

    /// <summary>
    /// Chữ ký Meta: <c>X-Hub-Signature-256: sha256=&lt;HMAC-SHA256(appSecret, thânThô)&gt;</c>.
    ///
    /// <para>HMAC chứ không phải SHA thường như Zalo — hai kênh hai kiểu, đừng chép qua lại.</para>
    /// </summary>
    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        var header = headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null;
        var phan = header.Split('=', 2);
        if (phan.Length != 2 || !phan[0].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
        var chuKyGui = Encoding.ASCII.GetBytes(phan[1].Trim().ToLowerInvariant());

        var taiKhoan = await _cred.ListAccountsAsync(tenantId, Channel, ct);
        if (taiKhoan.Count == 0)
        {
            _log.LogWarning("[chat/messenger] tenant={T} chưa khai Trang nào — bỏ webhook", tenantId);
            return null;
        }

        // Bước 1: chữ ký chứng minh đây là traffic THẬT của công ty (thử từng secret, nhiều Trang
        // cùng App sẽ trùng secret nên chỉ cần tìm MỘT khớp).
        var quaChuKy = taiKhoan.Any(t =>
        {
            if (!t.GiaTri.TryGetValue("appSecret", out var secret) || string.IsNullOrWhiteSpace(secret)) return false;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var mong = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
            var a = Encoding.ASCII.GetBytes(mong);
            return a.Length == chuKyGui.Length && CryptographicOperations.FixedTimeEquals(a, chuKyGui);
        });
        if (!quaChuKy) return null;

        // Bước 2: chữ ký đúng chỉ chứng minh "của công ty này", CHƯA biết Trang nào — đọc ID Trang
        // thật trong thân tin rồi khớp với tài khoản đã khai đúng Trang đó.
        var pageId = DocPageId(rawBody);
        if (pageId is null) return null;
        var taiKhoanKhop = taiKhoan.FirstOrDefault(t => t.GiaTri.GetValueOrDefault("pageId") == pageId);
        if (taiKhoanKhop is null)
            _log.LogWarning("[chat/messenger] tenant={T} nhận tin từ Trang {P} chưa khai tài khoản nào", tenantId, pageId);
        return taiKhoanKhop?.AccountId;
    }

    private static string? DocPageId(string rawBody)
    {
        try
        {
            var goc = JsonNode.Parse(rawBody);
            return (goc?["entry"] as JsonArray)?.OfType<JsonNode>().FirstOrDefault()?["id"]?.ToString();
        }
        catch { return null; }
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

                // Meta báo trạng thái tin MÌNH đã gửi bằng hai gói riêng, không nằm trong "message":
                //   delivery: {"mids":[…], "watermark": <ms>}  — đã tới máy khách
                //   read:     {"watermark": <ms>}              — khách đã đọc
                // Dùng watermark chứ không dùng mids: "read" không có mids, đi chung một đường thì
                // ít code hơn và hai loại không lệch hành vi.
                //
                // ⚠️ Người gửi ở hai gói này là KHÁCH (ngược với tin echo). Lấy nhầm recipient là
                // đánh dấu vào hội thoại của chính Trang mình — tức là không hội thoại nào cả.
                var tt = m["delivery"] is not null ? ChatState.DaNhan
                       : m["read"] is not null ? ChatState.DaXem
                       : (ChatState?)null;
                if (tt is { } trangThai)
                {
                    var uidM = m["sender"]?["id"]?.ToString();
                    var wm = m[trangThai == ChatState.DaNhan ? "delivery" : "read"]?["watermark"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidM) && long.TryParse(wm, out var wms))
                    {
                        var mocLuc = DateTimeOffset.FromUnixTimeMilliseconds(wms).UtcDateTime;
                        ra.Add(new(ChatChannel.Messenger, uidM!, null, ChatKind.Chu, null, null,
                            mocLuc, Watermark: new(trangThai, mocLuc)));
                    }
                    continue;
                }

                var msg = m["message"];
                if (msg is null) continue;   // postback, opt-in… — chưa dùng

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

    public async Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => await GuiAsync(tenantId, accountId, externalUserId, new { text }, ct);

    /// <summary>
    /// Messenger Send API nhận media qua <c>attachment.payload.url</c> — Meta TỰ TẢI ảnh/tệp từ
    /// URL đó, không nhận nhị phân trực tiếp. Chữ chú thích không gộp được vào cùng tin ảnh, nên
    /// nếu có <paramref name="caption"/> thì gửi thêm một tin chữ ngay sau, giống cách Zalo xử lý.
    /// </summary>
    public async Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        var loaiMeta = loai switch { ChatKind.Anh => "image", ChatKind.AmThanh => "audio", _ => "file" };
        var kq = await GuiAsync(tenantId, accountId, externalUserId, new
        {
            attachment = new { type = loaiMeta, payload = new { url, is_reusable = true } },
        }, ct);
        if (kq.Ok && !string.IsNullOrWhiteSpace(caption))
            await GuiAsync(tenantId, accountId, externalUserId, new { text = caption }, ct);
        return kq;
    }

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, string externalUserId,
        object noiDungTin, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null || !c.TryGetValue("pageAccessToken", out var token) || string.IsNullOrWhiteSpace(token))
            return new(false, false, null, "Trang Facebook này chưa khai (thiếu page access token)");

        try
        {
            var http = _http.CreateClient();
            var body = new
            {
                recipient = new { id = externalUserId },
                // RESPONSE = đang trả lời khách trong cửa sổ 24 giờ. Gửi ngoài cửa sổ phải dùng
                // message_tag, mà cái đó Meta duyệt theo từng mục đích — chưa làm ở đợt này.
                messaging_type = "RESPONSE",
                message = noiDungTin,
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
