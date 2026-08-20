// Services/Chat/Channels/ZaloChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Services.Digest;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Kênh Zalo OA — nhận tin khách nhắn và trả lời bằng API tin nhắn tư vấn.
///
/// <para><b>Dùng <c>message/cs</c>, KHÁC với bản tin sáng.</b> Bản tin dùng ZNS theo mẫu vì nó là
/// mình CHỦ ĐỘNG đẩy đi, lúc đó cửa sổ tư vấn luôn đóng. Chat thì ngược lại: khách vừa nhắn tới nên
/// cửa sổ vừa mở, và đây đúng là việc <c>message/cs</c> sinh ra để làm. Hai đường không mâu thuẫn —
/// đừng "sửa" cái này thành ZNS.</para>
///
/// <para>Tham khảo cách bóc sự kiện của ChatbotX (<c>integrations/zalo</c>): danh sách tên sự kiện
/// và công thức chữ ký lấy từ đó, phần còn lại viết lại cho khớp kiến trúc ở đây.</para>
/// </summary>
public class ZaloChatAdapter : IChatChannelAdapter
{
    private const string ApiBase = "https://openapi.zalo.me";
    private const string SendPath = "v3.0/oa/message/cs";

    /// Zalo trả mã này khi access token hết hạn — làm mới rồi thử lại đúng MỘT lần.
    private const int MaTokenHetHan = -1001;

    private readonly IHttpClientFactory _http;
    private readonly TenantChannelSettingsStore _cauHinh;
    private readonly ILogger<ZaloChatAdapter> _log;

    public ZaloChatAdapter(IHttpClientFactory http, TenantChannelSettingsStore cauHinh,
        ILogger<ZaloChatAdapter> log)
    { _http = http; _cauHinh = cauHinh; _log = log; }

    public ChatChannel Channel => ChatChannel.Zalo;

    // ── Xác thực webhook ────────────────────────────────────────────────────

