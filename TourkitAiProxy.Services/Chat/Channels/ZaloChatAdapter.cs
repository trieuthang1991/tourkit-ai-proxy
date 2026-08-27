// Services/Chat/Channels/ZaloChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

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
public class ZaloChatAdapter : IChatChannelAdapter, IApprovedTemplateSender, IButtonSender
{
    private const string ApiBase = "https://openapi.zalo.me";
    private const string SendPath = "v3.0/oa/message/cs";
    private const string OAuthUrl = "https://oauth.zaloapp.com/v4/oa/access_token";

    /// Zalo trả mã này khi access token hết hạn.
    private const int TokenExpiredCode = -1001;

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly ILogger<ZaloChatAdapter> _log;

    private readonly IConfiguration _cfg;

    public ZaloChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<ZaloChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    // ── Ứng dụng Zalo CẤP NỀN TẢNG ──────────────────────────────────────────
    //
    // Trước đây mỗi công ty phải tự tạo một ứng dụng trên developers.zalo.me: tự tìm App ID, hai
    // loại khoá bí mật, tự khai callback và webhook. Tám bước kỹ thuật trước khi nhắn được tin đầu
    // tiên — không công ty du lịch nào làm nổi, và không sửa được bằng cách viết hướng dẫn hay hơn.
    //
    // Nay TourKit sở hữu MỘT ứng dụng cho mọi khách hàng, khai một lần ở appsettings. Khách chỉ
    // bấm "Kết nối Zalo OA" rồi đồng ý trong cửa sổ Zalo.
    //
    // Khoá RIÊNG của tài khoản vẫn được ưu tiên nếu có — công ty nào đã khai ứng dụng riêng theo
    // đường cũ thì vẫn chạy nguyên, không phải khai lại.
    public string? PlatformAppId => NullIfBlank(_cfg["Chat:Zalo:AppId"]);
    private string? PlatformAppSecret => NullIfBlank(_cfg["Chat:Zalo:AppSecretKey"]);
    private string? PlatformOaSecret => NullIfBlank(_cfg["Chat:Zalo:OaSecretKey"]);

    /// Đã khai đủ ứng dụng cấp nền tảng chưa. Thiếu thì giao diện phải hiện lại các ô nhập tay.
    public bool HasPlatformApp => PlatformAppId is not null && PlatformAppSecret is not null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public ChatChannel Channel => ChatChannel.Zalo;

    /// <param name="SecretKey"><b>App Secret Key</b> — dùng ở header <c>secret_key</c> khi đổi token.</param>
    /// <param name="OaSecretKey"><b>OA Secret Key</b> — dùng KIỂM CHỮ KÝ webhook. Zalo cấp hai khoá
    /// khác nhau; dùng nhầm là một trong hai chiều hỏng mà thông báo lỗi không nói ra điều đó.</param>
    private record AccountRow(string AppId, string SecretKey, string? OaSecretKey,
        string? RefreshToken, string? AccessToken,
        DateTime? HetHanUtc)
    {
        public bool CanVerify => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(SecretKey);
        public bool CanSend => CanVerify && !string.IsNullOrWhiteSpace(RefreshToken);
    }

