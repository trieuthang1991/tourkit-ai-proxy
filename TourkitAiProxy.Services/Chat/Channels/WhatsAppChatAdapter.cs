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
    private const string MacDinhPhienBan = "v21.0";

    /// <summary>
    /// Trường webhook đăng ký cho WABA (<c>POST /{wabaId}/subscribed_apps</c>).
    ///
    /// <para>Thiếu <c>messages</c> là không có tin nào cả — trường này chở CẢ tin khách gửi LẪN
    /// <c>statuses[]</c> báo trạng thái, không phải hai trường riêng như Messenger.</para>
    /// </summary>
    public static readonly string[] SuKienWaba = { "messages" };

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WhatsAppChatAdapter> _log;

    public WhatsAppChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<WhatsAppChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.WhatsApp;

    private string PhienBan => Rong(_cfg["Chat:WhatsApp:Version"])
                               ?? Rong(_cfg["Chat:Messenger:Version"]) ?? MacDinhPhienBan;

    /// Khoá ký: ứng dụng Meta riêng cho WhatsApp nếu có, không thì dùng chung với Messenger.
    private string? AppSecretNenTang => Rong(_cfg["Chat:WhatsApp:AppSecret"])
                                        ?? Rong(_cfg["Chat:Messenger:AppSecret"]);

    private static string? Rong(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string U(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// <c>phone_number_id</c> của gói tin — khoá định tuyến ra công ty.
    ///
    /// <para>Nằm ở <c>entry[].changes[].value.metadata.phone_number_id</c>, KHÔNG phải
    /// <c>entry[].id</c> (chỗ đó là id WABA). Lấy nhầm là tra ra rỗng và tin rơi vào hư không.</para>
    /// </summary>
    public static string? IdSoDienThoaiCuaSuKien(string rawBody)
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

        var idSo = IdSoDienThoaiCuaSuKien(rawBody);
        var dsach = await _cred.ListAccountsAsync(tenantId, Channel, ct);

        foreach (var tk in dsach)
        {
            var khop = accountIdTuUrl is { Length: > 0 }
                ? string.Equals(tk.AccountId, accountIdTuUrl, StringComparison.OrdinalIgnoreCase)
                : idSo is { Length: > 0 } && tk.GiaTri.GetValueOrDefault("phoneNumberId", "") == idSo;
            if (!khop) continue;

            var bimat = Rong(tk.GiaTri.GetValueOrDefault("appSecret", "")) ?? AppSecretNenTang;
            if (bimat is null) continue;
            if (KyDung(bimat, rawBody, ky!)) return tk.AccountId;

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
    public async Task<(string TenantId, string AccountId)?> XacMinhDungChungAsync(string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        if (IdSoDienThoaiCuaSuKien(rawBody) is not { } khoa) return null;
        var tenant = await _cred.TimTenantAsync(Channel, khoa, ct);
        if (tenant is null)
        {
            _log.LogWarning("[chat/whatsapp] nhận tin của {Khoa} nhưng chưa công ty nào nối", khoa);
            return null;
        }
        return await VerifyAsync(tenant, khoa, rawBody, headers, ct) is { } tk ? (tenant, tk) : null;
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

                BocTrangThai(v, ra);
                BocTin(v, ten, ra);
            }
        }
        return ra;
    }

    /// <summary>
    /// <c>statuses[]</c>: WhatsApp báo trạng thái theo <b>mã từng tin</b>, không theo mốc nước.
    ///
    /// <para>Đi chung đường với Instagram: chuyển mã tin sang cho lõi tra ra thời điểm. Lấy tạm giờ
    /// nhận gói là đánh dấu THỪA lên mọi tin gửi trước đó, kể cả tin khách chưa hề mở.</para>
    ///
    /// <para><c>failed</c> KHÔNG map sang trạng thái hỏng: tin đã rời khỏi mình rồi, và luật
    /// <c>ChatRules.KhongLui</c> vốn chặn việc tin gửi được lại thành hỏng. Ghi log để còn tra.</para>
    /// </summary>
    private void BocTrangThai(JsonNode v, List<InboundChatEvent> ra)
    {
        if (v["statuses"] is not JsonArray ds) return;
        foreach (var st in ds.OfType<JsonNode>())
        {
            var maTin = st["id"]?.ToString();
            var so = st["recipient_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(maTin) || string.IsNullOrWhiteSpace(so)) continue;

            var tt = st["status"]?.ToString() switch
            {
                "sent" => ChatState.DaGui,
                "delivered" => ChatState.DaNhan,
                "read" => ChatState.DaXem,
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
            ra.Add(new(Channel, so!, null, ChatKind.Chu, null, null, luc,
                Watermark: new(tt.Value, default, maTin)));
        }
    }

    private void BocTin(JsonNode v, Dictionary<string, string> ten, List<InboundChatEvent> ra)
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
            var loai = ChatKind.Chu;

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
                        "image" => ChatKind.Anh,
                        "sticker" => ChatKind.Sticker,
                        "audio" => ChatKind.AmThanh,
                        _ => ChatKind.Tep,
                    };
                    break;
                case "location":
                    att = m["location"]?.ToJsonString();
                    loai = ChatKind.ViTri;
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
        return (Rong(token), Rong(soId));
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
            ChatKind.Anh => "image",
            ChatKind.AmThanh => "audio",
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
                $"{GraphBase}/{PhienBan}/{U(soId)}/messages")
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

            var moTa = o?["error"]?["message"]?.ToString() ?? Cat(raw);
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
    public async Task<(byte[] Bytes, string? Kieu)?> TaiTepAsync(string tenantId, string accountId,
        string mediaId, CancellationToken ct)
    {
        var (token, _) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null) return null;
        try
        {
            var http = _http.CreateClient();

            using var req1 = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/{PhienBan}/{U(mediaId)}");
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
    /// <para>WhatsApp <b>không có</b> dấu "đang gõ" cho bot — nên <c>BaoDangGoAsync</c> để nguyên
    /// mặc định rỗng. Đừng giả lập bằng cách gửi một tin "..." rồi xoá: khách nhận thông báo đẩy
    /// cho cái tin đó.</para>
    /// </summary>
    public async Task BaoDaXemAsync(string tenantId, string accountId, string maTin,
        CancellationToken ct)
    {
        var (token, soId) = await KhoaAsync(tenantId, accountId, ct);
        if (token is null || soId is null) return;
        try
        {
            var http = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GraphBase}/{PhienBan}/{U(soId)}/messages")
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

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];
}
