// Services/Chat/Channels/InstagramChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Instagram Direct.
///
/// <para><b>Phần bóc tin dùng CHUNG với Messenger</b> (<see cref="MetaMessagingParser"/>): cùng hình
/// dạng <c>entry[] × messaging[]</c>, cùng <c>mid</c>, cùng <c>is_echo</c>, cùng cách gói đính kèm.
/// Lớp này chỉ giữ những chỗ hai kênh <b>khác nhau thật</b>.</para>
///
/// <para><b>Nối qua TRANG FACEBOOK đã kết nối, không qua đăng nhập Instagram riêng.</b> Tài khoản
/// Instagram Professional liên kết với một Trang thì mọi tin Direct về cùng ứng dụng Meta mình đã
/// có, ký bằng cùng App Secret, và gửi được bằng chính <b>Page Access Token</b> của Trang đó.
/// Đường còn lại (Instagram Login, <c>api.instagram.com</c>) không cần Trang nhưng đòi một ứng dụng
/// Instagram riêng và token <b>hết hạn sau 60 ngày</b> phải tự làm mới — thêm một thứ hỏng âm thầm
/// vào lúc không ai để ý. Công ty du lịch nào cũng đã có Trang, nên chọn đường qua Trang.</para>
///
/// <para>⚠️ <b>Ba chỗ KHÁC Messenger, đừng chép qua lại:</b></para>
/// <list type="number">
///   <item>Trường <c>object</c> của gói tin là <c>"instagram"</c>, không phải <c>"page"</c>.</item>
///   <item>Đường gửi là <c>graph.instagram.com</c> và token đi ở header <c>Authorization: Bearer</c>
///     — Instagram KHÔNG nhận <c>?access_token=</c> trên URL như Graph của Facebook.</item>
///   <item><b>KHÔNG có <c>message_deliveries</c></b>. Meta chỉ cấp <c>messaging_seen</c> cho
///     Instagram, nên tin đi qua kênh này nhảy thẳng "đã gửi" → "đã xem", không bao giờ có "đã
///     nhận". <b>Đó là đúng</b> — đừng tự nhảy trạng thái cho đủ ba mức, như thế là nói dối nhân
///     viên rằng khách đã nhận trong khi mình không biết.</item>
/// </list>
///
/// <para>⚠️ Và <c>messaging_seen</c> của Instagram báo bằng <c>mid</c> chứ không bằng
/// <c>watermark</c> — xử lý ở <see cref="MetaMessagingParser"/>, đọc ghi chú tại đó.</para>
/// </summary>
public class InstagramChatAdapter : IChatChannelAdapter
{
    private const string GraphBase = "https://graph.instagram.com";
    private const string MacDinhPhienBan = "v21.0";

    /// <summary>
    /// Trường webhook phải bật cho đối tượng <c>instagram</c> của ứng dụng Meta.
    ///
    /// <para>⚠️ <b>Đây là cấu hình cấp ỨNG DỤNG, KHÔNG phải lệnh gọi cho từng tài khoản.</b> Khác
    /// hẳn Trang Facebook (mỗi Trang một lượt <c>subscribed_apps</c>) và khác Telegram (mỗi bot một
    /// lượt <c>setWebhook</c>): Instagram nối qua Trang thì bật MỘT lần trong bảng điều khiển Meta,
    /// mọi tài khoản dùng chung. Ghi danh sách ra đây để lúc khai ứng dụng không ai phải đoán — thiếu
    /// một trường thì hỏng IM LẶNG: mã bóc vẫn đúng, chỉ là gói tin không bao giờ tới.</para>
    ///
    /// <para>Thiếu <c>messaging_seen</c> là dấu tích không bao giờ lên hai tích; thiếu
    /// <c>messaging_referral</c> là mất vĩnh viễn "khách đến từ đâu".</para>
    ///
    /// <para><c>message_deliveries</c> KHÔNG có mặt vì Meta không cấp nó cho Instagram.</para>
    ///
    /// <para>⚠️ <b>Chưa kiểm bằng tài khoản thật.</b> Danh sách theo tài liệu Meta ngày 27/08/2026;
    /// bước nối tài khoản phải thử trên một tài khoản Instagram Professional thật rồi mới coi là
    /// xong — không lấy hành vi của dự án tham chiếu làm nguồn quy định hiện hành.</para>
    /// </summary>
    public static readonly string[] SuKienTaiKhoan =
    {
        "messages", "messaging_postbacks", "messaging_optins", "messaging_seen",
        "messaging_referral", "message_reactions",
    };

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<InstagramChatAdapter> _log;