    /// <summary>
    /// Đọc khoá của một tài khoản, <b>lùi về ứng dụng cấp nền tảng</b> ở từng ô một.
    ///
    /// <para>Lùi theo TỪNG Ô chứ không phải cả cụm: tài khoản nối bằng luồng mới chỉ có
    /// <c>refreshToken</c> + <c>oaId</c>, ba ô khoá ứng dụng lấy từ cấu hình; còn tài khoản khai
    /// tay theo đường cũ có đủ cả ba và phải được giữ nguyên.</para>
    /// </summary>
    private AccountRow? Read(IReadOnlyDictionary<string, string>? g) => g is null ? null : new AccountRow(
        NullIfBlank(g.GetValueOrDefault("appId")) ?? PlatformAppId ?? "",
        NullIfBlank(g.GetValueOrDefault("secretKey")) ?? PlatformAppSecret ?? "",
        NullIfBlank(g.GetValueOrDefault("oaSecretKey")) ?? PlatformOaSecret,
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
    /// <summary>
    /// Id của OA nhận/gửi sự kiện này — <b>khoá định tuyến</b> của webhook dùng chung.
    ///
    /// <para>Zalo không đặt id OA vào một chỗ cố định: sự kiện gắn nhãn có <c>oa_id</c> riêng, tin
    /// khách gửi thì OA là NGƯỜI NHẬN, còn tiếng vọng OA gửi thì OA là NGƯỜI GỬI. Lấy nhầm đầu là
    /// tra ra id của KHÁCH rồi không khớp công ty nào, tin rơi vào hư không mà chỉ có một dòng log.</para>
    /// </summary>
    public static string? OaIdOfEvent(string rawBody)
    {
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return null; }
        if (goc?["oa_id"]?.ToString() is { Length: > 0 } thang) return thang;

        var ten = goc?["event_name"]?.ToString() ?? "";
        var laOaGui = ten.StartsWith("oa_send", StringComparison.OrdinalIgnoreCase);
        var id = laOaGui ? goc?["sender"]?["id"]?.ToString() : goc?["recipient"]?["id"]?.ToString();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Xác thực cho đường webhook <b>DÙNG CHUNG</b> (không mang tên công ty trên URL).
    ///
    /// <para>Dùng ứng dụng cấp nền tảng thì <c>app_id</c> giống hệt nhau ở mọi khách hàng, nên nó
    /// không còn phân biệt được ai với ai. Định tuyến bằng <b>id OA</b>: id đó chính là
    /// <c>accountId</c> của tài khoản nối theo luồng mới, nên tra ra công ty chỉ mất một phép so.</para>
    ///
    /// <para>Tra được công ty rồi <b>vẫn phải kiểm chữ ký</b> bằng khoá của chính tài khoản đó.
    /// Bỏ bước này thì ai biết đường dẫn cũng bơm tin giả vào hộp thư, chỉ cần đoán một id OA —
    /// mà id OA không phải bí mật.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> ResolveSharedWebhookAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (OaIdOfEvent(rawBody) is not { } oaId) return null;
        var tenant = await _cred.FindTenantAsync(Channel, oaId, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/zalo] nhận tin của OA {Oa} nhưng chưa công ty nào nối OA đó", oaId);
            return null;
        }
        return await VerifyAsync(tenant, oaId, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
    }

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

        // Có accountId trên URL (hoặc do tra theo id OA) thì dùng ĐÚNG tài khoản đó. Dò theo
        // app_id chỉ còn đúng ở đường cũ, nơi mỗi công ty một ứng dụng riêng; với ứng dụng cấp
        // nền tảng thì app_id giống nhau ở mọi khách nên dò theo nó là khớp bừa.
        var taiKhoan = await _cred.ListAccountsAsync(tenantId, Channel, ct);
        var khop = accountIdTuUrl is { Length: > 0 }
            ? taiKhoan.FirstOrDefault(t => t.AccountId == accountIdTuUrl)
            : taiKhoan.FirstOrDefault(t => Read(t.GiaTri)?.AppId == appIdKhaiBao);
        if (khop is null)
        {
            _log.LogWarning("[chat/zalo] tenant={T} nhận tin từ app_id {A} chưa khai tài khoản nào", tenantId, appIdKhaiBao);
            return null;
        }
        var cfg = Read(khop.GiaTri)!;

