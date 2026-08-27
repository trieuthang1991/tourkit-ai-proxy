// Services/Chat/Channels/WhatsAppChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// WhatsApp Cloud API.
///
/// <para><b>Cùng nhà Meta nhưng KHÔNG dùng chung được gì với Messenger/Instagram.</b> Hai kênh kia
/// đi hợp đồng nhắn tin <c>entry[] × messaging[]</c>; WhatsApp đi hợp đồng Business Management
/// <c>entry[] × changes[] × value</c> — khác từ hình dạng gói tin, cách báo trạng thái, đến cách
/// lấy tệp. Chỉ có <b>chữ ký</b> là giống (HMAC App Secret trong <c>X-Hub-Signature-256</c>). Đừng
/// nhét nó vào <see cref="MetaMessagingParser"/>: gộp hai hợp đồng khác nhau vào một hàm là chỗ
/// sinh ra lỗi im lặng.</para>
///
/// <para>⚠️ <b>Bốn chỗ khác, mỗi chỗ là một kiểu hỏng riêng:</b></para>
/// <list type="number">
///   <item><b>Trạng thái tin báo theo <c>id</c> từng tin</b> (<c>statuses[]</c> với
///     <c>sent|delivered|read|failed</c>), không theo mốc nước như Messenger. Đi cùng đường với
///     Instagram — xem <c>StateWatermark.ExternalMsgId</c>.</item>
///   <item><b>Tệp khách gửi KHÔNG có URL</b>: gói tin chỉ cho mã tệp, phải hỏi ra đường tải rồi
///     tải bằng <b>khoá xác thực</b>. Khác Telegram (khoá giấu trong đường dẫn) và khác hẳn
///     Zalo/Messenger (URL công khai). Bắt buộc đi qua máy chủ mình.</item>
///   <item><b>Gửi theo <c>phone_number_id</c></b> của mình, không phải theo id Trang; người nhận
///     là <b>số điện thoại</b>, và đó cũng là danh tính khách.</item>
///   <item><b>Ngoài cửa sổ 24h phải dùng mẫu đã duyệt</b>, không gửi chữ tự do được. Đây là lý do
///     ô soạn phải khoá đúng lúc: gọi API rồi mới biết là nhân viên đã gõ xong tin.</item>
/// </list>
///
/// <para>⚠️ <b>Chưa kiểm bằng tài khoản thật</b> (27/08/2026). Cần một WABA đã xác minh doanh
/// nghiệp cùng số điện thoại riêng — chưa có thì phần bóc tin và chữ ký có test, còn đường gửi và
/// bước nối vẫn là theo tài liệu.</para>
/// </summary>
public class WhatsAppChatAdapter : IChatChannelAdapter
{
    private const string GraphBase = "https://graph.facebook.com";
    private const string DefaultApiVersion = "v21.0";

    /// <summary>
    /// Trường webhook đăng ký cho WABA (<c>POST /{wabaId}/subscribed_apps</c>). Danh sách này
    /// chép từ dự án tham chiếu, không phải tự chọn.
    ///
    /// <list type="bullet">
    ///   <item><c>messages</c> — thiếu là không có tin nào cả. Trường này chở CẢ tin khách gửi
    ///     LẪN <c>statuses[]</c> báo trạng thái, không phải hai trường riêng như Messenger.</item>
    ///   <item><c>history</c> — chở <b>lịch sử chat cũ</b> khi công ty chuyển từ ứng dụng WhatsApp
    ///     Business sang dùng API. Đây là <b>đường DUY NHẤT</b> trong sáu kênh lấy lại được đoạn
    ///     hội thoại có từ trước lúc nối; bỏ nó là mất hẳn cơ hội đó, và không lấy lại được sau.</item>
    ///   <item><c>smb_app_state_sync</c> — chở danh bạ khách có sẵn trong ứng dụng.</item>
    ///   <item><c>smb_message_echoes</c> — chở tin nhân viên trả lời từ CHÍNH điện thoại. Thiếu
    ///     nó thì hộp thư chỉ thấy câu khách hỏi mà không thấy câu đã trả lời, rồi trợ lý trả lời
    ///     đè lên người thật.</item>
    /// </list>
    /// </summary>
    public static readonly string[] WabaEvents =
        { "messages", "history", "smb_app_state_sync", "smb_message_echoes" };

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WhatsAppChatAdapter> _log;

    public WhatsAppChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<WhatsAppChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.WhatsApp;

