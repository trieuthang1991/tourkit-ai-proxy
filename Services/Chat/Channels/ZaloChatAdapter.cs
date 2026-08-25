// Services/Chat/Channels/ZaloChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Kênh Zalo OA — nhận tin khách nhắn và trả lời bằng API tin nhắn tư vấn.
///
/// <para><b>Độc lập hoàn toàn với Zalo OA của bản tin sáng</b> (<see cref="Digest.TenantChannelSettingsStore"/>).
/// Trước 24/08 chat dùng CHUNG cấu hình OA với bản tin — đơn giản nhưng ép mỗi công ty chỉ một OA
/// cho cả hai việc, mà thực tế nhiều công ty có nhiều OA cho nhiều chi nhánh/đội sale. Nay chat tự
/// quản OA của mình (tiền tố <c>chat-zalo</c> trong <see cref="ChannelCredentialStore"/>), NHIỀU
/// tài khoản/công ty, và <b>tự làm mới access token của MÌNH</b> — không đọc/ghi vào cấu hình OA
/// của bản tin, không có chuyện hai nơi cùng xoay một refresh token (Zalo sẽ vô hiệu hoá cái cũ,
/// bên chậm chân mất token vĩnh viễn — đúng lỗi mà bản tin đã né bằng cách giao hẳn việc xoay vòng
/// cho một nơi duy nhất; chat nay LÀ nơi duy nhất xoay vòng OA của chính nó).</para>
///
/// <para><b>Dùng <c>message/cs</c>, KHÁC với bản tin sáng.</b> Bản tin dùng ZNS theo mẫu vì nó là
/// mình CHỦ ĐỘNG đẩy đi, lúc đó cửa sổ tư vấn luôn đóng. Chat thì ngược lại: khách vừa nhắn tới nên
/// cửa sổ vừa mở, và đây đúng là việc <c>message/cs</c> sinh ra để làm. Hai đường không mâu thuẫn —
/// đừng "sửa" cái này thành ZNS.</para>
///
/// <para><b>Nhiều OA dùng CHUNG một đường webhook</b> — Zalo Developer Console cho khai một URL
/// webhook RIÊNG cho mỗi App, nhưng không có gì cấm hai App khác nhau cùng trỏ về MỘT URL của
/// mình. Payload luôn có <c>app_id</c> nên adapter tự soát: đọc app_id trong thân tin (chưa tin,
/// mới là tuyên bố) → tìm tài khoản có đúng AppId đó → dùng CHÍNH secret của tài khoản đó để kiểm
/// chữ ký. Khớp cả hai bước mới coi là thật.</para>
///
/// <para>Tham khảo cách bóc sự kiện của ChatbotX (<c>integrations/zalo</c>): danh sách tên sự kiện
/// và công thức chữ ký lấy từ đó, phần còn lại viết lại cho khớp kiến trúc ở đây.</para>
/// </summary>
public class ZaloChatAdapter : IChatChannelAdapter
{
    private const string ApiBase = "https://openapi.zalo.me";
    private const string SendPath = "v3.0/oa/message/cs";
    private const string OAuthUrl = "https://oauth.zaloapp.com/v4/oa/access_token";

    /// Zalo trả mã này khi access token hết hạn.
    private const int MaTokenHetHan = -1001;

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly ILogger<ZaloChatAdapter> _log;