        // OA Secret Key, KHÔNG phải App Secret Key — hai khoá khác nhau trên cổng Zalo. Chưa khai
        // ô mới thì lùi về ô cũ: cấu hình đang chạy không được gãy chỉ vì thêm một ô.
        var khoaKy = string.IsNullOrWhiteSpace(cfg.OaSecretKey) ? cfg.SecretKey : cfg.OaSecretKey!;
        var noiDung = cfg.AppId + rawBody + timestamp + khoaKy;
        var mong = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noiDung))).ToLowerInvariant();

        var a = Encoding.ASCII.GetBytes(mong);
        var b = Encoding.ASCII.GetBytes(phan[1].Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? khop.AccountId : null;
    }

    // ── Bóc sự kiện ─────────────────────────────────────────────────────────

    /// Tin do KHÁCH gửi tới.
    private static readonly Dictionary<string, ChatKind> CustomerKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_send_text"] = ChatKind.Text,
        ["user_send_image"] = ChatKind.Image,
        ["user_send_file"] = ChatKind.File,
        ["user_send_audio"] = ChatKind.Audio,
        ["user_send_sticker"] = ChatKind.Sticker,
        ["user_send_location"] = ChatKind.Location,
    };

    /// <summary>
    /// Tin do CHÍNH OA gửi — tiếng vọng.
    ///
    /// <para><b>Đừng bỏ nhóm này.</b> Nhân viên trả lời từ app Zalo OA (không qua TourKit) thì mình
    /// chỉ biết qua đây. Bỏ qua thì hộp thư thiếu mất nửa cuộc trò chuyện, VÀ bot nói đè lên người
    /// thật vì không biết có ai đang trả lời.</para>
    /// </summary>
    private static readonly Dictionary<string, ChatKind> OaKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oa_send_text"] = ChatKind.Text,
        // Zalo dùng cả tên chung "oa_send_msg" cho một số loại tin nhân viên gửi từ app OA. Thiếu
        // nó thì hộp thư mất một phần cuộc trò chuyện mà không có dấu hiệu gì.
        ["oa_send_msg"] = ChatKind.Text,
        ["oa_send_image"] = ChatKind.Image,
        ["oa_send_file"] = ChatKind.File,
        ["oa_send_video"] = ChatKind.File,
        ["oa_send_sticker"] = ChatKind.Sticker,
        ["oa_send_gif"] = ChatKind.Image,
        ["oa_send_link"] = ChatKind.Text,
        ["oa_send_list"] = ChatKind.Text,
        ["oa_send_carousel"] = ChatKind.Text,
        // CỐ Ý KHÔNG có "oa_send_action": đó là báo thao tác (đang gõ, đã xem…), KHÔNG phải tin và
        // không có msg_id. Nhận nó vào đây là sinh ra tin rỗng trong hộp thư mỗi lần nhân viên gõ
        // phím bên app Zalo. (ChatbotX có nó trong danh sách sự kiện nhưng cũng không dựng tin từ
        // nó — nhánh switch của họ không xử lý, rơi vào chỗ đòi msg_id rồi ném.)
    };

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }
        if (goc?["event_name"]?.ToString() is not { Length: > 0 } ten) return ra;

        var luc = TimestampOf(goc["timestamp"]?.ToString());

        // Khách đã xem tin — không phải tin nhắn, nhưng là tín hiệu thật cho nhân viên.
        if (ten.Equals("user_seen_message", StringComparison.OrdinalIgnoreCase))
        {
            var uid0 = goc["sender"]?["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(uid0))
                ra.Add(new(ChatChannel.Zalo, uid0!, null, ChatKind.Text, null, null, luc,
                    Watermark: new(ChatState.Seen, luc)));
            return ra;
        }

        var laKhach = CustomerKinds.TryGetValue(ten, out var loaiKhach);
        var laOa = !laKhach && OaKinds.TryGetValue(ten, out var loaiOa);
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
            laKhach ? loaiKhach : OaKinds[ten],
            text, att, luc,
            IsEcho: laOa,
            DisplayName: goc["sender"]?["name"]?.ToString()));
        return ra;
    }

    private static DateTime TimestampOf(string? ms)
        => long.TryParse(ms, out var v)
            ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime
            : DateTime.UtcNow;

    // ── Nút bấm ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Gửi chữ kèm nút bằng khung <c>template</c> của Zalo OA.
    ///
    /// <para>Zalo gộp cả hai kiểu vào một cơ chế như Telegram, nhưng tên trường thì khác hẳn
    /// Meta: <c>type</c> là <c>oa.open.url</c> (mở trang) hoặc <c>oa.query.show</c> (gửi lại chữ
    /// về). Chép hình dạng của Meta sang là Zalo nhận tin rồi bỏ sạch nút, không báo lỗi.</para>
    ///
    /// <para>⚠️ Khung này chỉ chạy trong <b>cửa sổ tư vấn</b>. Hết cửa sổ thì phải đi bằng tin
    /// mẫu ZNS, và ZNS có kiểu nút riêng của nó — không dùng lại được chỗ này.</para>
    /// </summary>
    public async Task<SendResult> SendTextWithButtonsAsync(string tenantId, string accountId,
        string externalUserId, string text, IReadOnlyList<ChatButton> nut, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        var dsNut = new JsonArray();
        foreach (var b in nut)
            dsNut.Add(new JsonObject
            {
                ["title"] = b.Label,
                ["type"] = b.IsLink ? "oa.open.url" : "oa.query.show",
                ["payload"] = b.IsLink
                    ? new JsonObject { ["url"] = b.Url }
                    // Với nút trả lời nhanh, payload CHÍNH LÀ chữ trên nút — khách bấm là coi
                    // như họ nói câu đó, rồi trợ lý xử như mọi câu khác.
                    : new JsonObject { ["content"] = b.Label },
            });

        var than = new JsonObject
        {
            ["recipient"] = new JsonObject { ["user_id"] = externalUserId },
            ["message"] = new JsonObject
            {
                ["text"] = text,
                ["attachment"] = new JsonObject
                {
                    ["type"] = "template",
                    ["payload"] = new JsonObject
                    {
                        ["template_type"] = "button",
                        ["buttons"] = dsNut,
                    },
                },
            },
        };

        var (ok, thuLai, id, loi) = await GoiApiGuiThoAsync(token.Token!, than, ct);
        return await AfterSendAsync(tenantId, accountId, ok, thuLai, id, loi,
            () => GoiApiGuiThoAsync(token.Token!, than, ct), ct);
    }

    // ── Mẫu tin đã duyệt (ZNS) ──────────────────────────────────────────────

    /// <summary>
    /// ZNS đi qua tên miền RIÊNG, không phải <c>openapi.zalo.me</c> như tin tư vấn. Gọi nhầm
    /// tên miền thì Zalo trả 404 chứ không nói là sai đường.
    ///
    /// <para><b>⚠️ Đường ZNS này của RIÊNG hộp thư chat, cố ý tách khỏi cụm bản tin.</b> Bản
    /// tin sáng cũng gửi ZNS, nhưng đi hẳn lối khác: xếp vào <c>dbo.OutboundMails</c> (SQL
    /// Server) rồi worker của <c>toutkit-app</c> mới rút ra gửi. Chat KHÔNG dùng lối đó và
    /// đừng gộp lại:</para>
    /// <list type="number">
    ///   <item>Chat cần <b>mã tin trả về ngay</b> để gắn vào đúng hội thoại và theo dõi trạng
    ///     thái. Qua hàng đợi của repo khác thì mã đó không bao giờ quay lại được.</item>
    ///   <item>Nhân viên bấm gửi và <b>chờ kết quả trên màn hình</b>. Hàng đợi chạy theo nhịp
    ///     riêng, có khi vài phút.</item>
    ///   <item>Hai kho dữ liệu tách hẳn: chat ở PostgreSQL, hàng đợi bản tin ở SQL Server.</item>
    /// </list>
    /// <para>Token dùng ở đây là token OA của <b>chính kênh chat</b> (kho khoá riêng, tự gia
    /// hạn), không phải cấu hình OA của bản tin.</para>
    /// </summary>
    private const string ZnsBase = "https://business.openapi.zalo.me";

    /// <inheritdoc/>
    /// <remarks>
    /// Hai lượt gọi: danh sách mẫu chỉ có tên và mã, phải hỏi tiếp từng mẫu mới ra danh sách ô
    /// điền. Chặn ở 50 mẫu — công ty du lịch thường dùng dưới mười, và mỗi mẫu là một lượt gọi.
    /// </remarks>
    public async Task<IReadOnlyList<ChatTemplate>> ListTemplatesAsync(string tenantId,
        string accountId, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(tenantId, accountId, ct);
        if (token.Token is null) return Array.Empty<ChatTemplate>();

        var http = _http.CreateClient();
        var ds = await ZnsJsonAsync(http, token.Token,
            $"{ZnsBase}/template/all?offset=0&limit=50&status=1", ct);
        if (ds?["data"] is not JsonArray mangMau) return Array.Empty<ChatTemplate>();

        var ra = new List<ChatTemplate>();
        foreach (var mau in mangMau.OfType<JsonNode>())
        {
            var ma = mau["templateId"]?.ToString() ?? mau["template_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(ma)) continue;

            var ten = mau["templateName"]?.ToString() ?? mau["template_name"]?.ToString() ?? ma!;
            var tt = (mau["status"]?.ToString() ?? "").ToUpperInvariant();

            var chiTiet = await ZnsJsonAsync(http, token.Token,
                $"{ZnsBase}/template/info?template_id={Uri.EscapeDataString(ma!)}", ct);
            var slots = new List<ChatTemplateSlot>();
            if (chiTiet?["data"]?["listParams"] is JsonArray ps)
                foreach (var x in ps.OfType<JsonNode>())
                {
                    var khoa = x["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(khoa)) continue;
                    // Zalo dùng TÊN tự đặt (ma_don, ngay_khoi_hanh), không phải số thứ tự như
                    // Meta — nên tên khoá vừa là khoá vừa là nhãn hiện cho nhân viên.
                    slots.Add(new(khoa!, khoa!, x["sample_value"]?.ToString()));
                }

            // Zalo trả status kiểu "ENABLE"; quy về đúng một từ mà cả ba kênh cùng hiểu.
            ra.Add(new(ma!, ten, "vi", mau["templateQuality"]?.ToString(),
                tt is "ENABLE" or "1" ? "APPROVED" : tt,
                slots, chiTiet?["data"]?["previewUrl"]?.ToString()));
        }
        return ra;
    }

    /// <summary>
    /// ⚠️ <b>ZNS gửi theo SỐ ĐIỆN THOẠI, không theo id người dùng Zalo.</b> Đây là khác biệt
    /// lớn nhất giữa ZNS và tin tư vấn, và là lý do một hội thoại Zalo đang mở vẫn có thể KHÔNG
    /// gửi ZNS được: mình biết khách là ai trên OA, nhưng không biết số của họ.
    ///
    /// <para>Kiểm ở đây, trước khi bày danh sách mẫu ra — để nhân viên chọn mẫu, điền năm ô rồi
    /// mới báo thiếu số là bắt họ làm công cốc.</para>
    /// </summary>
    public TemplateBlocked? WhyBlocked(ChatContact? khach)
        => string.IsNullOrWhiteSpace(khach?.Phone)
            ? new("Zalo gửi tin mẫu theo SỐ ĐIỆN THOẠI, mà hội thoại này chưa có số của khách. "
                  + "Hỏi số rồi điền vào hồ sơ khách bên phải, hoặc gọi điện.")
            : null;

    /// <inheritdoc/>
    public async Task<SendResult> SendTemplateAsync(string tenantId, string accountId,
        string externalUserId, ChatContact? khach, ChatTemplate mau,
        IReadOnlyDictionary<string, string> giaTri, CancellationToken ct)
    {
        if (WhyBlocked(khach) is { } chan) return new(false, false, null, chan.Reason);

        var token = await GetAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        var duLieu = new JsonObject();
        foreach (var slot in mau.Slots)
            duLieu[slot.Key] = giaTri.GetValueOrDefault(slot.Key, "");

        var than = new JsonObject
        {
            ["phone"] = ChuanHoaSo(khach!.Phone!),
            ["template_id"] = mau.Id,
            ["template_data"] = duLieu,
        };

        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ZnsBase}/message/template")
            {
                Content = new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("access_token", token.Token);

            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            // ⚠️ Zalo trả HTTP 200 kể cả khi hỏng; lỗi nằm ở trường "error" (0 = xong). Chỉ nhìn
            // mã HTTP là báo "đã gửi" cho những tin không bao giờ tới.
            var ma = o?["error"]?.ToString();
            if (res.IsSuccessStatusCode && ma == "0")
                return new(true, false, o?["data"]?["msg_id"]?.ToString(), null);

            var moTa = o?["message"]?.ToString() ?? Truncate(raw);
            return new(false, (int)res.StatusCode >= 500, null, $"Zalo từ chối tin mẫu ({ma}): {moTa}");
        }
        catch (Exception ex) { return new(false, true, null, ex.Message); }
    }

    /// <summary>
    /// ZNS đòi số dạng <c>84…</c>. Số trong CRM thường lưu <c>0…</c>, đôi khi có dấu cách hoặc
    /// dấu chấm — gửi nguyên si là Zalo từ chối với câu lỗi không hề nhắc tới định dạng.
    /// </summary>
    internal static string ChuanHoaSo(string so)
    {
        var s = new string(so.Where(char.IsDigit).ToArray());
        if (s.StartsWith("84", StringComparison.Ordinal)) return s;
        if (s.StartsWith('0')) return "84" + s[1..];
        return s;
    }

    /// <summary>Gọi một đường ZNS trả JSON. Không ném — chỗ gọi tự đọc lỗi trong thân.</summary>
    private async Task<JsonNode?> ZnsJsonAsync(HttpClient http, string token, string url,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("access_token", token);
            using var res = await http.SendAsync(req, ct);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/zalo] gọi ZNS hỏng: {Url}", url);
            return null;
        }
    }

    // ── Gửi ─────────────────────────────────────────────────────────────────

    public async Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        var (ok, thuLai, id, loi) = await GoiApiGuiChuAsync(token.Token!, externalUserId, text, ct);
        return await AfterSendAsync(tenantId, accountId, ok, thuLai, id, loi,
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
        var token = await GetAccessTokenAsync(tenantId, accountId, ct);
        if (token.Loi is not null) return new(false, token.ThuLai, null, token.Loi);

        if (loai != ChatKind.Image)
        {
            // Không có API ảnh/tệp chính thức cho loại này → gửi bằng chữ, nói rõ đây là liên kết.
            var chu = string.IsNullOrWhiteSpace(caption) ? url : $"{caption}\n{url}";
            var (okT, thuLaiT, idT, loiT) = await GoiApiGuiChuAsync(token.Token!, externalUserId, chu, ct);
            return await AfterSendAsync(tenantId, accountId, okT, thuLaiT, idT, loiT,
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
        var kq = await AfterSendAsync(tenantId, accountId, ok, thuLai, id, loi,
            () => GoiApiGuiThoAsync(token.Token!, body, ct), ct);

        // Ảnh không mang được chữ chú thích trong cùng một tin → gửi thêm một tin chữ nếu có.
        if (kq.Ok && !string.IsNullOrWhiteSpace(caption))
            await GoiApiGuiChuAsync(token.Token!, externalUserId, caption, ct);
        return kq;
    }

    /// <summary>Gửi thất bại vì hết hạn (-1001) → làm mới token MỘT LẦN rồi thử lại. Không quay
    /// vòng vô hạn: 1 lần làm mới là đủ, hỏng nữa thì đúng là hỏng thật.</summary>
    private async Task<SendResult> AfterSendAsync(string tenantId, string accountId,
        bool ok, bool thuLai, string? id, string? loi,
        Func<Task<(bool ok, bool thuLai, string? id, string? loi)>> guiLai, CancellationToken ct)
    {
        if (ok || loi is null || !loi.Contains(TokenExpiredCode.ToString()))
            return new(ok, thuLai, id, loi);

        _log.LogInformation("[chat/zalo] token hết hạn ngoài dự kiến, làm mới rồi thử lại — tenant={T} acc={A}",
            tenantId, accountId);
        var moi = await RefreshTokenAsync(tenantId, accountId, ct);
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
                return (false, (int)res.StatusCode >= 500, null, $"HTTP {(int)res.StatusCode}: {Truncate(raw)}");

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

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    // ── Access token: tự xoay vòng, KHÔNG chia sẻ với bất kỳ nơi nào khác ─────

    private record TokenResult(string? Token, bool ThuLai, string? Loi);

    /// Đường Zalo hỏi quản trị viên OA có đồng ý cho ứng dụng này không.
    private const string PermissionUrl = "https://oauth.zaloapp.com/v4/oa/permission";

    /// <summary>
    /// Dựng đường cấp quyền để mở trong trình duyệt.
    ///
    /// <para><paramref name="redirectUri"/> phải khai Y HỆT ở ô <b>Official Account Callback URL</b>
    /// bên cổng Zalo — lệch một dấu gạch chéo là Zalo từ chối, và câu lỗi của họ không nói lệch ở
    /// đâu.</para>
    /// </summary>
    public static string PermissionUrlFor(string appId, string redirectUri, string state)
        => $"{PermissionUrl}?app_id={Uri.EscapeDataString(appId)}"
         + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
         + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// Đổi <c>code</c> Zalo vừa đá về lấy access token + refresh token, rồi LƯU LUÔN.
    ///
    /// <para>Trả <c>null</c> khi xong; trả câu lỗi tiếng Việt khi hỏng — câu này hiện thẳng lên
    /// trang callback nên phải đọc được, không phải mã lỗi của Zalo.</para>
    ///
    /// <para>⚠️ <c>code</c> sống rất ngắn và <b>dùng một lần</b>. Bấm đi bấm lại đường cấp quyền
    /// rồi đổi mã cũ là hỏng — phải bấm lại từ đầu.</para>
    /// </summary>
    public async Task<string?> ExchangePermissionCodeAsync(string tenantId, string accountId, string ma,
        string redirectUri, CancellationToken ct)
    {
        // accountId rỗng = luồng MỚI: chưa có tài khoản nào, khoá lấy từ ứng dụng cấp nền tảng và
        // mã tài khoản sẽ là chính id OA — biết được sau khi hỏi hồ sơ.
        var moiToanh = string.IsNullOrWhiteSpace(accountId);
        var cfg = moiToanh
            ? Read(new Dictionary<string, string>())
            : Read(await _cred.GetAsync(tenantId, Channel, accountId, ct));
        if (cfg is null || !cfg.CanVerify)
            return moiToanh
                ? "Máy chủ chưa khai ứng dụng Zalo dùng chung (Chat:Zalo)"
                : "Tài khoản Zalo OA này chưa khai App ID và App Secret Key";

        var than = new Dictionary<string, string>
        {
            ["app_id"] = cfg.AppId,
            ["grant_type"] = "authorization_code",
            ["code"] = ma,
            ["redirect_uri"] = redirectUri,
        };

        // Luồng CŨ: đã biết tài khoản nào, đổi xong lưu thẳng.
        if (!moiToanh)
        {
            var kqCu = await CallOAuthAsync(cfg, than, tenantId, accountId, ct);
            if (kqCu.Loi is not null) return kqCu.Loi;
            await SaveOaProfileAsync(tenantId, accountId, kqCu.Token!, ct);
            return null;
        }

        // Luồng MỚI: đổi mã TRƯỚC, chưa lưu — chưa biết lưu vào đâu.
        var kq = await ExchangeTokenAsync(cfg, than, ct);
        if (kq.Loi is not null) return kq.Loi;

        // Hỏi Zalo vừa nối OA nào. Ở luồng này KHÔNG được nuốt lỗi như luồng cũ: thiếu id OA thì
        // không có mã tài khoản để lưu, mà webhook dùng chung cũng tra ngược bằng chính id đó —
        // lưu bừa một mã ngẫu nhiên là tin của khách không bao giờ tới được công ty này.
        var hoSo = await OaProfileAsync(kq.Token!, ct);
        if (hoSo is null)
            return "Đã lấy được quyền nhưng Zalo không trả hồ sơ OA — thử kết nối lại";

        await _cred.SaveAsync(tenantId, Channel, hoSo.Value.OaId, new Dictionary<string, string?>
        {
            ["oaId"] = hoSo.Value.OaId,
            ["oaName"] = hoSo.Value.Name,
            // Tên gợi nhớ mặc định là tên OA. Người dùng sửa lại được, và chỉ khi họ muốn.
            ["label"] = hoSo.Value.Name,
            ["accessToken"] = kq.AccessToken,
            ["refreshToken"] = kq.RefreshToken,
            ["accessTokenExpiresUtc"] = DateTime.UtcNow.AddSeconds(kq.ExpiresInSeconds).ToString("o"),
        }, ct);

        _log.LogInformation("[chat/zalo] tenant={T} vừa nối OA {Oa} ({Ten})",
            tenantId, hoSo.Value.OaId, hoSo.Value.Name);
        return null;
    }

    /// <summary>Hồ sơ OA của access token này. Trả null khi Zalo không cho biết.</summary>
    private async Task<(string OaId, string? Name)?> OaProfileAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v2.0/oa/getoa");
            req.Headers.Add("access_token", accessToken);
            using var res = await http.SendAsync(req, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();

            // Zalo trả HTTP 200 kèm error != 0 khi hỏng — không đọc trường đó là tưởng thành công.
            if (o?["error"]?.GetValue<int>() is not 0) return null;
            var oaId = o["data"]?["oa_id"]?.ToString();
            return string.IsNullOrWhiteSpace(oaId) ? null : (oaId!, o["data"]?["name"]?.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/zalo] không lấy được hồ sơ OA");
            return null;
        }
    }


    /// <summary>Lấy <c>oa_id</c> + tên OA rồi lưu vào cấu hình tài khoản. Nuốt mọi lỗi.</summary>
    private async Task SaveOaProfileAsync(string tenantId, string accountId, string accessToken,
        CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v2.0/oa/getoa");
            req.Headers.Add("access_token", accessToken);
            using var res = await http.SendAsync(req, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();

            // Zalo trả HTTP 200 kèm error != 0 khi hỏng — không đọc trường đó là tưởng thành công.
            if (o?["error"]?.GetValue<int>() is not 0) return;
            var d = o["data"];
            await _cred.SaveAsync(tenantId, Channel, accountId, new Dictionary<string, string?>
            {
                ["oaId"] = d?["oa_id"]?.ToString(),
                ["oaName"] = d?["name"]?.ToString(),
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/zalo] cấp quyền xong nhưng không lấy được hồ sơ OA");
        }
    }

    /// <summary>Trả token dùng được ngay. Còn hạn (đệm 5 phút) → đọc cache; sắp/đã hết hạn → làm
    /// mới trước khi trả, để KHÔNG gửi hụt một lượt vì token chết đúng lúc.</summary>
    private async Task<TokenResult> GetAccessTokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var g = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        var cfg = Read(g);
        if (cfg is null || !cfg.CanSend)
            return new(null, false, "Tài khoản Zalo OA này chưa khai đủ (thiếu App ID/Secret Key/Refresh Token)");

        if (!string.IsNullOrWhiteSpace(cfg.AccessToken)
            && cfg.HetHanUtc is { } h && h > DateTime.UtcNow.AddMinutes(5))
            return new(cfg.AccessToken, false, null);

        return await RefreshTokenAsync(tenantId, accountId, ct);
    }

    /// <summary>
    /// Đổi refresh token lấy access token mới (Zalo OA v4). Zalo LUÔN trả kèm một refresh token
    /// MỚI trong response — phải lưu lại cái mới và bỏ cái cũ (token rotation): dùng lại token cũ
    /// ở lần sau sẽ bị Zalo từ chối.
    /// </summary>
    private Task<TokenResult> RefreshTokenAsync(string tenantId, string accountId, CancellationToken ct)
        => RefreshTokenAsync(tenantId, accountId, null, ct);

    private async Task<TokenResult> RefreshTokenAsync(string tenantId, string accountId,
        AccountRow? daDoc, CancellationToken ct)
    {
        var cfg = daDoc ?? Read(await _cred.GetAsync(tenantId, Channel, accountId, ct));
        if (cfg is null || !cfg.CanSend)
            return new(null, false, "Tài khoản Zalo OA này chưa khai đủ (thiếu App ID/App Secret Key/Refresh Token)");

        return await CallOAuthAsync(cfg, new Dictionary<string, string>
        {
            ["app_id"] = cfg.AppId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = cfg.RefreshToken!,
        }, tenantId, accountId, ct);
    }
    /// <summary>
    /// Gọi <c>/v4/oa/access_token</c> và <b>KHÔNG lưu gì</b> — chỉ trả về những gì Zalo nói.
    ///
    /// <para>Tách khỏi phần lưu vì luồng kết nối mới chưa biết lưu vào đâu: mã tài khoản là id OA,
    /// mà id đó phải hỏi thêm một lượt nữa mới biết.</para>
    ///
    /// <para>Header <c>secret_key</c> là <b>App Secret Key</b>, không phải OA Secret Key.</para>
    /// </summary>
    private async Task<TokenExchange> ExchangeTokenAsync(AccountRow cfg, Dictionary<string, string> than,
        CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, OAuthUrl)
            {
                Content = new FormUrlEncodedContent(than),
            };
            req.Headers.Add("secret_key", cfg.SecretKey);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                return new(null, null, 0, (int)res.StatusCode >= 500,
                    $"Zalo trả HTTP {(int)res.StatusCode} khi đổi token");

            var o = JsonNode.Parse(raw)?.AsObject();
            var accessToken = o?["access_token"]?.ToString();
            var refreshMoi = o?["refresh_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshMoi))
            {
                _log.LogWarning("[chat/zalo] đổi token hỏng: {Raw}", Truncate(raw));
                // Zalo trả 200 kèm error trong thân khi mã sai/hết hạn hoặc quyền bị thu hồi —
                // thử lại vô ích, phải cấp quyền lại từ đầu.
                var loi = o?["error_name"]?.ToString() ?? o?["message"]?.ToString();
                return new(null, null, 0, false, string.IsNullOrWhiteSpace(loi)
                    ? "Zalo không cấp token — mã cấp quyền đã hết hạn hoặc quyền bị thu hồi, hãy cấp quyền lại"
                    : $"Zalo từ chối: {Truncate(loi!)}");
            }

            var giay = int.TryParse(o?["expires_in"]?.ToString(), out var gi) ? gi : 3600;
            return new(accessToken, refreshMoi, giay, false, null);
        }
        catch (Exception ex)
        {
            return new(null, null, 0, true, ex.Message);
        }
    }

    /// <param name="ExpiresInSeconds">Access token sống được bao lâu, theo lời Zalo.</param>
    private record TokenExchange(string? AccessToken, string? RefreshToken, int ExpiresInSeconds,
        bool ThuLai, string? Loi)
    {
        public string? Token => AccessToken;
    }

    /// <summary>
    /// Đổi token rồi LƯU cho một tài khoản đã biết. Dùng chung cho cả hai lượt: đổi <c>code</c> lần
    /// đầu (đường cũ) và làm mới bằng <c>refresh_token</c> về sau.
    /// </summary>
    private async Task<TokenResult> CallOAuthAsync(AccountRow cfg, Dictionary<string, string> than,
        string tenantId, string accountId, CancellationToken ct)
    {
        var kq = await ExchangeTokenAsync(cfg, than, ct);
        if (kq.Loi is not null) return new(null, kq.ThuLai, kq.Loi);

        // Zalo LUÔN trả refresh token MỚI (token rotation) — phải lưu cái mới và bỏ cái cũ, dùng
        // lại cái cũ ở lần sau sẽ bị từ chối.
        await _cred.SaveAsync(tenantId, Channel, accountId, new Dictionary<string, string?>
        {
            ["accessToken"] = kq.AccessToken,
            ["refreshToken"] = kq.RefreshToken,
            ["accessTokenExpiresUtc"] = DateTime.UtcNow.AddSeconds(kq.ExpiresInSeconds).ToString("o"),
        }, ct);

        return new(kq.AccessToken, false, null);
    }
}