    public InstagramChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<InstagramChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.Instagram;

    private string PhienBan => Rong(_cfg["Chat:Messenger:Version"]) ?? MacDinhPhienBan;

    /// Khoá ký dùng CHUNG với Messenger: cùng một ứng dụng Meta, cùng một App Secret.
    private string? AppSecretNenTang => Rong(_cfg["Chat:Messenger:AppSecret"]);

    private static string? Rong(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string U(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// Id tài khoản Instagram của gói tin — <c>entry[].id</c>, giống hệt chỗ Messenger để id Trang.
    /// </summary>
    public static string? IdTaiKhoanCuaSuKien(string rawBody)
    {
        try { return JsonNode.Parse(rawBody)?["entry"]?[0]?["id"]?.ToString(); }
        catch { return null; }
    }

    /// <summary>
    /// Kiểm chữ ký rồi cho biết thân thô này thuộc tài khoản nào.
    ///
    /// <para><b>Tìm tài khoản TRƯỚC, kiểm chữ ký SAU</b> — cùng lý do với Messenger: mọi công ty
    /// dùng chung một ứng dụng Meta nên "khớp một App Secret bất kỳ" không chứng minh được gì.
    /// Và tra ra công ty cũng KHÔNG chứng minh tin là thật: id tài khoản Instagram nằm công khai
    /// trên chính trang đó, ai cũng đọc được.</para>
    /// </summary>
    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        var ky = headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ky)) return null;

        var idTk = IdTaiKhoanCuaSuKien(rawBody);
        var dsach = await _cred.ListAccountsAsync(tenantId, Channel, ct);

        foreach (var tk in dsach)
        {
            // Khớp theo mã tài khoản trên URL (đường cũ) HOẶC theo id Instagram trong thân tin
            // (đường dùng chung). Thiếu một nhánh là một trong hai cách khai ngừng nhận tin.
            var khop = accountIdTuUrl is { Length: > 0 }
                ? string.Equals(tk.AccountId, accountIdTuUrl, StringComparison.OrdinalIgnoreCase)
                : idTk is { Length: > 0 } && tk.GiaTri.GetValueOrDefault("igId", "") == idTk;
            if (!khop) continue;

            var bimat = Rong(tk.GiaTri.GetValueOrDefault("appSecret", "")) ?? AppSecretNenTang;
            if (bimat is null) continue;
            if (KyDung(bimat, rawBody, ky!)) return tk.AccountId;

            _log.LogWarning("[chat/instagram] chữ ký sai cho tài khoản {A} (ig={Ig}) của {T}",
                tk.AccountId, idTk, tenantId);
            return null;
        }