    public ZaloChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred, ILogger<ZaloChatAdapter> log)
    { _http = http; _cred = cred; _log = log; }

    public ChatChannel Channel => ChatChannel.Zalo;

    private record TaiKhoan(string AppId, string SecretKey, string? RefreshToken, string? AccessToken,
        DateTime? HetHanUtc)
    {
        public bool DuDeXacThuc => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(SecretKey);
        public bool DuDeGui => DuDeXacThuc && !string.IsNullOrWhiteSpace(RefreshToken);
    }

    private static TaiKhoan? Doc(IReadOnlyDictionary<string, string>? g) => g is null ? null : new TaiKhoan(
        g.GetValueOrDefault("appId", ""), g.GetValueOrDefault("secretKey", ""),
        g.GetValueOrDefault("refreshToken"), g.GetValueOrDefault("accessToken"),
        DateTime.TryParse(g.GetValueOrDefault("accessTokenExpiresUtc"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var h) ? h : null);

    // ── Xác thực webhook ────────────────────────────────────────────────────

    /// <summary>
    /// Chữ ký Zalo: <c>SHA256(appId + thânThô + timestamp + oaSecretKey)</c>, header dạng
    /// <c>mac=&lt;hash&gt;</c>.
    ///
    /// <para><b>Ký trên THÂN THÔ.</b> Serialize lại từ object đã parse chỉ đúng khi thứ tự khoá và
    /// khoảng trắng trùng khít bản gốc — gần như không bao giờ trùng. Đọc raw rồi ký thẳng.</para>
    /// </summary>
    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        string? appIdKhaiBao, timestamp;
        try
        {
            var goc = JsonNode.Parse(rawBody);
            appIdKhaiBao = goc?["app_id"]?.ToString();
            timestamp = goc?["timestamp"]?.ToString();
        }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(appIdKhaiBao) || string.IsNullOrWhiteSpace(timestamp)) return null;

        var header = headers["X-ZEvent-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null;
        var phan = header.Split('=', 2);
        if (phan.Length != 2) return null;

        var taiKhoan = await _cred.ListAccountsAsync(tenantId, Channel, ct);
        var khop = taiKhoan.FirstOrDefault(t => Doc(t.GiaTri)?.AppId == appIdKhaiBao);
        if (khop is null)
        {
            _log.LogWarning("[chat/zalo] tenant={T} nhận tin từ app_id {A} chưa khai tài khoản nào", tenantId, appIdKhaiBao);
            return null;
        }
        var cfg = Doc(khop.GiaTri)!;

        var noiDung = cfg.AppId + rawBody + timestamp + cfg.SecretKey;
        var mong = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noiDung))).ToLowerInvariant();

        var a = Encoding.ASCII.GetBytes(mong);
        var b = Encoding.ASCII.GetBytes(phan[1].Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? khop.AccountId : null;
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
                ra.Add(new(ChatChannel.Zalo, uid0!, null, ChatKind.Chu, null, null, luc,
                    Watermark: new(ChatState.DaXem, luc)));
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

    public async Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
    {
        var token = await LayAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        var (ok, thuLai, id, loi) = await GoiApiGuiChuAsync(token.Token!, externalUserId, text, ct);
        return await TraVeSauKhiGuiAsync(tenantId, accountId, ok, thuLai, id, loi,
            () => GoiApiGuiChuAsync(token.Token!, externalUserId, text, ct), ct);
    }

    /// <summary>
    /// Ảnh gửi bằng khung "media template" (elements[].media_type=image + url) — đây là kiểu DUY
    /// NHẤT Zalo OA cho gửi ảnh qua URL ngoài trong cửa sổ tư vấn. Tệp khác ảnh KHÔNG có đường
    /// tương đương chính thức, nên rơi về gửi một tin chữ kèm liên kết — thà nhân viên bấm vào
    /// link còn hơn báo "đã gửi" mà khách không thấy gì.
    /// </summary>
    public async Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        var token = await LayAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        if (loai != ChatKind.Anh)
        {
            // Không có API ảnh/tệp chính thức cho loại này → gửi bằng chữ, nói rõ đây là liên kết.
            var chu = string.IsNullOrWhiteSpace(caption) ? url : $"{caption}\n{url}";
            var (okT, thuLaiT, idT, loiT) = await GoiApiGuiChuAsync(token.Token!, externalUserId, chu, ct);
            return await TraVeSauKhiGuiAsync(tenantId, accountId, okT, thuLaiT, idT, loiT,
                () => GoiApiGuiChuAsync(token.Token!, externalUserId, chu, ct), ct);
        }

        var body = new
        {
            recipient = new { user_id = externalUserId },
            message = new
            {
                attachment = new
                {
                    type = "template",
                    payload = new { template_type = "media", elements = new[] { new { media_type = "image", url } } },
                },
            },
        };
        var (ok, thuLai, id, loi) = await GoiApiGuiThoAsync(token.Token!, body, ct);
        var kq = await TraVeSauKhiGuiAsync(tenantId, accountId, ok, thuLai, id, loi,
            () => GoiApiGuiThoAsync(token.Token!, body, ct), ct);

        // Ảnh không mang được chữ chú thích trong cùng một tin → gửi thêm một tin chữ nếu có.
        if (kq.Ok && !string.IsNullOrWhiteSpace(caption))
            await GoiApiGuiChuAsync(token.Token!, externalUserId, caption, ct);
        return kq;
    }

    /// <summary>Gửi thất bại vì hết hạn (-1001) → làm mới token MỘT LẦN rồi thử lại. Không quay
    /// vòng vô hạn: 1 lần làm mới là đủ, hỏng nữa thì đúng là hỏng thật.</summary>
    private async Task<SendResult> TraVeSauKhiGuiAsync(string tenantId, string accountId,
        bool ok, bool thuLai, string? id, string? loi,
        Func<Task<(bool ok, bool thuLai, string? id, string? loi)>> guiLai, CancellationToken ct)
    {
        if (ok || loi is null || !loi.Contains(MaTokenHetHan.ToString()))
            return new(ok, thuLai, id, loi);

        _log.LogInformation("[chat/zalo] token hết hạn ngoài dự kiến, làm mới rồi thử lại — tenant={T} acc={A}",
            tenantId, accountId);
        var moi = await LamMoiTokenAsync(tenantId, accountId, ct);
        if (moi.Loi is not null) return new(false, moi.ThuLai, null, moi.Loi);

        var (ok2, thuLai2, id2, loi2) = await guiLai();
        return new(ok2, thuLai2, id2, loi2);
    }

    private Task<(bool ok, bool thuLai, string? id, string? loi)> GoiApiGuiChuAsync(
        string token, string uid, string text, CancellationToken ct)
        => GoiApiGuiThoAsync(token, new { recipient = new { user_id = uid }, message = new { text } }, ct);

    private async Task<(bool ok, bool thuLai, string? id, string? loi)> GoiApiGuiThoAsync(
        string token, object body, CancellationToken ct)
    {
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

    // ── Access token: tự xoay vòng, KHÔNG chia sẻ với bất kỳ nơi nào khác ─────

    private record TokenResult(string? Token, bool ThuLai, string? Loi);

    /// <summary>Trả token dùng được ngay. Còn hạn (đệm 5 phút) → đọc cache; sắp/đã hết hạn → làm
    /// mới trước khi trả, để KHÔNG gửi hụt một lượt vì token chết đúng lúc.</summary>
    private async Task<TokenResult> LayAccessTokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var g = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        var cfg = Doc(g);
        if (cfg is null || !cfg.DuDeGui)
            return new(null, false, "Tài khoản Zalo OA này chưa khai đủ (thiếu App ID/Secret Key/Refresh Token)");

        if (!string.IsNullOrWhiteSpace(cfg.AccessToken)
            && cfg.HetHanUtc is { } h && h > DateTime.UtcNow.AddMinutes(5))
            return new(cfg.AccessToken, false, null);

        return await LamMoiTokenAsync(tenantId, accountId, ct);
    }

    /// <summary>
    /// Đổi refresh token lấy access token mới (Zalo OA v4). Zalo LUÔN trả kèm một refresh token
    /// MỚI trong response — phải lưu lại cái mới và bỏ cái cũ (token rotation): dùng lại token cũ
    /// ở lần sau sẽ bị Zalo từ chối.
    /// </summary>
    private async Task<TokenResult> LamMoiTokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var g = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        var cfg = Doc(g);
        if (cfg is null || !cfg.DuDeGui)
            return new(null, false, "Tài khoản Zalo OA này chưa khai đủ (thiếu App ID/Secret Key/Refresh Token)");

        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, OAuthUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["app_id"] = cfg.AppId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = cfg.RefreshToken!,
                }),
            };
            req.Headers.Add("secret_key", cfg.SecretKey);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                return new(null, (int)res.StatusCode >= 500, $"Làm mới token Zalo hỏng: HTTP {(int)res.StatusCode}");

            var o = JsonNode.Parse(raw)?.AsObject();
            var accessToken = o?["access_token"]?.ToString();
            var refreshMoi = o?["refresh_token"]?.ToString();
            var hetHanGiay = o?["expires_in"]?.ToString();
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshMoi))
            {
                _log.LogWarning("[chat/zalo] làm mới token hỏng, tenant={T} acc={A}: {Raw}", tenantId, accountId, Cat(raw));
                // refresh token có thể đã bị thu hồi (công ty đổi mật khẩu OA, rút quyền…) — thử
                // lại vô ích, phải khai lại từ đầu.
                return new(null, false, "Refresh token Zalo không còn dùng được — khai lại kết nối OA");
            }

            var giay = int.TryParse(hetHanGiay, out var s) ? s : 3600;
            await _cred.SaveAsync(tenantId, Channel, accountId, new Dictionary<string, string?>
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = refreshMoi,
                ["accessTokenExpiresUtc"] = DateTime.UtcNow.AddSeconds(giay).ToString("o"),
            }, ct);

            return new(accessToken, false, null);
        }
        catch (Exception ex)
        {
            return new(null, true, ex.Message);
        }
    }
}
