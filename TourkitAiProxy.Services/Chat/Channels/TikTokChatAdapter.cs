// Services/Chat/Channels/TikTokChatAdapter.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// TikTok Direct Message cho tài khoản doanh nghiệp.
///
/// <para>⚠️ <b>Bốn chỗ TikTok làm khác mọi kênh khác — mỗi chỗ là một kiểu hỏng riêng:</b></para>
/// <list type="number">
///   <item><b>Nội dung tin là một chuỗi JSON LỒNG trong JSON.</b> Trường <c>content</c> của gói
///     webhook là một <b>chuỗi</b> phải phân tích lần thứ hai mới ra tin. Đọc thẳng như một đối
///     tượng là luôn ra rỗng, mà không có lỗi nào — hộp thư chỉ đơn giản không có tin nào.</item>
///   <item><b>Chữ ký có HẠN 5 GIÂY.</b> Header <c>TikTok-Signature: t=&lt;giây&gt;,s=&lt;hex&gt;</c>,
///     ký trên chuỗi <c>"{t}.{thân thô}"</c>. TikTok khuyến nghị từ chối gói cũ hơn 5 giây — chống
///     phát lại. Máy chủ lệch giờ là mọi gói tin bị từ chối sạch mà log chỉ nói "chữ ký sai", nên
///     ở đây tách riêng hai lý do trong nhật ký.</item>
///   <item><b>Gửi tin theo mã HỘI THOẠI, không theo mã người.</b> Mọi kênh khác gửi tới id khách;
///     TikTok đòi <c>recipient_type=CONVERSATION</c> và <c>recipient</c> là mã hội thoại. Nên ở
///     kênh này <c>ExternalUserId</c> mang <b>mã hội thoại</b> — không phải mã người dùng. Lấy
///     nhầm là gửi ra lỗi mà nhìn dữ liệu thì thấy "có id đàng hoàng".</item>
///   <item><b>Ảnh phải TẢI LÊN trước rồi mới gửi được</b> (<c>media/upload</c> ra <c>media_id</c>);
///     TikTok không tự tải từ URL như bốn kênh kia.</item>
/// </list>
///
/// <para><b>Không có báo đã nhận / đã xem</b> — như Telegram, tin dừng ở "đã gửi" và đó là đúng.
/// Tiếng vọng <c>im_send_msg</c> thì có: nhân viên trả lời từ chính ứng dụng TikTok cũng vào hộp
/// thư.</para>
///
/// <para>⚠️ <b>Chưa kiểm bằng tài khoản thật</b> (27/08/2026). Cần một ứng dụng TikTok for Business
/// đã được duyệt quyền nhắn tin.</para>
/// </summary>
public class TikTokChatAdapter : IChatChannelAdapter
{
    private const string BusinessBase = "https://business-api.tiktok.com/open_api/v1.3";

    /// <summary>TikTok khuyến nghị bỏ gói cũ hơn 5 giây — chống phát lại.</summary>
    private static readonly TimeSpan SignatureMaxAge = TimeSpan.FromSeconds(5);

    /// <summary>Cho phép lệch đồng hồ hai chiều giữa máy chủ TikTok và máy mình.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TikTokChatAdapter> _log;

    public TikTokChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<TikTokChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.TikTok;

    private string? PlatformClientSecret => NullIfBlank(_cfg["Chat:TikTok:ClientSecret"]);

    // ── Nối bằng MỘT nút ────────────────────────────────────────────────────

    private const string OpenApiBase = "https://open.tiktokapis.com/v2";
    private const string AuthorizeUrl = "https://www.tiktok.com/v2/auth/authorize/";