    /// <summary>
    /// Chữ ký Zalo: <c>SHA256(appId + thânThô + timestamp + oaSecretKey)</c>, header dạng
    /// <c>mac=&lt;hash&gt;</c>.
    ///
    /// <para><b>Ký trên THÂN THÔ.</b> ChatbotX serialize lại từ object đã parse — cách đó chỉ đúng
    /// khi thứ tự khoá và khoảng trắng trùng khít bản gốc, mà .NET serialize lại gần như chắc chắn
    /// ra chuỗi khác. Đọc raw rồi ký thẳng trên chuỗi đó.</para>
    ///
    /// <para>So sánh bằng <see cref="CryptographicOperations.FixedTimeEquals"/> chứ không phải
    /// <c>==</c>: so chuỗi thường thoát ra ngay ở ký tự đầu khác nhau, đủ để dò dần ra chữ ký.</para>
    /// </summary>
    public async Task<bool> VerifyAsync(string tenantId, string rawBody, IHeaderDictionary headers,
        CancellationToken ct)
    {
        var cfg = await _cauHinh.GetZaloAsync(tenantId, ct);
        if (cfg?.SecretKey is not { Length: > 0 } secret || string.IsNullOrWhiteSpace(cfg.AppId))
        {
            _log.LogWarning("[chat/zalo] tenant={T} chưa khai OA — bỏ webhook", tenantId);
            return false;
        }

        var header = headers["X-ZEvent-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return false;
        var phan = header.Split('=', 2);
        if (phan.Length != 2) return false;

        string? timestamp;
        try { timestamp = JsonNode.Parse(rawBody)?["timestamp"]?.ToString(); }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(timestamp)) return false;

        var noiDung = cfg.AppId + rawBody + timestamp + secret;
        var mong = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noiDung))).ToLowerInvariant();

        var a = Encoding.ASCII.GetBytes(mong);
        var b = Encoding.ASCII.GetBytes(phan[1].Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ── Bóc sự kiện ─────────────────────────────────────────────────────────

    /// Tin do KHÁCH gửi tới.
    private static readonly Dictionary<string, ChatKind> KhachGui = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_send_text"] = ChatKind.Chu,
        ["user_send_image"] = ChatKind.Anh,
        ["user_send_file"] = ChatKind.Tep,
        ["user_send_audio"] = ChatKind.AmThanh,
        ["user_send_sticker"] = ChatKind.Sticker,
        ["user_send_location"] = ChatKind.ViTri,
    };

    /// <summary>
    /// Tin do CHÍNH OA gửi — tiếng vọng.
    ///
    /// <para><b>Đừng bỏ nhóm này.</b> Nhân viên trả lời từ app Zalo OA (không qua TourKit) thì mình
    /// chỉ biết qua đây. Bỏ qua thì hộp thư thiếu mất nửa cuộc trò chuyện, VÀ bot nói đè lên người
    /// thật vì không biết có ai đang trả lời.</para>
    /// </summary>
    private static readonly Dictionary<string, ChatKind> OaGui = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oa_send_text"] = ChatKind.Chu,
        ["oa_send_image"] = ChatKind.Anh,
        ["oa_send_file"] = ChatKind.Tep,
        ["oa_send_video"] = ChatKind.Tep,
        ["oa_send_sticker"] = ChatKind.Sticker,
        ["oa_send_gif"] = ChatKind.Anh,
        ["oa_send_link"] = ChatKind.Chu,
        ["oa_send_list"] = ChatKind.Chu,
        ["oa_send_carousel"] = ChatKind.Chu,
    };

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }
        if (goc?["event_name"]?.ToString() is not { Length: > 0 } ten) return ra;

        var luc = MocThoiGian(goc["timestamp"]?.ToString());

        // Khách đã xem tin — không phải tin nhắn, nhưng là tín hiệu thật cho nhân viên.
        if (ten.Equals("user_seen_message", StringComparison.OrdinalIgnoreCase))
        {
            var uid0 = goc["sender"]?["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(uid0))
                ra.Add(new(ChatChannel.Zalo, uid0!, null, ChatKind.Chu, null, null, luc, SeenMarker: "seen"));
            return ra;
        }

        var laKhach = KhachGui.TryGetValue(ten, out var loaiKhach);
        var laOa = !laKhach && OaGui.TryGetValue(ten, out var loaiOa);
        if (!laKhach && !laOa) return ra;   // sự kiện gắn thẻ, theo dõi… — chưa dùng

        // Tin của khách: người gửi là khách. Tiếng vọng: khách là NGƯỜI NHẬN.
        var uid = laKhach ? goc["sender"]?["id"]?.ToString() : goc["recipient"]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(uid)) return ra;

        var msg = goc["message"];
        var text = msg?["text"]?.ToString();
        var msgId = msg?["msg_id"]?.ToString();

        // Đính kèm giữ nguyên khối gốc để sau này hiện ảnh/tệp mà không phải bóc lại webhook.
        string? att = null;
        if (msg?["attachments"] is JsonArray a && a.Count > 0) att = a.ToJsonString();

        ra.Add(new(
            ChatChannel.Zalo, uid!, msgId,
            laKhach ? loaiKhach : OaGui[ten],
            text, att, luc,
            IsEcho: laOa,
            DisplayName: goc["sender"]?["name"]?.ToString()));
        return ra;
    }

    private static DateTime MocThoiGian(string? ms)
        => long.TryParse(ms, out var v)
            ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime
            : DateTime.UtcNow;

    // ── Gửi ─────────────────────────────────────────────────────────────────

    public async Task<SendResult> SendTextAsync(string tenantId, string externalUserId, string text,
        CancellationToken ct)
    {
        var cfg = await _cauHinh.GetZaloAsync(tenantId, ct);
        if (cfg is null || !cfg.IsUsable)
            // Thử lại cũng vô ích: thiếu cấu hình thì lần sau vẫn thiếu.
            return new(false, false, null, "Công ty chưa khai OA Zalo (Tự động hoá → Theo tổ chức)");

        var token = await LayAccessTokenAsync(tenantId, ct);
        if (token is null)
            return new(false, true, null, "Chưa lấy được access token Zalo — thử lại sau");

        var (ok, thuLai, id, loi) = await GoiApiGuiAsync(token, externalUserId, text, ct);

        // Token hết hạn KHÔNG tự làm mới ở đây — xem GetZaloAccessTokenAsync. Báo lỗi tạm thời để
        // hàng đợi thử lại; worker sẽ có token mới trong vòng một nhịp.
        if (!ok && loi is not null && loi.Contains(MaTokenHetHan.ToString()))
        {
            _log.LogInformation("[chat/zalo] token hết hạn — chờ worker làm mới, tenant={T}", tenantId);
            return new(false, true, null, "Access token Zalo hết hạn, đang chờ làm mới");
        }
        return new(ok, thuLai, id, loi);
    }

    private async Task<(bool ok, bool thuLai, string? id, string? loi)> GoiApiGuiAsync(
        string token, string uid, string text, CancellationToken ct)
    {
        var body = new
        {
            recipient = new { user_id = uid },
            message = new { text },
        };
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{SendPath}")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Add("access_token", token);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                // 5xx là hỏng tạm thời phía Zalo → đáng thử lại. 4xx là mình gửi sai → đừng.
                return (false, (int)res.StatusCode >= 500, null, $"HTTP {(int)res.StatusCode}: {Cat(raw)}");

            var o = JsonNode.Parse(raw)?.AsObject();
            var err = o?["error"]?.GetValue<int>() ?? 0;
            if (err != 0) return (false, false, null, $"Zalo lỗi {err}: {o?["message"]}");

            return (true, false, o?["data"]?["message_id"]?.ToString(), null);
        }
        catch (Exception ex)
        {
            // Mạng chập chờn → thử lại.
            return (false, true, null, ex.Message);
        }
    }

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];

    /// <summary>
    /// Lấy access token đang lưu. Worker bên toutkit-app là nơi xoay vòng token; ở đây chỉ ĐỌC.
    /// </summary>
    private async Task<string?> LayAccessTokenAsync(string tenantId, CancellationToken ct)
        => await _cauHinh.GetZaloAccessTokenAsync(tenantId, ct);
}