    private string ApiVersion => NullIfBlank(_cfg["Chat:WhatsApp:Version"])
                               ?? NullIfBlank(_cfg["Chat:Messenger:Version"]) ?? DefaultApiVersion;

    /// Khoá ký: ứng dụng Meta riêng cho WhatsApp nếu có, không thì dùng chung với Messenger.
    private string? PlatformAppSecret => NullIfBlank(_cfg["Chat:WhatsApp:AppSecret"])
                                        ?? NullIfBlank(_cfg["Chat:Messenger:AppSecret"]);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string U(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// <c>phone_number_id</c> của gói tin — khoá định tuyến ra công ty.
    ///
    /// <para>Nằm ở <c>entry[].changes[].value.metadata.phone_number_id</c>, KHÔNG phải
    /// <c>entry[].id</c> (chỗ đó là id WABA). Lấy nhầm là tra ra rỗng và tin rơi vào hư không.</para>
    /// </summary>
    public static string? PhoneNumberIdOfEvent(string rawBody)
    {
        try
        {
            return JsonNode.Parse(rawBody)?["entry"]?[0]?["changes"]?[0]?["value"]?["metadata"]?
                ["phone_number_id"]?.ToString();
        }
        catch { return null; }
    }

    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        var ky = headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ky)) return null;

        var idSo = PhoneNumberIdOfEvent(rawBody);
        var dsach = await _cred.ListAccountsAsync(tenantId, Channel, ct);

        foreach (var tk in dsach)
        {
            var khop = accountIdTuUrl is { Length: > 0 }
                ? string.Equals(tk.AccountId, accountIdTuUrl, StringComparison.OrdinalIgnoreCase)
                : idSo is { Length: > 0 } && tk.GiaTri.GetValueOrDefault("phoneNumberId", "") == idSo;
            if (!khop) continue;

            var bimat = NullIfBlank(tk.GiaTri.GetValueOrDefault("appSecret", "")) ?? PlatformAppSecret;
            if (bimat is null) continue;
            if (SignatureMatches(bimat, rawBody, ky!)) return tk.AccountId;

            _log.LogWarning("[chat/whatsapp] chữ ký sai cho số {So} của {T}", idSo, tenantId);
            return null;
        }

        _log.LogWarning("[chat/whatsapp] không tài khoản nào khớp số {So} của {T} — bỏ gói tin",
            idSo, tenantId);
        return null;
    }

    /// <summary>
    /// Đường webhook DÙNG CHUNG: tra ra công ty rồi kiểm chữ ký.
    ///
    /// <para>Webhook đăng ký theo ỨNG DỤNG nên URL không mang tên công ty được. Khoá định tuyến
    /// là số điện thoại trong thân tin — nhưng tra ra công ty <b>KHÔNG</b> chứng minh tin là thật,
    /// nên vẫn phải kiểm chữ ký bằng khoá của chính tài khoản đó.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> ResolveSharedWebhookAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (PhoneNumberIdOfEvent(rawBody) is not { } khoa) return null;
        var tenant = await _cred.FindTenantAsync(Channel, khoa, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/whatsapp] nhận tin của {Khoa} nhưng chưa công ty nào nối", khoa);
            return null;
        }
        return await VerifyAsync(tenant, khoa, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
    }

    private static bool SignatureMatches(string appSecret, string rawBody, string header)
    {
        var mong = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..] : header;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var tinh = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var a = Encoding.UTF8.GetBytes(tinh);
        var b = Encoding.UTF8.GetBytes(mong.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ── Nối bằng MỘT nút (Embedded Signup của Meta) ─────────────────────────

    /// Id ứng dụng: dùng chung ứng dụng Meta với Messenger nếu không khai riêng.
    public string? PlatformAppId => NullIfBlank(_cfg["Chat:WhatsApp:AppId"]) ?? NullIfBlank(_cfg["Chat:Messenger:AppId"]);

    /// <summary>
    /// Mã cấu hình <b>Embedded Signup</b> khai trong bảng điều khiển Meta.
    ///
    /// <para>⚠️ WhatsApp KHÔNG xin quyền bằng <c>scope</c> như Messenger — nó đi bằng
    /// <c>config_id</c>. Truyền <c>scope</c> vào là Meta bỏ qua và người dùng đi hết luồng mà
    /// không cấp quyền nào, rồi mình không tra ra tài khoản WhatsApp của họ.</para>
    /// </summary>
    public string? ConfigId => NullIfBlank(_cfg["Chat:WhatsApp:ConfigId"]);

    public bool HasPlatformApp =>
        PlatformAppId is not null && PlatformAppSecret is not null && ConfigId is not null;

    /// <summary>
    /// Đường mở hộp thoại cấp quyền của Meta cho WhatsApp.
    ///
    /// <para><c>extras</c> bật luồng Embedded Signup — thiếu nó thì Meta mở hộp thoại đăng nhập
    /// thường, người dùng không được dẫn qua bước tạo/chọn tài khoản WhatsApp.</para>
    /// </summary>
    public string PermissionUrlFor(string redirectUri, string state)
    {
        var extras = "{\"sessionInfoVersion\":3,\"setup\":{}}";
        return $"https://www.facebook.com/{ApiVersion}/dialog/oauth"
             + $"?client_id={U(PlatformAppId!)}&config_id={U(ConfigId!)}"
             + $"&redirect_uri={U(redirectUri)}&response_type=code&state={U(state)}"
             + $"&extras={U(extras)}";
    }

    /// <summary>Kết quả nối một số WhatsApp.</summary>
    public record KetQuaNoiSo(string? AccountId, string? SoHienThi, string? Loi);

    /// <summary>
    /// Đổi mã cấp quyền thành một số WhatsApp nối sẵn — <b>người dùng không nhập gì cả</b>.
    ///
    /// <para>Bốn bước, chép cách làm của dự án tham chiếu (ChatbotX) chứ không tự nghĩ:</para>
    /// <list type="number">
    ///   <item>Đổi <c>code</c> lấy token của người dùng.</item>
    ///   <item><c>debug_token</c> đọc <c>granular_scopes</c> → quyền
    ///     <c>whatsapp_business_management</c> chỉ đúng MỘT tài khoản WhatsApp, nên
    ///     <c>target_ids[0]</c> chính là id tài khoản đó. Đây là mẹo mấu chốt: hộp thoại chỉ trả
    ///     về <c>code</c>, không trả về id nào.</item>
    ///   <item>Hỏi tài khoản đó lấy <b>số điện thoại đầu tiên</b> — cái mình dùng để gửi.</item>
    ///   <item>Bật nhận tin cho tài khoản (<c>subscribed_apps</c>).</item>
    /// </list>
    ///
    /// <para>⚠️ <c>redirect_uri</c> lúc đổi mã phải TRÙNG KHÍT chuỗi đã dùng lúc mở hộp thoại —
    /// lệch một ký tự là Meta từ chối và câu lỗi của họ không nói lệch ở đâu.</para>
    /// </summary>
    public async Task<KetQuaNoiSo> ConnectFromCodeAsync(string tenantId, string code,
        string redirectUri, CancellationToken ct)
    {
        if (!HasPlatformApp) return new(null, null, "Máy chủ chưa khai ứng dụng WhatsApp (Chat:WhatsApp)");
        var http = _http.CreateClient();

        // 1. code → token người dùng
        var tk = await JsonAsync(http,
            $"{GraphBase}/{ApiVersion}/oauth/access_token?client_id={U(PlatformAppId!)}"
            + $"&client_secret={U(PlatformAppSecret!)}&code={U(code)}&redirect_uri={U(redirectUri)}", ct);
        var token = tk?["access_token"]?.ToString();
        if (string.IsNullOrWhiteSpace(token))
            return new(null, null, $"Không đổi được mã cấp quyền: {tk?["error"]?["message"] ?? "không rõ lý do"}");

        // 2. debug_token → id tài khoản WhatsApp được cấp quyền
        var appToken = $"{PlatformAppId}|{PlatformAppSecret}";
        var dbg = await JsonAsync(http,
            $"{GraphBase}/debug_token?input_token={U(token!)}&access_token={U(appToken)}", ct);
        string? wabaId = null;
        if (dbg?["data"]?["granular_scopes"] is JsonArray quyen)
            foreach (var q in quyen.OfType<JsonNode>())
                if (q["scope"]?.ToString() == "whatsapp_business_management")
                    wabaId = (q["target_ids"] as JsonArray)?.FirstOrDefault()?.ToString();
        if (string.IsNullOrWhiteSpace(wabaId))
            return new(null, null,
                "Không tra ra tài khoản WhatsApp nào từ lượt cấp quyền. Kiểm lại mã cấu hình "
                + "Embedded Signup (Chat:WhatsApp:ConfigId).");

        // 3. tài khoản → số điện thoại đầu tiên
        var waba = await JsonAsync(http,
            $"{GraphBase}/{ApiVersion}/{U(wabaId!)}?fields=name,owner_business_info,phone_numbers"
            + $"&access_token={U(token!)}", ct);
        var so = (waba?["phone_numbers"]?["data"] as JsonArray)?.FirstOrDefault();
        var soId = so?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(soId))
            return new(null, null, "Tài khoản WhatsApp này chưa có số điện thoại nào.");

        // 4. bật nhận tin cho tài khoản.
        //
        // ⚠️ PHẢI gửi subscribed_fields trong thân yêu cầu. Gọi rỗng thì Meta chỉ bật đúng bộ
        // trường mặc định khai ở bảng điều khiển ứng dụng — hộp thư vẫn có tin, nên nhìn qua
        // tưởng chạy đúng, nhưng LỊCH SỬ CHAT CŨ và tin nhân viên gõ từ điện thoại thì không bao
        // giờ tới. Đó là loại hỏng chỉ phát hiện ra hàng tuần sau, lúc không lấy lại được nữa.
        try
        {
            var than = new JsonObject { ["subscribed_fields"] = new JsonArray(
                WabaEvents.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) };
            using var res = await http.PostAsync(
                $"{GraphBase}/{ApiVersion}/{U(wabaId!)}/subscribed_apps?access_token={U(token!)}",
                new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode || JsonNode.Parse(raw)?["error"] is not null)
                return new(null, null, $"Không bật được nhận tin WhatsApp: {Truncate(raw)}");
        }
        catch (Exception ex) { return new(null, null, "Không gọi được Meta: " + ex.Message); }

        var soHienThi = so?["display_phone_number"]?.ToString();
        var tenWaba = waba?["name"]?.ToString();

        // Mã tài khoản là chính id số điện thoại — cũng là khoá định tuyến webhook, nên tra ngược
        // ra công ty chỉ mất một phép so.
        await _cred.SaveAsync(tenantId, Channel, soId!, new Dictionary<string, string?>
        {
            ["phoneNumberId"] = soId,
            ["wabaId"] = wabaId,
            ["accessToken"] = token,
            ["displayPhone"] = soHienThi,
            ["label"] = string.IsNullOrWhiteSpace(soHienThi) ? tenWaba : soHienThi,
        }, ct);

        _log.LogInformation("[chat/whatsapp] tenant={T} nối số {So} ({Id}) của tài khoản {W}",
            tenantId, soHienThi, soId, wabaId);

        // 5. xin Meta đẩy lịch sử chat cũ + danh bạ về.
        //
        // Không chặn lượt nối nếu hỏng: nối được rồi là kênh chạy, lịch sử chỉ là phần thêm.
        await TriggerHistorySyncAsync(http, soId!, token!, ct);

        return new(soId, soHienThi, null);
    }

    /// <summary>
    /// Xin Meta đẩy <b>lịch sử chat cũ</b> và <b>danh bạ</b> của ứng dụng WhatsApp Business về.
    ///
    /// <para>⚠️ <b>Đăng ký trường <c>history</c> thôi là KHÔNG ĐỦ</b> — nếu không gọi hàm này thì
    /// Meta không gửi gì cả, và nhìn bên ngoài mọi thứ vẫn bình thường: tin mới vẫn về, chỉ có
    /// lịch sử là im lặng không bao giờ tới. Đây là chỗ dễ tưởng đã xong nhất.</para>
    ///
    /// <para>⚠️ <b>Mỗi loại chỉ xin được MỘT LẦN</b>, và chỉ trong <b>24 giờ</b> kể từ lúc nối.
    /// Bỏ lỡ là mất hẳn, không có đường xin lại. Vì thế gọi ngay trong bước nối chứ không hẹn
    /// một tác vụ chạy sau.</para>
    ///
    /// <para>Ba lý do hỏng dưới đây đều BÌNH THƯỜNG, không phải sự cố: số không phải loại đăng ký
    /// từ ứng dụng WhatsApp Business (mã 131000/10), đã xin rồi, hoặc quá 24 giờ. Ghi log ở mức
    /// nhắc chứ không báo lỗi — dựng cảnh báo cho ba ca này là báo động giả cho gần như mọi lượt
    /// nối của tài khoản mở mới.</para>
    /// </summary>
    private async Task TriggerHistorySyncAsync(HttpClient http, string phoneNumberId, string token,
        CancellationToken ct)
    {
        foreach (var loai in new[] { "history", "smb_app_state_sync" })
        {
            try
            {
                var than = new JsonObject
                {
                    ["messaging_product"] = "whatsapp",
                    ["sync_type"] = loai,
                };
                using var res = await http.PostAsync(
                    $"{GraphBase}/{ApiVersion}/{U(phoneNumberId)}/smb_app_data?access_token={U(token)}",
                    new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var raw = await res.Content.ReadAsStringAsync(ct);

                if (res.IsSuccessStatusCode && JsonNode.Parse(raw)?["error"] is null)
                {
                    _log.LogInformation("[chat/whatsapp] đã xin Meta đẩy {Loai} cho số {So}",
                        loai, phoneNumberId);
                    continue;
                }

                _log.LogInformation("[chat/whatsapp] không xin được {Loai} cho số {So} — "
                    + "bình thường nếu số này không đăng ký từ ứng dụng WhatsApp Business, "
                    + "đã xin rồi, hoặc đã quá 24 giờ. Meta trả: {Ly}",
                    loai, phoneNumberId, Truncate(raw));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[chat/whatsapp] gọi smb_app_data ({Loai}) hỏng", loai);
            }
        }
    }

    /// <summary>Gọi Graph trả JSON. Không ném — chỗ gọi tự đọc lỗi trong thân.</summary>
    private async Task<JsonNode?> JsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, ct);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/whatsapp] gọi Graph hỏng");
            return null;
        }
    }

    // ── Bóc gói tin ─────────────────────────────────────────────────────────

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        if (goc?["entry"] is not JsonArray entries) return ra;
        foreach (var e in entries)
        {
            if (e?["changes"] is not JsonArray changes) continue;
            foreach (var c in changes)
            {
                var v = c?["value"];
                if (v is null) continue;

                // Tên khách nằm ở contacts[], TÁCH khỏi messages[] và ghép lại bằng số điện thoại.
                // Không ghép thì hộp thư hiện một dãy số thay cho tên, dù gói tin có sẵn tên.
                var ten = new Dictionary<string, string>(StringComparer.Ordinal);
                if (v["contacts"] is JsonArray lh)
                    foreach (var x in lh.OfType<JsonNode>())
                    {
                        var so = x["wa_id"]?.ToString();
                        var t = x["profile"]?["name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(so) && !string.IsNullOrWhiteSpace(t)) ten[so!] = t!;
                    }

                ReadStatuses(v, ra);
                ReadMessages(v, ten, ra);
                ReadEchoes(v, ten, ra);
                ReadHistory(v, ten, ra);
            }
        }
        return ra;
    }

    /// <summary>
    /// <c>message_echoes[]</c>: tin nhân viên gõ từ CHÍNH ứng dụng WhatsApp trên điện thoại.
    ///
    /// <para>Thiếu nhánh này thì hộp thư chỉ thấy câu khách hỏi mà không thấy câu đã trả lời —
    /// rồi trợ lý trả lời đè lên người thật, khách nhận hai câu khác nhau cho cùng một câu hỏi.</para>
    ///
    /// <para>Khách là <c>to</c> chứ không phải <c>from</c>: ở tiếng vọng, <c>from</c> là số của
    /// công ty. Lấy nhầm đầu là hội thoại mang tên chính mình.</para>
    ///
    /// <para>Meta dùng cả hai tên trường cho cùng một thứ tuỳ đời gói tin — đọc cả hai.</para>
    /// </summary>
    private void ReadEchoes(JsonNode v, Dictionary<string, string> ten, List<InboundChatEvent> ra)
    {
        foreach (var khoa in new[] { "message_echoes", "smb_message_echoes" })
        {
            if (v[khoa] is not JsonArray ds) continue;
            foreach (var m in ds.OfType<JsonNode>())
            {
                var khach = m["to"]?.ToString();
                if (string.IsNullOrWhiteSpace(khach)) continue;
                var (loai, chu, att) = DocThan(m);
                ra.Add(new(Channel, khach!, m["id"]?.ToString(), loai, chu, att, LucGui(m),
                    IsEcho: true, DisplayName: ten.GetValueOrDefault(khach!)));
            }
        }
    }

    /// <summary>
    /// <c>history[]</c>: đoạn chat CŨ, có từ trước lúc nối — Meta đẩy về sau khi mình gọi
    /// <c>smb_app_data</c>. Đây là <b>đường duy nhất</b> trong sáu kênh lấy lại được lịch sử.
    ///
    /// <para><b>Chiều tin đọc từ <c>thread.id</c>.</b> Mã luồng CHÍNH LÀ số của khách, nên
    /// <c>from != thread.id</c> nghĩa là tin của mình. Nhờ vậy không cần biết số của công ty —
    /// mà trong gói lịch sử thì đúng là không phải lúc nào cũng có.</para>
    ///
    /// <para>Bỏ tin <c>type="errors"</c>: Meta không giải mã được, không có nội dung gì dùng
    /// được. Ghi vào là hộp thư đầy dòng trống mà không ai biết là tin gì.</para>
    /// </summary>
    private void ReadHistory(JsonNode v, Dictionary<string, string> ten, List<InboundChatEvent> ra)
    {
        if (v["history"] is not JsonArray mangLs) return;
        foreach (var manh in mangLs.OfType<JsonNode>())
        {
            if (manh["threads"] is not JsonArray luong) continue;
            foreach (var t in luong.OfType<JsonNode>())
            {
                var khach = t["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(khach)) continue;
                if (t["messages"] is not JsonArray tins) continue;

                foreach (var m in tins.OfType<JsonNode>())
                {
                    if (m["type"]?.ToString() == "errors") continue;
                    var (loai, chu, att) = DocThan(m);
                    ra.Add(new(Channel, khach!, m["id"]?.ToString(), loai, chu, att, LucGui(m),
                        IsEcho: m["from"]?.ToString() != khach,
                        DisplayName: ten.GetValueOrDefault(khach!),
                        IsHistory: true));
                }
            }
        }
    }

    /// <summary>
    /// Bóc phần nội dung dùng chung giữa tin thường, tiếng vọng và lịch sử — ba chỗ này mang
    /// CÙNG hình dạng tin, chỉ khác vỏ ngoài.
    ///
    /// <para>Media ở đây <b>chỉ giữ mã tệp</b>, không tải về: gói lịch sử có thể chở hàng nghìn
    /// tệp, tải hết ngay lúc nhận webhook là treo cả đường nhận tin. Tin vẫn hiện đúng chỗ đúng
    /// giờ, kèm nhãn loại tệp.</para>
    /// </summary>
    private static (ChatKind Loai, string? Chu, string? Att) DocThan(JsonNode m)
    {
        var kieu = m["type"]?.ToString();
        var chu = m["text"]?["body"]?.ToString();

        foreach (var (khoa, loai) in new[]
        {
            ("image", ChatKind.Image), ("sticker", ChatKind.Sticker), ("video", ChatKind.File),
            ("audio", ChatKind.Audio), ("document", ChatKind.File),
        })
        {
            if (m[khoa] is not JsonNode tep) continue;
            var att = new JsonObject
            {
                ["id"] = tep["id"]?.ToString(),
                ["mime"] = tep["mime_type"]?.ToString(),
                ["ten"] = tep["filename"]?.ToString(),
            }.ToJsonString();
            return (loai, chu ?? tep["caption"]?.ToString(), att);
        }

        // Loại chưa hỗ trợ (vị trí, danh thiếp, đơn hàng…): giữ lại một dòng nói rõ là gì thay vì
        // bỏ hẳn. Dòng trống giữa hội thoại khiến người đọc tưởng mất tin.
        if (string.IsNullOrWhiteSpace(chu) && !string.IsNullOrWhiteSpace(kieu))
            chu = $"[{kieu}]";
        return (ChatKind.Text, chu, null);
    }

    /// <summary>
    /// <c>timestamp</c> của WhatsApp là GIÂY Unix, kiểu chuỗi. Thiếu hoặc hỏng thì lấy giờ hiện
    /// tại — với tin trực tiếp là đúng, còn tin lịch sử thì thà sai chỗ hơn là mất hẳn tin.
    /// </summary>
    private static DateTime LucGui(JsonNode m)
        => long.TryParse(m["timestamp"]?.ToString(), out var giay)
            ? DateTimeOffset.FromUnixTimeSeconds(giay).UtcDateTime
            : DateTime.UtcNow;
    /// <summary>
    /// <c>statuses[]</c>: WhatsApp báo trạng thái theo <b>mã từng tin</b>, không theo mốc nước.
    ///
    /// <para>Đi chung đường với Instagram: chuyển mã tin sang cho lõi tra ra thời điểm. Lấy tạm giờ
    /// nhận gói là đánh dấu THỪA lên mọi tin gửi trước đó, kể cả tin khách chưa hề mở.</para>
    ///
    /// <para><c>failed</c> KHÔNG map sang trạng thái hỏng: tin đã rời khỏi mình rồi, và luật
    /// <c>ChatRules.CanAdvanceState</c> vốn chặn việc tin gửi được lại thành hỏng. Ghi log để còn tra.</para>
    /// </summary>
    private void ReadStatuses(JsonNode v, List<InboundChatEvent> ra)
    {
        if (v["statuses"] is not JsonArray ds) return;
        foreach (var st in ds.OfType<JsonNode>())
        {
            var maTin = st["id"]?.ToString();
            var so = st["recipient_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(maTin) || string.IsNullOrWhiteSpace(so)) continue;

            var tt = st["status"]?.ToString() switch
            {
                "sent" => ChatState.Sent,
                "delivered" => ChatState.Delivered,
                "read" => ChatState.Seen,
                _ => (ChatState?)null,
            };
            if (tt is null)
            {
                if (st["status"]?.ToString() == "failed")
                    _log.LogWarning("[chat/whatsapp] tin {Ma} gửi hỏng: {Loi}", maTin,
                        st["errors"]?.ToJsonString() ?? "không rõ");
                continue;
            }

            var luc = long.TryParse(st["timestamp"]?.ToString(), out var giay)
                ? DateTimeOffset.FromUnixTimeSeconds(giay).UtcDateTime : DateTime.UtcNow;
            ra.Add(new(Channel, so!, null, ChatKind.Text, null, null, luc,
                Watermark: new(tt.Value, default, maTin)));
        }
    }

    private void ReadMessages(JsonNode v, Dictionary<string, string> ten, List<InboundChatEvent> ra)
    {
        if (v["messages"] is not JsonArray ds) return;
        foreach (var m in ds.OfType<JsonNode>())
        {
            var so = m["from"]?.ToString();
            var maTin = m["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(so)) continue;

            var luc = long.TryParse(m["timestamp"]?.ToString(), out var giay)
                ? DateTimeOffset.FromUnixTimeSeconds(giay).UtcDateTime : DateTime.UtcNow;

            var kieu = m["type"]?.ToString();
            string? chu = null;
            string? att = null;
            var loai = ChatKind.Text;

            switch (kieu)
            {
                case "text":
                    chu = m["text"]?["body"]?.ToString();
                    break;
                case "image":
                case "sticker":
                case "audio":
                case "video":
                case "document":
                    // Mỗi loại gói dưới CHÍNH tên loại đó, không có trường chung — thiếu một nhánh
                    // là loại đó thành dòng trắng, đúng cái đã dính với Telegram.
                    att = m[kieu!]?.ToJsonString();
                    chu = m[kieu!]?["caption"]?.ToString();
                    loai = kieu switch
                    {
                        "image" => ChatKind.Image,
                        "sticker" => ChatKind.Sticker,
                        "audio" => ChatKind.Audio,
                        _ => ChatKind.File,
                    };
                    break;
                case "location":
                    att = m["location"]?.ToJsonString();
                    loai = ChatKind.Location;
                    break;
                case "button":
                    // Khách bấm nút của một mẫu tin. Ghi bằng CHỮ TRÊN NÚT, không phải payload.
                    chu = m["button"]?["text"]?.ToString() ?? m["button"]?["payload"]?.ToString();
                    break;
                case "interactive":
                    // Nút/danh sách trong tin tương tác — chữ hiển thị nằm ở "title".
                    var tt = m["interactive"];
                    chu = tt?["button_reply"]?["title"]?.ToString()
                          ?? tt?["list_reply"]?["title"]?.ToString();
                    break;
                default:
                    // Loại chưa hỗ trợ (đơn hàng, danh thiếp, phản ứng…): bỏ qua chứ không ghi dòng
                    // trắng. Dòng trắng vẫn đẩy hội thoại lên đầu và vẫn tính là chưa đọc.
                    continue;
            }

            if (att is null && string.IsNullOrWhiteSpace(chu)) continue;

            ra.Add(new(Channel, so!, maTin, loai, chu, att, luc,
                DisplayName: ten.GetValueOrDefault(so!)));
        }
    }

    // ── Gửi ─────────────────────────────────────────────────────────────────

    private async Task<(string? Token, string? SoId)> KhoaAsync(string tenantId, string accountId,
        CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is null) return (null, null);
        c.TryGetValue("accessToken", out var token);
        c.TryGetValue("phoneNumberId", out var soId);
        return (NullIfBlank(token), NullIfBlank(soId));
    }

    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => GuiAsync(tenantId, accountId, new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = externalUserId,
            ["type"] = "text",
            // preview_url=false: link tour dán vào tin sẽ không kéo theo một thẻ xem trước to đùng
            // che mất câu trả lời.
            ["text"] = new JsonObject { ["preview_url"] = false, ["body"] = text },
        }, ct);

    /// <summary>
    /// Gửi ảnh/tệp bằng <b>đường dẫn công khai</b> — WhatsApp tự tải về, không cần tải lên trước.
    ///
    /// <para>Chú thích gộp được vào cùng tin (giống Telegram, khác Messenger/Instagram), trừ âm
    /// thanh vì WhatsApp không nhận <c>caption</c> cho loại đó.</para>
    /// </summary>
    public Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        var kieu = loai switch
        {
            ChatKind.Image => "image",
            ChatKind.Audio => "audio",
            _ => "document",
        };
        var noi = new JsonObject { ["link"] = url };
        if (kieu != "audio" && !string.IsNullOrWhiteSpace(caption)) noi["caption"] = caption;

        return GuiAsync(tenantId, accountId, new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = externalUserId,
            ["type"] = kieu,
            [kieu] = noi,
        }, ct);
    }

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, JsonObject than,
        CancellationToken ct)
    {
        var (token, soId) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null || soId is null)
            return new(false, false, null, "Chưa khai số WhatsApp cho công ty này");

        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GraphBase}/{ApiVersion}/{U(soId)}/messages")
            {
                Content = new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + token);

            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            if (res.IsSuccessStatusCode && o?["error"] is null)
                // Mã tin nằm trong messages[0].id — đây là thứ statuses[] sẽ đối chiếu về sau.
                return new(true, false, o?["messages"]?[0]?["id"]?.ToString(), null);

            var moTa = o?["error"]?["message"]?.ToString() ?? Truncate(raw);
            return new(false, (int)res.StatusCode >= 500, null, $"WhatsApp từ chối: {moTa}");
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);
        }
    }

    // ── Tệp khách gửi ───────────────────────────────────────────────────────

    /// <summary>
    /// Đổi mã tệp thành byte thật. <b>Hai lượt gọi và CẢ HAI đều cần khoá.</b>
    ///
    /// <para>⚠️ Đây là chỗ WhatsApp khác mọi kênh khác: đường tải trả về ở lượt đầu <b>không mở
    /// công khai</b> — gọi trần vào nó là 401. Nên không có cách nào đưa thẳng cho trình duyệt,
    /// bắt buộc máy chủ tải hộ. Quên gắn khoá ở lượt hai là ảnh hỏng mà lỗi lại nằm ở tận Meta.</para>
    /// </summary>
    public async Task<(byte[] Bytes, string? Kieu)?> DownloadFileAsync(string tenantId, string accountId,
        string mediaId, CancellationToken ct)
    {
        var (token, _) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null) return null;
        try
        {
            var http = _http.CreateClient();

            using var req1 = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/{ApiVersion}/{U(mediaId)}");
            req1.Headers.Add("Authorization", "Bearer " + token);
            using var res1 = await http.SendAsync(req1, ct);
            var o = JsonNode.Parse(await res1.Content.ReadAsStringAsync(ct));
            var url = o?["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url)) return null;

            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            req2.Headers.Add("Authorization", "Bearer " + token);
            using var res2 = await http.SendAsync(req2, ct);
            if (!res2.IsSuccessStatusCode) return null;

            return (await res2.Content.ReadAsByteArrayAsync(ct),
                res2.Content.Headers.ContentType?.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/whatsapp] tải tệp {Ma} hỏng", mediaId);
            return null;
        }
    }

    // ── Năng lực kênh ───────────────────────────────────────────────────────

    /// <summary>
    /// Đánh dấu đã xem bên phía khách (hai tích xanh).
    ///
    /// <para>WhatsApp <b>không có</b> dấu "đang gõ" cho bot — nên <c>SendTypingAsync</c> để nguyên
    /// mặc định rỗng. Đừng giả lập bằng cách gửi một tin "..." rồi xoá: khách nhận thông báo đẩy
    /// cho cái tin đó.</para>
    /// </summary>
    public async Task MarkSeenAsync(string tenantId, string accountId, string maTin,
        CancellationToken ct)
    {
        var (token, soId) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null || soId is null) return;
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GraphBase}/{ApiVersion}/{U(soId)}/messages")
            {
                Content = new StringContent(new JsonObject
                {
                    ["messaging_product"] = "whatsapp",
                    ["status"] = "read",
                    ["message_id"] = maTin,
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + token);
            await http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[chat/whatsapp] đánh dấu đã xem hỏng");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