    /// <summary>
    /// Quyền xin lúc cấp phép. Ba quyền <c>message.list.*</c> là phần bắt buộc; năm quyền
    /// <c>user.info.*</c> để lấy tên và ảnh tài khoản hiện lên danh sách kênh.
    ///
    /// <para>⚠️ Danh sách này phải TRÙNG với phần đã khai trong ứng dụng TikTok for Business.
    /// Xin một quyền chưa được duyệt thì TikTok từ chối CẢ lượt cấp phép chứ không bỏ qua riêng
    /// quyền đó — và câu lỗi của họ không nói quyền nào.</para>
    /// </summary>
    private const string Scopes =
        "user.info.basic,user.info.username,user.info.profile,user.info.stats,user.account.type,"
        + "message.list.read,message.list.send,message.list.manage";

    private string? PlatformClientId => NullIfBlank(_cfg["Chat:TikTok:ClientId"]);

    public bool HasPlatformApp => PlatformClientId is not null && PlatformClientSecret is not null;

    /// <summary>
    /// Đường mở hộp thoại cấp quyền của TikTok.
    ///
    /// <para><c>disable_auto_auth=1</c> buộc TikTok hỏi lại người dùng thay vì lặng lẽ dùng lại
    /// lượt cấp quyền cũ. Thiếu nó thì ĐỔI TÀI KHOẢN không được: bấm Kết nối là nối lại đúng tài
    /// khoản lần trước, không hiện màn hình chọn nào.</para>
    /// </summary>
    public string PermissionUrlFor(string redirectUri, string state)
        => $"{AuthorizeUrl}?client_key={U(PlatformClientId!)}&response_type=code"
         + $"&scope={U(Scopes)}&redirect_uri={U(redirectUri)}&disable_auto_auth=1&state={U(state)}";

    /// <summary>Kết quả nối một tài khoản TikTok.</summary>
    public record KetQuaNoi(string? AccountId, string? Ten, string? Loi);

    /// <summary>
    /// Đổi mã cấp quyền thành một tài khoản TikTok nối sẵn — <b>người dùng không nhập gì cả</b>.
    ///
    /// <para>⚠️ <b>Đổi mã đi qua Business API, KHÔNG phải Open API.</b> Hai bên có hai đường đổi
    /// mã khác nhau và chỉ đường Business mới cấp token dùng được cho nhắn tin. Gọi nhầm đường
    /// thì vẫn ra token hợp lệ, nối vẫn báo thành công, mà mọi lượt gửi tin sau đó đều bị từ chối.</para>
    ///
    /// <para>⚠️ Thân yêu cầu là <b>JSON</b> chứ không phải form — khác hầu hết OAuth khác.</para>
    /// </summary>
    public async Task<KetQuaNoi> ConnectFromCodeAsync(string tenantId, string code, string redirectUri,
        CancellationToken ct)
    {
        if (!HasPlatformApp) return new(null, null, "Máy chủ chưa khai ứng dụng TikTok (Chat:TikTok)");

        var tk = await DoiMaAsync(new JsonObject
        {
            ["client_id"] = PlatformClientId,
            ["client_secret"] = PlatformClientSecret,
            ["grant_type"] = "authorization_code",
            ["auth_code"] = code,
            ["redirect_uri"] = redirectUri,
        }, "tt_user/oauth2/token/", ct);
        if (tk.Loi is not null) return new(null, null, tk.Loi);

        // business_id CHÍNH LÀ open_id — không phải một mã riêng phải đi tìm trong bảng điều
        // khiển TikTok. Đây là lý do bốn ô khai tay trước đây đều thừa.
        var openId = tk.OpenId;
        if (string.IsNullOrWhiteSpace(openId))
            return new(null, null, "TikTok không trả về mã tài khoản (open_id).");

        var hoSo = await HoSoAsync(tk.AccessToken!, ct);
        var ten = hoSo?["display_name"]?.ToString() ?? hoSo?["username"]?.ToString();

        await LuuKhoaAsync(tenantId, openId!, tk, new Dictionary<string, string?>
        {
            ["businessId"] = openId,
            ["openId"] = openId,
            ["tiktokName"] = ten,
            ["avatarUrl"] = hoSo?["avatar_url"]?.ToString(),
            ["label"] = ten,
        }, ct);

        _log.LogInformation("[chat/tiktok] tenant={T} nối tài khoản {Ten} ({Id})", tenantId, ten, openId);
        return new(openId, ten, null);
    }