        _log.LogWarning("[chat/instagram] không tài khoản nào khớp ig={Ig} của {T} — bỏ gói tin",
            idTk, tenantId);
        return null;
    }

    private static bool KyDung(string appSecret, string rawBody, string header)
    {
        var mong = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..] : header;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var tinh = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var a = Encoding.UTF8.GetBytes(tinh);
        var b = Encoding.UTF8.GetBytes(mong.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Đường webhook DÙNG CHUNG: tra ra công ty rồi kiểm chữ ký.
    ///
    /// <para>Instagram đăng ký webhook theo ỨNG DỤNG, một địa chỉ duy nhất cho mọi tài khoản —
    /// URL không mang tên công ty được. Định tuyến bằng <b>id tài khoản Instagram</b>, vốn chính là
    /// <c>accountId</c> lúc nối.</para>
    ///
    /// <para>Tra được công ty rồi <b>vẫn phải kiểm chữ ký</b> — id đó nằm công khai, ai cũng đọc
    /// được trên chính trang Instagram đó.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> XacMinhDungChungAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (IdTaiKhoanCuaSuKien(rawBody) is not { } igId) return null;
        var tenant = await _cred.TimTenantAsync(Channel, igId, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/instagram] nhận tin của tài khoản {Ig} nhưng chưa công ty nào nối", igId);
            return null;
        }
        return await VerifyAsync(tenant, igId, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
    }

    /// <inheritdoc />
    /// <remarks>Dùng chung với Messenger — xem <see cref="MetaMessagingParser"/>.</remarks>
    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
        => MetaMessagingParser.Boc(rawBody, ChatChannel.Instagram);

    // ── Gửi ─────────────────────────────────────────────────────────────────

    private async Task<string?> TokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        return c is not null && c.TryGetValue("pageAccessToken", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t : null;
    }

    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => GuiAsync(tenantId, accountId, new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = externalUserId },
            ["message"] = new JsonObject { ["text"] = text },
        }, ct);

    /// <summary>
    /// Gửi ảnh/tệp. Instagram tải về từ URL công khai, y như Messenger.
    ///
    /// <para>⚠️ <b>Instagram KHÔNG gộp được chú thích vào cùng tin media</b> (Telegram thì được).
    /// Nên chú thích phải đi thành một tin riêng — bỏ qua là khách nhận ảnh trần không hiểu để làm
    /// gì.</para>
    /// </summary>
    public async Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        var kieu = loai switch
        {
            ChatKind.Anh => "image",
            ChatKind.AmThanh => "audio",
            _ => "file",
        };
        var kq = await GuiAsync(tenantId, accountId, new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = externalUserId },
            ["message"] = new JsonObject
            {
                ["attachment"] = new JsonObject
                {
                    ["type"] = kieu,
                    ["payload"] = new JsonObject { ["url"] = url, ["is_reusable"] = true },
                },
            },
        }, ct);

        if (kq.Ok && !string.IsNullOrWhiteSpace(caption))
            await SendTextAsync(tenantId, accountId, externalUserId, caption!, ct);
        return kq;
    }

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, JsonObject than,
        CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null)
            return new(false, false, null, "Chưa nối tài khoản Instagram cho công ty này");

        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{GraphBase}/{PhienBan}/me/messages")
            {
                Content = new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            // ⚠️ Token đi ở HEADER. Instagram không nhận ?access_token= trên URL như Graph của
            // Facebook — chép nguyên đường gửi của Messenger sang là mọi tin đều bị từ chối.
            req.Headers.Add("Authorization", "Bearer " + token);

            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            if (res.IsSuccessStatusCode && o?["error"] is null)
                return new(true, false, o?["message_id"]?.ToString(), null);

            var moTa = o?["error"]?["message"]?.ToString() ?? Cat(raw);
            // 5xx là hỏng tạm thời phía Meta; 4xx là mình sai (hết cửa sổ 24h, khách chặn) —
            // thử lại chỉ quay vòng vô nghĩa.
            return new(false, (int)res.StatusCode >= 500, null, $"Instagram từ chối: {moTa}");
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);
        }
    }

    // ── Năng lực kênh ───────────────────────────────────────────────────────

    /// <summary>
    /// Tên + ảnh khách. Gói tin chỉ có mã người dùng (IGSID), y như Messenger.
    ///
    /// <para>Mã đó riêng cho từng tài khoản Instagram, nên phải hỏi bằng token của ĐÚNG tài khoản
    /// đã nhận tin.</para>
    /// </summary>
    public async Task<HoSoKhach?> HoSoKhachAsync(string tenantId, string accountId,
        string externalUserId, CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return null;
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{GraphBase}/{PhienBan}/{U(externalUserId)}?fields=name,username,profile_pic");
            req.Headers.Add("Authorization", "Bearer " + token);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();
            if (o is null || o["error"] is not null)
            {
                _log.LogWarning("[chat/instagram] không lấy được hồ sơ khách {Id}: {Loi}",
                    externalUserId, Cat(raw));
                return null;
            }

            // `name` là tên hiển thị, `username` là @tên. Thiếu tên hiển thị thì lấy @tên còn hơn
            // để hộp thư hiện một dãy số.
            var ten = Rong(o["name"]?.ToString()) ?? Rong(o["username"]?.ToString());
            var anh = Rong(o["profile_pic"]?.ToString());
            return ten is null && anh is null ? null : new HoSoKhach(ten, anh);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/instagram] hỏi hồ sơ khách {Id} hỏng", externalUserId);
            return null;
        }
    }

    public Task BaoDangGoAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => HanhDongAsync(tenantId, accountId, externalUserId, "typing_on", ct);

    /// <summary>Một lượt <c>sender_action</c>. Nuốt mọi lỗi — mất một chi tiết lịch sự không đáng
    /// để chặn tin của khách.</summary>
    private async Task HanhDongAsync(string tenantId, string accountId, string externalUserId,
        string hanhDong, CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return;
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{GraphBase}/{PhienBan}/me/messages")
            {
                Content = new StringContent(new JsonObject
                {
                    ["recipient"] = new JsonObject { ["id"] = externalUserId },
                    ["sender_action"] = hanhDong,
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + token);
            await http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[chat/instagram] sender_action {H} hỏng", hanhDong);
        }
    }

    /// <summary>
    /// Dò tài khoản Instagram liên kết với một Trang vừa nối, và nối luôn nếu có.
    ///
    /// <para>Đây là chỗ Instagram <b>rẻ hơn hẳn</b> mọi kênh khác: khách đã bấm "Kết nối Facebook"
    /// rồi thì không phải bấm gì thêm — cùng token, cùng ứng dụng, chỉ thêm một lượt hỏi.</para>
    ///
    /// <para>⚠️ <b>Không bao giờ chặn việc nối Trang.</b> Trang không có Instagram liên kết là
    /// chuyện bình thường; báo lỗi ở đây là làm hỏng một luồng đang chạy đúng để đổi lấy một tính
    /// năng phụ. Hỏng thì ghi log rồi thôi.</para>
    /// </summary>
    /// <returns>Id tài khoản Instagram đã nối, hoặc <c>null</c> nếu Trang không có.</returns>
    public async Task<string?> NoiTuTrangAsync(string tenantId, string pageId, string? pageName,
        string pageAccessToken, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            // Lượt hỏi này đi qua Graph của FACEBOOK (hỏi về Trang), khác đường GỬI TIN vốn đi qua
            // graph.instagram.com. Hai tên miền, đừng gộp.
            using var res = await http.GetAsync(
                $"https://graph.facebook.com/{PhienBan}/{U(pageId)}"
                + $"?fields=instagram_business_account%7Bid%2Cusername%7D"
                + $"&access_token={U(pageAccessToken)}", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var ig = JsonNode.Parse(raw)?["instagram_business_account"];
            var igId = ig?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(igId))
            {
                _log.LogInformation("[chat/instagram] Trang {P} không có tài khoản Instagram liên kết", pageId);
                return null;
            }

            var ten = ig?["username"]?.ToString();
            await _cred.SaveAsync(tenantId, Channel, igId!, new Dictionary<string, string?>
            {
                ["igId"] = igId,
                ["igUsername"] = ten,
                // Tên gợi nhớ mặc định lấy @tên Instagram, không lấy tên Trang: một Trang có thể
                // đổi tên, mà nhân viên nhìn hộp thư thì nhận ra @tên nhanh hơn.
                ["label"] = string.IsNullOrWhiteSpace(ten) ? pageName : "@" + ten,
                // Dùng CHÍNH token của Trang — Instagram nối qua Trang thì không có token riêng.
                ["pageAccessToken"] = pageAccessToken,
            }, ct);

            _log.LogInformation("[chat/instagram] tenant={T} nối luôn Instagram @{Ten} ({Id}) theo Trang {P}",
                tenantId, ten, igId, pageId);
            return igId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/instagram] dò Instagram của Trang {P} hỏng", pageId);
            return null;
        }
    }
    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];
}
