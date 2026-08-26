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
    private const string GraphBase = "https://graph.facebook.com";

    /// Đổi phiên bản là đổi hành vi của MỌI lệnh gọi Meta cùng lúc — để ở cấu hình, mặc định giữ
    /// nguyên bản đang chạy thật, đừng nhảy phiên bản chỉ vì Meta ra bản mới.
    private const string MacDinhPhienBan = "v21.0";

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MessengerChatAdapter> _log;

    public MessengerChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<MessengerChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.Messenger;

    // ── Ứng dụng Meta CẤP NỀN TẢNG ──────────────────────────────────────────
    //
    // Giống hệt lối đã làm cho Zalo: TourKit sở hữu MỘT ứng dụng Facebook cho mọi khách hàng, khai
    // một lần ở appsettings. Khách bấm một nút, đăng nhập Facebook, chọn Trang — hết.
    //
    // Facebook còn dễ hơn Zalo một bậc: bước đăng ký nhận tin cho Trang gọi được bằng API
    // (<c>subscribed_apps</c>), nên khách KHÔNG phải vào màn hình quản trị của Meta lần nào. Zalo
    // không có cái tương đương.
    //
    // Khoá RIÊNG của tài khoản vẫn được ưu tiên: công ty nào đã khai ứng dụng riêng theo đường cũ
    // thì chạy nguyên, không phải khai lại.
    public string? AppIdNenTang => Rong(_cfg["Chat:Messenger:AppId"]);
    private string? AppSecretNenTang => Rong(_cfg["Chat:Messenger:AppSecret"]);
    private string? VerifyTokenNenTang => Rong(_cfg["Chat:Messenger:VerifyToken"]);
    public string PhienBan => Rong(_cfg["Chat:Messenger:Version"]) ?? MacDinhPhienBan;

    /// Mã cấu hình của Facebook Login for Business. Để trống = dùng luồng cổ điển.
    private string? CauHinhNenTang => Rong(_cfg["Chat:Messenger:ConfigId"]);

    /// Đã khai đủ ứng dụng cấp nền tảng chưa. Thiếu thì giao diện phải hiện lại các ô nhập tay.
    public bool CoUngDungNenTang => AppIdNenTang is not null && AppSecretNenTang is not null;

    private static string? Rong(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// App Secret của một tài khoản, <b>lùi về ứng dụng cấp nền tảng</b>. Tài khoản nối bằng luồng
    /// mới chỉ lưu <c>pageId</c> + <c>pageAccessToken</c>; khoá ứng dụng lấy từ cấu hình.
    private string? AppSecretCua(IReadOnlyDictionary<string, string> g)
        => Rong(g.GetValueOrDefault("appSecret")) ?? AppSecretNenTang;

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

        // Ứng dụng cấp nền tảng: chỉ có MỘT verify token, khai ở appsettings. Kiểm trước vì đường
        // dùng chung không mang tên công ty nên danh sách tài khoản bên dưới sẽ rỗng.
        if (VerifyTokenNenTang is { } chung && chung == token) return challenge;

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

        // Bước 1: TÌM TRANG. Meta đăng ký webhook theo ỨNG DỤNG chứ không theo Trang, nên một gói
        // tin tới đây chưa tự nói nó thuộc tài khoản nào — id Trang nằm trong thân tin (hoặc do
        // đường dùng chung tra sẵn ra rồi truyền vào).
        var pageId = accountIdTuUrl is { Length: > 0 } ? accountIdTuUrl : IdTrangCuaSuKien(rawBody);
        if (pageId is null) return null;

        // accountId của luồng mới CHÍNH LÀ id Trang; luồng khai tay cũ để id ngẫu nhiên và cất id
        // Trang trong ô pageId — thử cả hai, không thì tài khoản khai tay ngừng nhận tin.
        var taiKhoanKhop = taiKhoan.FirstOrDefault(t => t.AccountId == pageId)
                        ?? taiKhoan.FirstOrDefault(t => t.GiaTri.GetValueOrDefault("pageId") == pageId);
        if (taiKhoanKhop is null)
        {
            _log.LogWarning("[chat/messenger] tenant={T} nhận tin từ Trang {P} chưa khai tài khoản nào", tenantId, pageId);
            return null;
        }

        // Bước 2: KIỂM CHỮ KÝ bằng đúng khoá của tài khoản vừa tìm ra.
        //
        // Trước đây bước này thử LẦN LƯỢT mọi appSecret đã khai rồi mới đi tìm Trang. Đổi thứ tự
        // vì với ứng dụng cấp nền tảng thì mọi công ty chung một App Secret — "khớp một cái bất
        // kỳ" không còn chứng minh được gì. Tìm Trang trước rồi kiểm bằng khoá của chính nó thì
        // chặt hơn, và luồng khai tay cũ vẫn đúng y như cũ.
        var secret = AppSecretCua(taiKhoanKhop.GiaTri);
        if (secret is null)
        {
            _log.LogWarning("[chat/messenger] tenant={T} Trang {P} chưa khai App Secret và máy chủ "
                + "cũng chưa khai ứng dụng dùng chung — không kiểm được chữ ký", tenantId, pageId);
            return null;
        }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mong = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var a = Encoding.ASCII.GetBytes(mong);
        return a.Length == chuKyGui.Length && CryptographicOperations.FixedTimeEquals(a, chuKyGui)
            ? taiKhoanKhop.AccountId : null;
    }

    /// <summary>
    /// Id Trang của gói tin này — <b>khoá định tuyến</b> của webhook dùng chung.
    ///
    /// <para>Meta để nó ở <c>entry[].id</c> cho MỌI loại sự kiện (tin khách, tiếng vọng, báo đã
    /// đọc, đổi nhãn). Dễ hơn Zalo, nơi id OA nhảy chỗ theo từng loại sự kiện.</para>
    /// </summary>
    public static string? IdTrangCuaSuKien(string rawBody)
    {
        try
        {
            var goc = JsonNode.Parse(rawBody);
            var id = (goc?["entry"] as JsonArray)?.OfType<JsonNode>().FirstOrDefault()?["id"]?.ToString();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch { return null; }
    }

    /// <summary>
    /// Xác thực cho đường webhook <b>DÙNG CHUNG</b> (không mang tên công ty trên URL).
    ///
    /// <para>Dùng ứng dụng cấp nền tảng thì mọi khách hàng chung một App, nên URL không thể mang
    /// tên công ty. Định tuyến bằng <b>id Trang</b>: id đó chính là <c>accountId</c> của tài khoản
    /// nối theo luồng mới, nên tra ra công ty chỉ mất một phép so.</para>
    ///
    /// <para>Tra được công ty rồi <b>vẫn phải kiểm chữ ký</b> — id Trang không phải bí mật, ai vào
    /// trang Facebook công khai cũng đọc được.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> XacMinhDungChungAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (IdTrangCuaSuKien(rawBody) is not { } pageId) return null;
        var tenant = await _cred.TimTenantAsync(Channel, pageId, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/messenger] nhận tin của Trang {P} nhưng chưa công ty nào nối Trang đó", pageId);
            return null;
        }
        return await VerifyAsync(tenant, pageId, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
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

                // Cảm xúc: KHÔNG phải tin mới, mà gắn vào một tin đã có.
                //
                //   {"sender":{"id":<khách>},"recipient":{"id":<Trang>},
                //    "reaction":{"mid":<tin bị thả>,"action":"react"|"unreact",
                //                "emoji":"❤","reaction":"love"}}
                //
                // ⚠️ "unreact" là GỠ cảm xúc, và lúc đó Meta KHÔNG gửi kèm emoji. Xử lý chung một
                // nhánh với "react" mà không đọc action là cảm xúc đã gỡ vẫn hiện mãi.
                if (m["reaction"] is { } cx)
                {
                    var uidCx = m["sender"]?["id"]?.ToString();
                    var midCx = cx["mid"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidCx) && !string.IsNullOrWhiteSpace(midCx))
                    {
                        var lucCx = long.TryParse(m["timestamp"]?.ToString(), out var tsCx)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(tsCx).UtcDateTime : DateTime.UtcNow;
                        ra.Add(new(ChatChannel.Messenger, uidCx!, null, ChatKind.Chu, null, null, lucCx,
                            Reaction: new(midCx!, cx["emoji"]?.ToString(), cx["reaction"]?.ToString(),
                                cx["action"]?.ToString() == "unreact")));
                    }
                    continue;
                }

                // Nguồn khách đến. Meta gắn nó vào BA chỗ khác nhau tuỳ đường khách vào:
                //   messaging_referrals -> m.referral        (khách đã từng nhắn, quay lại qua QR/liên kết)
                //   messaging_postbacks -> m.postback.referral (lần ĐẦU bấm "Bắt đầu" từ quảng cáo)
                //   messaging_optins    -> m.optin.ref
                // Chỉ đọc một chỗ là mất phần lớn ca — mà mất là mất vĩnh viễn, không tra lại được.
                var nguon = m["referral"] ?? m["postback"]?["referral"];
                ChatReferral? tuDau = nguon is null ? null : new(
                    nguon["source"]?.ToString(), nguon["ref"]?.ToString(), nguon["ad_id"]?.ToString());

                // Khách bấm NÚT. Ghi lại bằng CHỮ TRÊN NÚT (title) chứ không phải payload kỹ
                // thuật: nhân viên đọc lại hội thoại phải thấy đúng thứ khách nhìn thấy, không
                // phải một chuỗi mã như "MENU_TOUR_DA_NANG".
                if (m["postback"] is { } pb)
                {
                    var uidPb = m["sender"]?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidPb))
                    {
                        var lucPb = long.TryParse(m["timestamp"]?.ToString(), out var tsPb)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(tsPb).UtcDateTime : DateTime.UtcNow;
                        ra.Add(new(ChatChannel.Messenger, uidPb!, pb["mid"]?.ToString(), ChatKind.Chu,
                            pb["title"]?.ToString() ?? pb["payload"]?.ToString(), null, lucPb,
                            Referral: tuDau));
                    }
                    continue;
                }

                // Gói CHỈ CÓ nguồn, không kèm tin (khách mở cuộc trò chuyện từ quảng cáo nhưng
                // chưa gõ gì). Vẫn phải ghi nhận — đây chính là lúc duy nhất Meta nói nguồn.
                if (m["message"] is null && tuDau is not null)
                {
                    var uidRf = m["sender"]?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidRf))
                        ra.Add(new(ChatChannel.Messenger, uidRf!, null, ChatKind.Chu, null, null,
                            DateTime.UtcNow, Referral: tuDau));
                    continue;
                }

                var msg = m["message"];
                if (msg is null) continue;   // opt-in… — chưa dùng

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
                    msg["text"]?.ToString(), att, luc, IsEcho: vong, Referral: tuDau));
            }
        }
        return ra;
    }

    /// <summary>
    /// Tên + ảnh của khách. Gói tin webhook của Meta <b>chỉ có mã người dùng</b>, khác Zalo và
    /// Telegram vốn kèm sẵn tên — nên riêng kênh này phải hỏi thêm một lượt.
    ///
    /// <para>Mã đó (PSID) <b>riêng cho từng Trang</b>: cùng một người nhắn hai Trang là hai mã
    /// khác nhau. Nên phải hỏi bằng token của ĐÚNG Trang đã nhận tin.</para>
    ///
    /// <para>Nuốt mọi lỗi và trả <c>null</c>: không có tên thì hộp thư hiện mã người dùng, xấu
    /// nhưng vẫn dùng được. Ném ở đây là chặn cả tin của khách chỉ vì không lấy được cái tên.</para>
    /// </summary>
    public async Task<HoSoKhach?> HoSoKhachAsync(string tenantId, string accountId,
        string externalUserId, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null || !c.TryGetValue("pageAccessToken", out var token) || string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var http = _http.CreateClient();
            using var res = await http.GetAsync($"{GraphBase}/{PhienBan}/{U(externalUserId)}"
                + $"?fields=first_name,last_name,profile_pic&access_token={U(token)}", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();
            if (o is null || o["error"] is not null)
            {
                _log.LogWarning("[chat/messenger] không lấy được hồ sơ khách {Id}: {Loi}",
                    externalUserId, Cat(raw));
                return null;
            }

            var ten = string.Join(" ", new[] { o["first_name"]?.ToString(), o["last_name"]?.ToString() }
                .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            var anh = o["profile_pic"]?.ToString();
            return string.IsNullOrWhiteSpace(ten) && string.IsNullOrWhiteSpace(anh)
                ? null : new HoSoKhach(Rong(ten), Rong(anh));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/messenger] hỏi hồ sơ khách {Id} hỏng", externalUserId);
            return null;
        }
    }

    // ── Nối Trang bằng MỘT nút (OAuth) ──────────────────────────────────────

    /// <summary>
    /// Quyền xin của người dùng, giữ ở mức <b>tối thiểu chạy được</b>:
    /// <list type="bullet">
    /// <item><c>pages_show_list</c> — để đọc <c>/me/accounts</c>, tức là biết họ quản trị Trang nào.</item>
    /// <item><c>pages_messaging</c> — để gửi tin.</item>
    /// <item><c>pages_manage_metadata</c> — để gọi <c>subscribed_apps</c>, tức là TỰ bật nhận tin.</item>
    /// </list>
    ///
    /// <para>⚠️ <b>Đã BỎ <c>pages_read_engagement</c> ngày 26/08/2026</b> dù tài liệu Messenger của
    /// Meta có liệt kê nó: app thật trả về <c>Invalid Scopes: pages_read_engagement</c> rồi bỏ qua.
    /// Mình không cần — tên Trang lấy từ <c>/me/accounts</c> bằng <c>pages_show_list</c>. Thêm lại
    /// chỉ khi khâu duyệt của Meta đòi, và phải khai quyền đó trong ứng dụng trước.</para>
    ///
    /// <para><b>Vì sao không xin thêm cho chắc.</b> Mỗi quyền thừa là một mục phải giải trình khi
    /// Meta duyệt ứng dụng, và một dòng đáng ngờ trong màn hình khách bấm đồng ý. Cần thêm về sau
    /// thì xin thêm — khách cấp quyền lại mất mười giây.</para>
    /// </summary>
    public static readonly string[] Quyen =
    {
        "public_profile",
        "pages_show_list",
        "pages_messaging",
        "pages_manage_metadata",

        // Thêm 26/08/2026 SAU KHI GẶP THẬT. Ban đầu cố tình bỏ cho nhẹ khâu duyệt, và trả giá:
        // Facebook cấp pages_show_list bình thường nhưng /me/accounts trả về RỖNG, không báo lỗi
        // gì. Tài liệu Messenger của Meta ghi business_management là PHỤ THUỘC của
        // pages_show_list và pages_messaging — Trang do một Danh mục doanh nghiệp sở hữu thì
        // thiếu quyền này là không liệt kê ra được. Đừng bỏ lại.
        "business_management",
    };

    /// <summary>
    /// Loại sự kiện đăng ký cho Trang. <b>Đây là thứ quyết định webhook có tin hay không</b> — cấp
    /// quyền xong mà quên bước này thì mọi thứ trông như đã nối mà hộp thư im lặng mãi mãi.
    ///
    /// <para>Phải khớp với những gì <see cref="Parse"/> bóc: thiếu <c>message_echoes</c> là mất tin
    /// nhân viên trả lời từ ứng dụng Meta; thiếu <c>message_deliveries</c>/<c>message_reads</c> là
    /// tin gửi đi không bao giờ leo lên hai tích.</para>
    /// </summary>
    private static readonly string[] SuKienTrang =
    {
        "messages", "messaging_postbacks", "messaging_optins", "messaging_referrals",
        "message_deliveries", "message_reads", "message_echoes",
    };

    private static string U(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// Đường đăng nhập Facebook để xin quyền.
    ///
    /// <para><paramref name="redirectUri"/> phải nằm trong danh sách <b>Valid OAuth Redirect URIs</b>
    /// của ứng dụng bên Meta — lệch một dấu gạch chéo là Facebook từ chối, y như Zalo.</para>
    /// </summary>
    public string DuongCapQuyen(string redirectUri, string state)
    {
        var d = $"https://www.facebook.com/{PhienBan}/dialog/oauth"
              + $"?client_id={U(AppIdNenTang ?? "")}"
              + $"&redirect_uri={U(redirectUri)}"
              + $"&scope={U(string.Join(",", Quyen))}"
              + "&response_type=code"

              // BẮT Facebook hỏi lại từ đầu, kể cả khi tài khoản này đã đồng ý lần trước.
              //
              // Mặc định Facebook NHỚ lựa chọn cũ và bỏ qua màn hình đồng ý — nên khi mình xin thêm
              // một quyền mới (đã dính thật với business_management), người dùng bấm kết nối lại vẫn
              // không bao giờ được hỏi, và cứ hỏng y như cũ mà không hiểu vì sao. Cách duy nhất chữa
              // bằng tay là vào Facebook gỡ ứng dụng ra — không khách hàng nào làm nổi việc đó.
              + "&auth_type=rerequest"

              + $"&state={U(state)}";

        // Facebook Login for Business làm việc theo "cấu hình" (configuration): quyền và TÀI SẢN
        // (Trang nào) khai sẵn trong bảng điều khiển, rồi truyền config_id để màn hình đồng ý hiện
        // đúng bước chọn Trang. Không có nó thì luồng cổ điển vẫn cấp quyền nhưng có thể KHÔNG kèm
        // tài sản nào — quyền đủ mà danh sách Trang rỗng.
        //
        // Để TRỐNG là giữ nguyên luồng cổ điển. Chỉ khai khi đã tạo cấu hình bên Meta.
        return CauHinhNenTang is { } ch ? d + $"&config_id={U(ch)}" : d;
    }

    /// <summary>
    /// Đổi <c>code</c> Facebook vừa đá về lấy <b>danh sách Trang</b> người này quản trị, mỗi Trang
    /// kèm access token riêng của nó.
    ///
    /// <para><b>Vì sao chưa nối luôn.</b> Zalo hỏi ra đúng một OA; Meta trả về mọi Trang người đó
    /// quản trị, kể cả Trang cá nhân chẳng liên quan. Phải để họ chọn.</para>
    /// </summary>
    public async Task<(IReadOnlyList<TrangUngVien>? Trang, string? Loi)> DoiMaLayTrangAsync(
        string ma, string redirectUri, CancellationToken ct)
    {
        if (AppIdNenTang is null || AppSecretNenTang is null)
            return (null, "Máy chủ chưa khai ứng dụng Facebook dùng chung (Chat:Messenger)");

        try
        {
            var http = _http.CreateClient();

            // 1. code → user token NGẮN hạn (khoảng 1-2 giờ).
            var ngan = await ChuoiAsync(http, $"{GraphBase}/{PhienBan}/oauth/access_token"
                + $"?client_id={U(AppIdNenTang)}&client_secret={U(AppSecretNenTang)}"
                + $"&redirect_uri={U(redirectUri)}&code={U(ma)}", "access_token", ct);
            if (ngan.Loi is not null) return (null, ngan.Loi);

            // 2. Đổi sang user token DÀI hạn TRƯỚC khi hỏi danh sách Trang.
            //
            // Thứ tự này quan trọng và rất dễ làm ngược: page token lấy ra từ user token NGẮN hạn
            // cũng chỉ sống vài giờ, còn page token lấy ra từ user token DÀI hạn thì KHÔNG HẾT HẠN.
            // Làm ngược là vài giờ sau cả hộp thư ngừng gửi được, mà lỗi Meta trả về chỉ nói
            // "session expired" — không ai đoán ra nguyên nhân nằm ở thứ tự hai lệnh gọi này.
            var dai = await ChuoiAsync(http, $"{GraphBase}/{PhienBan}/oauth/access_token"
                + $"?grant_type=fb_exchange_token&client_id={U(AppIdNenTang)}"
                + $"&client_secret={U(AppSecretNenTang)}&fb_exchange_token={U(ngan.Gia!)}", "access_token", ct);
            if (dai.Loi is not null) return (null, dai.Loi);

            // 3. Danh sách Trang, mỗi Trang một token riêng.
            using var res = await http.GetAsync($"{GraphBase}/{PhienBan}/me/accounts"
                + $"?fields=id,name,access_token&limit=100&access_token={U(dai.Gia!)}", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();
            if (o?["error"] is { } loi)
                return (null, $"Facebook từ chối: {loi["message"]?.ToString() ?? Cat(raw)}");

            var ds = (o?["data"] as JsonArray ?? new JsonArray())
                .OfType<JsonNode>()
                .Select(x => new TrangUngVien(x["id"]?.ToString() ?? "", x["name"]?.ToString() ?? "",
                    x["access_token"]?.ToString() ?? ""))
                .Where(x => x.PageId.Length > 0 && x.AccessToken.Length > 0)
                .ToList();

            // Rỗng thì HỎI THẲNG Facebook đã cấp quyền gì. Câu lỗi cũ đoán bừa là "tài khoản này
            // không quản trị Trang nào" rồi đổ lỗi cho người dùng — trong khi nguyên nhân thật hay
            // gặp hơn nhiều là họ ĐÚNG là quản trị viên, nhưng bước chọn Trang trên màn hình đồng ý
            // không hiện hoặc không chọn gì, nên Facebook không kèm Trang nào. Hai ca đó sửa ở hai
            // chỗ khác hẳn nhau; đoán nhầm là người dùng đi đăng nhập lại bằng tài khoản khác cho
            // tới lúc bỏ cuộc.
            return ds.Count == 0 ? (null, await ViSaoRongAsync(http, dai.Gia!, ct)) : (ds, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/messenger] không đổi được mã cấp quyền");
            return (null, "Không gọi được Facebook: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>/me/accounts</c> rỗng thì hỏi <c>/me/permissions</c> xem Facebook thật sự cấp những gì,
    /// rồi nói đúng việc người dùng phải làm.
    ///
    /// <para>Tốn thêm một lượt gọi, nhưng chỉ ở nhánh HỎNG — và nó biến một câu lỗi đoán mò thành
    /// một câu chỉ đúng chỗ. Chỗ này từng làm mất một buổi.</para>
    /// </summary>
    private async Task<string> ViSaoRongAsync(HttpClient http, string token, CancellationToken ct)
    {
        var thieu = "";
        try
        {
            using var res = await http.GetAsync(
                $"{GraphBase}/{PhienBan}/me/permissions?access_token={U(token)}", ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
            var cap = (o?["data"] as JsonArray ?? new JsonArray()).OfType<JsonNode>()
                .Where(x => x["status"]?.ToString() == "granted")
                .Select(x => x["permission"]?.ToString() ?? "")
                .ToHashSet(StringComparer.Ordinal);

            _log.LogWarning("[chat/messenger] /me/accounts rỗng — quyền đã cấp: {Cap}",
                cap.Count == 0 ? "(không có)" : string.Join(", ", cap.OrderBy(x => x)));

            if (!cap.Contains("pages_show_list"))
                return "Facebook KHÔNG cấp quyền xem danh sách Trang (pages_show_list). Bấm kết nối "
                     + "lại, và ở màn hình đồng ý nhớ qua bước CHỌN TRANG — tick đúng Trang của công "
                     + "ty rồi mới bấm Tiếp tục. Bỏ qua bước đó là Facebook không đưa Trang nào cả.";

            thieu = cap.Contains("pages_messaging") ? "" : " (cũng chưa có quyền nhắn tin)";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/messenger] không đọc được /me/permissions");
        }

        return "Đã cấp quyền xem danh sách Trang nhưng Facebook trả về danh sách RỖNG" + thieu
             + ". Thường là Trang do một Danh mục doanh nghiệp sở hữu: bấm kết nối lại và ở màn "
             + "hình đồng ý nhớ CHỌN DOANH NGHIỆP rồi TICK TRANG của công ty. Nếu vẫn rỗng thì tài "
             + "khoản vừa đăng nhập không quản trị Trang nào.";
    }

    private async Task<(string? Gia, string? Loi)> ChuoiAsync(HttpClient http, string url,
        string truong, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        var o = JsonNode.Parse(raw)?.AsObject();
        if (o?["error"] is { } loi)
            return (null, $"Facebook từ chối: {loi["message"]?.ToString() ?? Cat(raw)}");
        var gia = o?[truong]?.ToString();
        return string.IsNullOrWhiteSpace(gia)
            ? (null, $"Facebook không trả về {truong}: {Cat(raw)}")
            : (gia, null);
    }

    /// <summary>
    /// Bật nhận tin cho Trang rồi lưu khoá. Trả <c>null</c> khi xong, câu lỗi tiếng Việt khi hỏng.
    ///
    /// <para><c>subscribed_apps</c> là thứ Zalo không có: nó thay cho việc bắt khách tự vào màn
    /// hình quản trị của Meta bật webhook. Gọi được cái này thì cả bước nối gói gọn trong một nút.</para>
    ///
    /// <para><c>accountId</c> lưu bằng CHÍNH id Trang — webhook dùng chung tra ngược ra công ty
    /// bằng id đó, đặt mã ngẫu nhiên là tin của khách không bao giờ tới nơi.</para>
    /// </summary>
    public async Task<string?> NoiTrangAsync(string tenantId, TrangUngVien trang, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GraphBase}/{PhienBan}/{trang.PageId}/subscribed_apps");
            req.Headers.Authorization = new("Bearer", trang.AccessToken);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["subscribed_fields"] = string.Join(",", SuKienTrang),
            });
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            // Meta trả {"success":true}. Không đọc trường đó mà chỉ nhìn mã HTTP là có ngày báo
            // "đã nối" cho một Trang không bao giờ gửi tin về.
            if (!res.IsSuccessStatusCode || o?["success"]?.GetValue<bool>() != true)
                return $"Không bật được nhận tin cho Trang: {Cat(raw)}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/messenger] không đăng ký được webhook cho Trang {P}", trang.PageId);
            return "Không gọi được Facebook: " + ex.Message;
        }

        await _cred.SaveAsync(tenantId, Channel, trang.PageId, new Dictionary<string, string?>
        {
            ["pageId"] = trang.PageId,
            ["pageName"] = trang.Ten,
            // Tên gợi nhớ mặc định là tên Trang. Người dùng sửa lại được, và chỉ khi họ muốn.
            ["label"] = trang.Ten,
            ["pageAccessToken"] = trang.AccessToken,
        }, ct);

        _log.LogInformation("[chat/messenger] tenant={T} vừa nối Trang {P} ({Ten})",
            tenantId, trang.PageId, trang.Ten);
        return null;
    }
    /// <summary>
    /// Bật ba chấm "đang gõ" bên phía khách.
    ///
    /// <para>Bot mất vài giây mới soạn xong; trong lúc đó khách nhìn màn hình trống và tưởng
    /// không ai đọc tin của mình. Một lượt gọi, không lưu gì, không có gì để hỏng.</para>
    ///
    /// <para>Meta tự tắt sau 20 giây hoặc khi mình gửi tin — không phải tắt tay.</para>
    /// </summary>
    public Task BaoDangGoAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => HanhDongAsync(tenantId, accountId, externalUserId, "typing_on", ct);

    /// <summary>Một lượt <c>sender_action</c> bất kỳ. Nuốt mọi lỗi — mất một chi tiết lịch sự
    /// không đáng để chặn tin của khách.</summary>
    private async Task HanhDongAsync(string tenantId, string accountId, string externalUserId,
        string hanhDong, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null || !c.TryGetValue("pageAccessToken", out var token) || string.IsNullOrWhiteSpace(token))
            return;
        try
        {
            var http = _http.CreateClient();
            await http.PostAsJsonAsync($"{GraphBase}/{PhienBan}/me/messages?access_token={U(token)}", new
            {
                recipient = new { id = externalUserId },
                sender_action = hanhDong,
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[chat/messenger] sender_action {H} hỏng", hanhDong);
        }
    }

    /// <summary>Đánh dấu đã xem bên phía khách. Cùng đường gọi với báo đang gõ.</summary>
    public Task BaoDaXemAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => HanhDongAsync(tenantId, accountId, externalUserId, "mark_seen", ct);

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
            using var res = await http.PostAsJsonAsync($"{GraphBase}/{PhienBan}/me/messages?access_token={token}", body, ct);
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