    /// <summary>
    /// Đảm bảo token còn hạn trước khi gọi API. <b>Gọi ở MỌI đường ra ngoài.</b>
    ///
    /// <para>Token TikTok sống 24 giờ. Không tự gia hạn thì đúng một ngày sau khi nối là mọi tin
    /// gửi đi đều hỏng — và triệu chứng ("TikTok từ chối") không hề gợi ra rằng nguyên nhân là
    /// hết hạn, nên chỗ này im lặng hỏng rất lâu.</para>
    ///
    /// <para>Gia hạn <b>sớm 30 phút</b>: gọi đúng lúc hết hạn thì lượt đang bay vẫn hỏng.</para>
    /// </summary>
    private async Task EnsureFreshTokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null) return;
        c.TryGetValue("refreshToken", out var refresh);
        if (string.IsNullOrWhiteSpace(refresh)) return;   // khai tay, không có gì để gia hạn

        c.TryGetValue("expiresAtUtc", out var hanChu);
        if (DateTime.TryParse(hanChu, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var han)
            && han - DateTime.UtcNow > TimeSpan.FromMinutes(30))
            return;

        if (!HasPlatformApp) return;
        var tk = await DoiMaAsync(new JsonObject
        {
            ["client_id"] = PlatformClientId,
            ["client_secret"] = PlatformClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        }, "tt_user/oauth2/refresh_token/", ct);

        if (tk.Loi is not null)
        {
            _log.LogWarning("[chat/tiktok] gia hạn token hỏng cho {Acc}: {Loi}", accountId, tk.Loi);
            return;   // để lượt gọi tới tự báo lỗi thật, đừng nuốt tin
        }
        await LuuKhoaAsync(tenantId, accountId, tk, new Dictionary<string, string?>(), ct);
        _log.LogInformation("[chat/tiktok] đã gia hạn token cho {Acc}", accountId);
    }

    private record KetQuaToken(string? AccessToken, string? RefreshToken, string? OpenId,
        int ExpiresInSeconds, string? Loi);

    /// <summary>
    /// Gọi một trong hai đường token của Business API. Cả hai trả cùng hình dạng
    /// <c>{code, message, data:{…}}</c> — và <c>code != 0</c> là HỎNG dù HTTP vẫn 200.
    /// </summary>
    private async Task<KetQuaToken> DoiMaAsync(JsonObject than, string duong, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var res = await http.PostAsync($"{BusinessBase}/{duong}",
                new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            var ma = o?["code"]?.ToString();
            if (ma is not null && ma != "0")
                return new(null, null, null, 0,
                    $"TikTok từ chối ({ma}): {o?["message"]?.ToString() ?? "không rõ lý do"}");

            var d = o?["data"];
            var token = d?["access_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(token))
                return new(null, null, null, 0, $"TikTok không trả về token: {Truncate(raw)}");

            var song = 0;
            if (d?["expires_in"] is { } e) int.TryParse(e.ToString(), out song);
            return new(token, d?["refresh_token"]?.ToString(), d?["open_id"]?.ToString(), song, null);
        }
        catch (Exception ex) { return new(null, null, null, 0, "Không gọi được TikTok: " + ex.Message); }
    }

    private async Task<JsonNode?> HoSoAsync(string token, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{OpenApiBase}/user/info/?fields=open_id,display_name,avatar_url,username");
            req.Headers.Add("Authorization", "Bearer " + token);
            using var res = await http.SendAsync(req, ct);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"]?["user"];
        }
        catch (Exception ex)
        {
            // Thiếu tên thì danh sách hiện mã tài khoản — xấu nhưng vẫn nhắn tin được. Không chặn.
            _log.LogWarning(ex, "[chat/tiktok] không đọc được hồ sơ tài khoản");
            return null;
        }
    }

    /// <summary>
    /// Ghi token kèm mốc hết hạn. Kho khoá GỘP theo từng khoá nên ô nào để rỗng thì giữ giá trị
    /// cũ — nhờ đó lượt gia hạn không xoá mất tên tài khoản và nhãn người dùng tự đặt.
    /// </summary>
    private Task LuuKhoaAsync(string tenantId, string accountId, KetQuaToken tk,
        Dictionary<string, string?> them, CancellationToken ct)
    {
        var g = new Dictionary<string, string?>(them)
        {
            ["accessToken"] = tk.AccessToken,
            ["refreshToken"] = tk.RefreshToken,
            ["expiresAtUtc"] = DateTime.UtcNow
                .AddSeconds(tk.ExpiresInSeconds > 0 ? tk.ExpiresInSeconds : 3600)
                .ToString("O", CultureInfo.InvariantCulture),
        };
        return _cred.SaveAsync(tenantId, Channel, accountId, g, ct);
    }

    private static string U(string s) => Uri.EscapeDataString(s);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary><c>user_openid</c> của gói tin — khoá định tuyến ra công ty.</summary>
    public static string? OpenIdOfEvent(string rawBody)
    {
        try { return JsonNode.Parse(rawBody)?["user_openid"]?.ToString(); }
        catch { return null; }
    }

    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        var ky = headers["TikTok-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ky)) return null;

        var openId = OpenIdOfEvent(rawBody);
        var dsach = await _cred.ListAccountsAsync(tenantId, Channel, ct);

        foreach (var tk in dsach)
        {
            var khop = accountIdTuUrl is { Length: > 0 }
                ? string.Equals(tk.AccountId, accountIdTuUrl, StringComparison.OrdinalIgnoreCase)
                : openId is { Length: > 0 } && tk.GiaTri.GetValueOrDefault("openId", "") == openId;
            if (!khop) continue;

            var bimat = NullIfBlank(tk.GiaTri.GetValueOrDefault("clientSecret", "")) ?? PlatformClientSecret;
            if (bimat is null) continue;

            var (dung, viSao) = CheckSignature(bimat, rawBody, ky!, DateTimeOffset.UtcNow);
            if (dung) return tk.AccountId;

            // Tách rõ "quá hạn" khỏi "ký sai": máy chủ lệch giờ làm MỌI gói bị từ chối, mà nếu chỉ
            // ghi "chữ ký sai" thì người tìm lỗi sẽ đi soi khoá bí mật suốt buổi.
            _log.LogWarning("[chat/tiktok] từ chối gói của {Ig} tenant {T}: {ViSao}", openId, tenantId, viSao);
            return null;
        }

        _log.LogWarning("[chat/tiktok] không tài khoản nào khớp openid {Ig} của {T} — bỏ gói tin",
            openId, tenantId);
        return null;
    }

    /// <summary>
    /// Đường webhook DÙNG CHUNG: tra ra công ty rồi kiểm chữ ký.
    ///
    /// <para>Webhook đăng ký theo ỨNG DỤNG nên URL không mang tên công ty được. Khoá định tuyến
    /// là openid trong thân tin — nhưng tra ra công ty <b>KHÔNG</b> chứng minh tin là thật,
    /// nên vẫn phải kiểm chữ ký bằng khoá của chính tài khoản đó.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> ResolveSharedWebhookAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (OpenIdOfEvent(rawBody) is not { } khoa) return null;
        var tenant = await _cred.FindTenantAsync(Channel, khoa, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/tiktok] nhận tin của {Khoa} nhưng chưa công ty nào nối", khoa);
            return null;
        }
        return await VerifyAsync(tenant, khoa, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
    }

    /// <summary>Kiểm chữ ký TikTok. Hàm thuần (trừ tham số thời điểm) để test được cả nhánh quá hạn.</summary>
    internal static (bool Dung, string ViSao) CheckSignature(string clientSecret, string rawBody,
        string header, DateTimeOffset bayGio)
    {
        string? t = null, chuKy = null;
        foreach (var phan in header.Split(','))
        {
            var p = phan.Trim();
            if (p.StartsWith("t=", StringComparison.Ordinal)) t = p[2..];
            else if (p.StartsWith("s=", StringComparison.Ordinal)) chuKy = p[2..];
        }
        if (t is null || chuKy is null) return (false, "header không đúng dạng t=…,s=…");

        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var giay))
            return (false, "mốc thời gian không phải số");

        var lech = bayGio - DateTimeOffset.FromUnixTimeSeconds(giay);
        if (lech > SignatureMaxAge || lech < -ClockSkew)
            return (false, $"gói quá hạn {lech.TotalSeconds:F0}s — kiểm đồng hồ máy chủ trước khi nghi khoá");

        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(clientSecret));
        var tinh = Convert.ToHexString(
            h.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{rawBody}"))).ToLowerInvariant();
        var a = Encoding.UTF8.GetBytes(tinh);
        var b = Encoding.UTF8.GetBytes(chuKy.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b)
            ? (true, "") : (false, "chữ ký không khớp");
    }

    // ── Bóc gói tin ─────────────────────────────────────────────────────────

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }
        if (goc is null) return ra;

        var loaiSuKien = goc["event"]?.ToString();
        // im_receive_msg = khách nhắn tới; im_send_msg = tiếng vọng tin mình gửi (kể cả gửi từ
        // chính ứng dụng TikTok). Bỏ tiếng vọng là hộp thư thiếu nửa cuộc trò chuyện VÀ bot nói
        // đè lên người thật — cùng bài học với Zalo.
        var vong = loaiSuKien == "im_send_msg";
        if (loaiSuKien != "im_receive_msg" && !vong) return ra;

        // ⚠️ content là một CHUỖI JSON, phải phân tích lần hai. Đọc thẳng như đối tượng thì luôn
        // rỗng và không có lỗi nào để lần ra.
        JsonNode? noi;
        try { noi = JsonNode.Parse(goc["content"]?.GetValue<string>() ?? ""); }
        catch
        {
            _log.LogWarning("[chat/tiktok] không đọc được trường content của gói {SuKien}", loaiSuKien);
            return ra;
        }
        if (noi is null) return ra;

        // Mã HỘI THOẠI là thứ dùng để gửi lại — xem docstring lớp.
        var maHoiThoai = noi["conversation_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(maHoiThoai)) return ra;

        var luc = long.TryParse(goc["create_time"]?.ToString(), out var giay)
            ? DateTimeOffset.FromUnixTimeSeconds(giay).UtcDateTime : DateTime.UtcNow;

        var kieu = noi["type"]?.ToString();
        var loai = kieu == "image" ? ChatKind.Image : ChatKind.Text;
        var chu = noi["text"]?["body"]?.ToString();
        var att = kieu == "image" && noi["media_url"] is not null
            ? new JsonObject { ["media_url"] = noi["media_url"]!.ToString() }.ToJsonString()
            : null;

        if (att is null && string.IsNullOrWhiteSpace(chu))
        {
            _log.LogWarning("[chat/tiktok] loại tin chưa hỗ trợ ({Kieu}), bỏ qua", kieu);
            return ra;
        }

        // Tên khách: ở tiếng vọng thì khách là NGƯỜI NHẬN, không phải người gửi. Lấy nhầm đầu là
        // hội thoại mang tên chính mình.
        var ben = vong ? noi["to_user"] : noi["from_user"];

        ra.Add(new(Channel, maHoiThoai!, noi["message_id"]?.ToString(), loai, chu, att, luc,
            IsEcho: vong, DisplayName: ben?["nickname"]?.ToString() ?? ben?["name"]?.ToString()));
        return ra;
    }

    // ── Gửi ─────────────────────────────────────────────────────────────────

    private async Task<(string? Token, string? BusinessId)> KhoaAsync(string tenantId, string accountId,
        CancellationToken ct)
    {
        // Gia hạn TRƯỚC khi đọc khoá. Mọi đường gửi ra ngoài đều qua đây, nên đặt ở đây là
        // không đường nào lọt. Token TikTok sống 24 giờ; thiếu bước này thì đúng một ngày sau
        // khi nối là kênh im lặng hỏng.
        await EnsureFreshTokenAsync(tenantId, accountId, ct);
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null) return (null, null);
        c.TryGetValue("accessToken", out var token);
        c.TryGetValue("businessId", out var bid);
        return (NullIfBlank(token), NullIfBlank(bid));
    }

    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => GuiAsync(tenantId, accountId, externalUserId, than =>
        {
            than["message_type"] = "TEXT";
            than["text"] = new JsonObject { ["body"] = text };
        }, ct);

    /// <summary>
    /// Gửi ảnh. <b>TikTok không tự tải từ URL</b> — phải tải tệp lên trước để lấy <c>media_id</c>,
    /// khác cả bốn kênh kia. Và chỉ nhận ẢNH: tệp, âm thanh, video đều không gửi được qua đường này.
    /// </summary>
    public async Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        if (loai != ChatKind.Image)
            return new(false, false, null,
                "TikTok chỉ gửi được ảnh qua tin nhắn. Gửi đường dẫn tệp bằng tin chữ thay thế.");

        var maTep = await UploadAsync(tenantId, accountId, url, ct);
        if (maTep is null) return new(false, true, null, "Không tải được ảnh lên TikTok");

        var kq = await GuiAsync(tenantId, accountId, externalUserId, than =>
        {
            than["message_type"] = "IMAGE";
            than["image"] = new JsonObject { ["media_id"] = maTep };
        }, ct);

        // Chú thích phải đi thành tin riêng — TikTok không gộp chữ vào tin ảnh.
        if (kq.Ok && !string.IsNullOrWhiteSpace(caption))
            await SendTextAsync(tenantId, accountId, externalUserId, caption!, ct);
        return kq;
    }

    private async Task<string?> UploadAsync(string tenantId, string accountId, string url,
        CancellationToken ct)
    {
        var (token, businessId) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null || businessId is null) return null;
        try
        {
            var http = _http.CreateClient();
            var anh = await http.GetByteArrayAsync(url, ct);

            using var form = new MultipartFormDataContent
            {
                { new StringContent(businessId), "business_id" },
                { new StringContent("IMAGE"), "media_type" },
                { new ByteArrayContent(anh), "file", "anh.jpg" },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{BusinessBase}/business/message/media/upload/") { Content = form };
            // ⚠️ TikTok dùng header "Access-Token", KHÔNG phải "Authorization: Bearer".
            req.Headers.Add("Access-Token", token);

            using var res = await http.SendAsync(req, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            return o?["data"]?["media_id"]?.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/tiktok] tải ảnh lên hỏng");
            return null;
        }
    }

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, string maHoiThoai,
        Action<JsonObject> dungNoiDung, CancellationToken ct)
    {
        var (token, businessId) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null || businessId is null)
            return new(false, false, null, "Chưa khai tài khoản TikTok cho công ty này");

        var than = new JsonObject
        {
            ["business_id"] = businessId,
            // recipient là mã HỘI THOẠI — xem docstring lớp.
            ["recipient_type"] = "CONVERSATION",
            ["recipient"] = maHoiThoai,
        };
        dungNoiDung(than);

        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{BusinessBase}/business/message/send/")
            {
                Content = new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Access-Token", token);

            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            // ⚠️ TikTok trả HTTP 200 KỂ CẢ KHI HỎNG; lỗi nằm ở trường "code" trong thân. Chỉ nhìn
            // mã HTTP là báo "đã gửi" cho những tin không bao giờ tới.
            var ma = o?["code"]?.ToString();
            if (res.IsSuccessStatusCode && (ma is null || ma == "0"))
                return new(true, false, o?["data"]?["message_id"]?.ToString(), null);

            var moTa = o?["message"]?.ToString() ?? Truncate(raw);
            return new(false, (int)res.StatusCode >= 500, null, $"TikTok từ chối ({ma}): {moTa}");
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
