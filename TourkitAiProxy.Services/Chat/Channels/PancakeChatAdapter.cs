// Services/Chat/Channels/PancakeChatAdapter.cs
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Pancake (pages.fm) — <b>cổng gom</b>, không phải một nền tảng nhắn tin.
///
/// <para>Khác mọi kênh còn lại ở một điểm nền tảng: Pancake không sở hữu người dùng cuối. Nó đứng
/// giữa, gom hộp thư Facebook · Instagram · TikTok · Zalo của khách hàng về một chỗ, rồi cho mình
/// đọc và trả lời qua API của nó. Nên một "tài khoản" ở đây là MỘT TRANG trong Pancake, và trang
/// đó thuộc nền tảng nào thì nằm ở trường <c>platform</c> khi liệt kê trang.</para>
///
/// <para><b>Có webhook đẩy tin thật</b> (sự kiện <c>messaging</c>) — không phải đi hỏi theo chu kỳ.
/// Tin mới hiện lên ngay như Zalo. Hai API <c>GET …/messages</c> và <c>GET …/conversations</c> chỉ
/// dùng để kéo LỊCH SỬ lúc vừa nối.</para>
///
/// <para><b>⚠️ Pancake KHÔNG ký nội dung webhook.</b> Đặc tả của họ
/// (<c>developer.pancake.biz/openapi/webhook.yaml</c>) không có chữ ký, không có khoá bí mật, không
/// có header xác thực nào — chỉ là một lượt POST trần tới địa chỉ mình khai. Nên ai biết địa chỉ
/// cũng bơm được tin giả vào hộp thư. Ở đây chặn bằng HAI lớp:</para>
/// <list type="number">
///   <item><b>Đoạn bí mật trên chính URL</b> (máy chủ sinh, 32 byte) — giống hệt cách Telegram làm,
///     vì Telegram cũng không ký nội dung.</item>
///   <item><b>Đối chiếu <c>page_id</c> trong gói tin với trang đã khai</b> — gói nào nói về một
///     trang chưa ai nối thì bỏ, kể cả khi đoạn bí mật đúng.</item>
/// </list>
///
/// <para><b>⚠️ Dùng CHUNG một URL cho mọi trang.</b> Pancake khai webhook trong phần Công cụ của
/// TỪNG trang (không có API khai hộ — đã tra cả hai đặc tả), nên nối 5 trang là dán 5 lần. Dùng
/// chung một chuỗi thì người khai copy một lần dán nhiều lần; định tuyến ra công ty dựa vào
/// <c>page_id</c>, y như Zalo dựa vào id OA. URL riêng từng trang không an toàn hơn đáng kể vì
/// đằng nào cũng phải đối chiếu <c>page_id</c> mới tin được gói tin.</para>
///
/// <para><b>⚠️ Pancake trả lỗi bằng HTTP 200.</b> Hỏng hay không nằm ở <c>success: false</c> trong
/// thân, không nằm ở mã HTTP. Đọc mã HTTP để quyết định thử lại là luôn thấy "thành công" — tin
/// biến mất mà hàng đợi báo đã gửi xong.</para>
///
/// <para><b>⚠️ Bật webhook cho mỗi trang ăn thêm 1 connection slot</b> trong gói Pancake của khách,
/// và Pancake <b>tự ngắt</b> webhook khi trong 30 phút tỉ lệ lỗi vượt 80% hoặc có từ 300 lượt hỏng
/// — ngắt rồi phải vào Pancake bật lại tay. Vì thế đường nhận tin phải trả 200 thật nhanh rồi xử lý
/// nền (lõi đã làm đúng vậy: ghi hàng đợi rồi trả về ngay).</para>
/// </summary>
public class PancakeChatAdapter : IChatChannelAdapter
{
    /// API cấp TÀI KHOẢN (liệt kê trang, sinh khoá trang). Chỉ dùng lúc khai.
    private const string UserApi = "https://pages.fm/api/v1";
    /// API cấp TRANG (hội thoại, tin nhắn) — phần dùng hằng ngày.
    private const string PageApiV1 = "https://pages.fm/api/public_api/v1";
    private const string PageApiV2 = "https://pages.fm/api/public_api/v2";

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly ILogger<PancakeChatAdapter> _log;

    public PancakeChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        ILogger<PancakeChatAdapter> log)
    { _http = http; _cred = cred; _log = log; }

    public ChatChannel Channel => ChatChannel.Pancake;

    // ── Khai kết nối: một token tài khoản → nhiều trang ──────────────────────

    /// <param name="Platform">Nền tảng thật của trang (facebook · instagram · tiktok · zalo…).
    /// Giữ lại vì nó quyết định hạn trả lời: Meta 24 giờ, Zalo 48 giờ.</param>
    /// <param name="PageAccessToken">Khoá trang ĐÃ CÓ SẴN, nếu lấy được. Người khai dán thẳng khoá
    /// trang, hoặc Pancake trả kèm trong <c>settings.page_access_token</c> khi chủ token là quản
    /// trị. Có sẵn thì KHÔNG sinh mới — sinh mới là huỷ khoá cũ (xem
    /// <see cref="GeneratePageTokenAsync"/>).</param>
    public record PancakePage(string Id, string Name, string? Platform, string? PageAccessToken = null);

    /// <summary>
    /// Đọc mã trang nằm trong chính <b>khoá trang</b>. Khoá trang là một JWT mà phần tải mang
    /// <c>{"id":"&lt;mã trang&gt;"}</c>, nên dán khoá trang vào là biết ngay nó của trang nào —
    /// không phải hỏi Pancake, và cũng <b>không có</b> đường nào hỏi: đặc tả không có endpoint tra
    /// thông tin một trang bằng khoá trang.
    ///
    /// <para>Trả <c>null</c> khi chuỗi không phải JWT ba phần, hoặc phần tải không có <c>id</c> —
    /// tức là người khai dán khoá TÀI KHOẢN, và chỗ gọi phải rẽ sang đường liệt kê trang.</para>
    ///
    /// <para><b>Chỉ để nhận dạng, không phải để tin.</b> Phần tải JWT ai cũng đọc và sửa được;
    /// chữ ký không kiểm ở đây. Mã trang đọc ra luôn phải đem xác minh lại bằng một lượt gọi thật
    /// (<see cref="VerifyPageTokenAsync"/>) trước khi lưu.</para>
    /// </summary>
    public static string? DocPageIdTuToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var phan = token!.Trim().Split('.');
        if (phan.Length != 3) return null;
        try
        {
            var p = phan[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');   // bù '=' base64url đã cắt
            var o = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)))?.AsObject();
            var id = o?["id"]?.ToString();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch { return null; }
    }

    /// <summary>
    /// Thử khoá trang bằng một lượt gọi thật, nhẹ nhất có thể: lấy danh sách thẻ hội thoại.
    ///
    /// <para>Bắt buộc phải gọi: <see cref="DocPageIdTuToken"/> chỉ đọc chữ trong token chứ không
    /// chứng minh được token còn sống. Bỏ bước này thì khoá sai/đã huỷ vẫn lưu được, và người khai
    /// chỉ biết mình nối hỏng vào lúc khách nhắn tới mà không ai trả lời.</para>
    /// </summary>
    public async Task<string?> VerifyPageTokenAsync(string pageId, string pageAccessToken,
        CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var res = await http.GetAsync(
                $"{PageApiV1}/pages/{Uri.EscapeDataString(pageId)}/tags"
                + $"?page_access_token={Uri.EscapeDataString(pageAccessToken)}", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            JsonObject? o = null;
            try { o = JsonNode.Parse(raw)?.AsObject(); } catch { /* thân không phải JSON */ }

            // Pancake báo hỏng bằng HTTP 200 + success:false, nên phải soi thân chứ không soi mã.
            if (o?["success"]?.GetValue<bool>() == false)
                return MoTaLoi(o) ?? "Pancake từ chối khoá trang này";
            if (!res.IsSuccessStatusCode)
                return MoTaLoi(o) ?? $"Pancake trả HTTP {(int)res.StatusCode}";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Liệt kê MỌI trang mà chủ token quản lý, để người khai tick chọn.
    /// <para>Token tài khoản <b>hết hạn sau 90 ngày</b> (hoặc khi đăng xuất) và mở được mọi trang
    /// của người đó, nên nó chỉ đi qua đây một lần rồi thôi — <b>không lưu</b>.</para>
    /// </summary>
    public async Task<(IReadOnlyList<PancakePage> Trang, string? Loi)> ListPagesAsync(
        string userAccessToken, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var res = await http.GetAsync(
                $"{UserApi}/pages?access_token={Uri.EscapeDataString(userAccessToken)}", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            if (o?["success"]?.GetValue<bool>() == false)
                return (Array.Empty<PancakePage>(), MoTaLoi(o) ?? "Pancake từ chối token tài khoản");

            var ra = DocDanhSachTrang(raw);

            if (ra.Count == 0 && !res.IsSuccessStatusCode)
                return (ra, $"Pancake trả HTTP {(int)res.StatusCode}");
            return (ra, null);
        }
        catch (Exception ex) { return (Array.Empty<PancakePage>(), ex.Message); }
    }

    /// <summary>
    /// Đọc danh sách trang từ thân trả về của <c>GET /pages</c> — <b>hàm thuần</b>, tách khỏi lượt
    /// gọi mạng để test được.
    ///
    /// <para>Pancake gói danh sách dưới <c>categorized</c> (nhóm theo nền tảng) <b>HOẶC</b>
    /// <c>pages</c> (mảng phẳng) tuỳ phiên bản. Đọc cả hai: đoán một kiểu thì bản kia ra danh sách
    /// rỗng <b>mà không có lỗi nào</b>, và người khai tưởng tài khoản mình không quản lý trang nào.</para>
    /// </summary>
    public static IReadOnlyList<PancakePage> DocDanhSachTrang(string? rawJson)
    {
        var ra = new List<PancakePage>();
        if (string.IsNullOrWhiteSpace(rawJson)) return ra;
        JsonObject? o;
        try { o = JsonNode.Parse(rawJson!)?.AsObject(); } catch { return ra; }

        foreach (var nguon in new[] { o?["categorized"], o?["pages"], o?["data"] })
            GomTrang(nguon, ra);
        return ra;
    }

    /// <summary>
    /// Gom trang từ một nhánh JSON bất kỳ (mảng trang, hoặc object nhóm theo nền tảng).
    ///
    /// <para><b>Chỉ nhận phần tử là OBJECT.</b> Nhóm <c>activated_page_ids</c> trong trả về của
    /// Pancake là mảng CHUỖI mã trang, không phải mảng object — mà đánh chỉ mục <c>["id"]</c> lên
    /// một <see cref="JsonValue"/> thì .NET ném <c>InvalidOperationException</c>, và cả lượt dò
    /// trang hỏng theo dù token hoàn toàn đúng.</para>
    /// </summary>
    private static void GomTrang(JsonNode? nut, List<PancakePage> ra)
    {
        switch (nut)
        {
            case JsonArray mang:
                foreach (var t in mang)
                {
                    if (t is not JsonObject tr) continue;    // bỏ mảng chuỗi (activated_page_ids)
                    var id = tr["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (ra.Any(x => x.Id == id)) continue;   // hai nhánh có thể trả trùng
                    ra.Add(new(id!, tr["name"]?.ToString() ?? id!, tr["platform"]?.ToString(),
                        // Chủ token là quản trị thì Pancake trả kèm khoá trang sẵn có ở đây. Lấy
                        // được thì khỏi sinh mới — sinh mới là huỷ khoá cũ của trang đó.
                        (tr["settings"] as JsonObject)?["page_access_token"]?.ToString()));
                }
                break;
            case JsonObject obj:
                foreach (var kv in obj) GomTrang(kv.Value, ra);
                break;
        }
    }

    /// <summary>
    /// Sinh <c>page_access_token</c> cho một trang.
    ///
    /// <para><b>⚠️ Lệnh này HUỶ token cũ của trang đó</b> — đặc tả Pancake nói thẳng
    /// "regenerating invalidates the old token". Trang nào đang có token dùng cho việc khác (phần
    /// mềm khác, hay chính tích hợp Pancake bên web TourKit đang lưu "Access Token Page") thì sinh
    /// mới ở đây là <b>làm chết tích hợp kia</b>. Vì vậy chỗ gọi phải cho người khai lối dán lại
    /// token sẵn có thay vì luôn sinh mới.</para>
    /// </summary>
    public async Task<(string? Token, string? Loi)> GeneratePageTokenAsync(
        string userAccessToken, string pageId, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var res = await http.PostAsync(
                $"{UserApi}/pages/{Uri.EscapeDataString(pageId)}/generate_page_access_token"
                + $"?access_token={Uri.EscapeDataString(userAccessToken)}", null, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            var token = o?["page_access_token"]?.ToString()
                     ?? o?["data"]?["page_access_token"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token)) return (token, null);

            return (null, MoTaLoi(o) ?? $"Pancake không trả token (HTTP {(int)res.StatusCode})");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Sinh đoạn bí mật cho URL webhook. <b>Máy chủ sinh</b>, người khai không tự nghĩ — vì nó là
    /// thứ DUY NHẤT ngăn người ngoài bơm tin (Pancake không ký nội dung). Chỉ chữ–số–gạch để dán
    /// vào ô URL của Pancake không bị mã hoá lại.
    /// </summary>
    public static string SinhChuoiBiMat()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                  .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ── Nhận tin ─────────────────────────────────────────────────────────────

    /// <summary>Bóc <c>page_id</c> khỏi gói webhook — khoá định tuyến ra công ty.</summary>
    public static string? PageIdOfEvent(string rawBody)
    {
        try { return JsonNode.Parse(rawBody)?["page_id"]?.ToString(); }
        catch { return null; }
    }

    /// <summary>
    /// Tra ra (công ty, trang) cho một gói webhook đến đường dùng chung, và kiểm đoạn bí mật.
    ///
    /// <para>Trả <c>null</c> khi: gói không có <c>page_id</c> (Pancake gọi thử lúc lưu URL), trang
    /// chưa công ty nào nối, hoặc đoạn bí mật không khớp.</para>
    /// </summary>
    public async Task<(string TenantId, string AccountId)?> ResolveSharedWebhookAsync(
        string rawBody, string? chuoiBiMatTuUrl, CancellationToken ct)
    {
        var pageId = PageIdOfEvent(rawBody);
        if (string.IsNullOrWhiteSpace(pageId)) return null;

        var tenant = await _cred.FindTenantAsync(Channel, pageId!, ct);
        if (tenant is null) return null;

        var c = await _cred.GetAsync(tenant, Channel, pageId!, ct);
        if (c is null || !c.TryGetValue("webhookSecret", out var mong) || string.IsNullOrWhiteSpace(mong))
        {
            _log.LogWarning("[chat/pancake] tenant={T} page={P} chưa có webhookSecret — bỏ gói tin",
                tenant, pageId);
            return null;
        }
        if (string.IsNullOrWhiteSpace(chuoiBiMatTuUrl)) return null;

        var a = Encoding.UTF8.GetBytes(mong);
        var b = Encoding.UTF8.GetBytes(chuoiBiMatTuUrl!);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            _log.LogWarning("[chat/pancake] TỪ CHỐI tin của trang {P}: đoạn bí mật trên URL sai", pageId);
            return null;
        }
        return (tenant, pageId!);
    }

    /// <summary>
    /// Đường webhook dùng chung nên <paramref name="accountIdTuUrl"/> mang ĐOẠN BÍ MẬT chứ không
    /// phải mã trang — xem <see cref="ResolveSharedWebhookAsync"/>, nơi làm cả việc tra công ty.
    /// </summary>
    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
        => (await ResolveSharedWebhookAsync(rawBody, accountIdTuUrl, ct))?.AccountId;

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }
        if (goc is null) return ra;

        // Chỉ sự kiện "messaging" mang tin nhắn. "conversation" chỉ báo hội thoại đổi (phân công,
        // nhãn, đã xem) — dựng nó thành tin là hộp thư đầy dòng trắng và hội thoại cứ nhảy lên đầu.
        var loaiSuKien = goc["event_type"]?.ToString();
        if (!string.IsNullOrWhiteSpace(loaiSuKien)
            && !string.Equals(loaiSuKien, "messaging", StringComparison.OrdinalIgnoreCase))
            return ra;

        var m = goc["data"]?["message"];
        if (m is null) return ra;

        var pageId = goc["page_id"]?.ToString();
        var hoiThoai = goc["data"]?["conversation"];

        // NGƯỜI GỬI. Gói tin không có cờ "của khách hay của trang" — phải so id người gửi với
        // id trang. Bỏ bước này thì tin nhân viên trả lời từ chính app Pancake quay về thành tin
        // của khách, và trợ lý sẽ trả lời đè lên người thật.
        var fromId = m["from"]?["id"]?.ToString();
        var laTiengVong = !string.IsNullOrWhiteSpace(pageId)
                       && string.Equals(fromId, pageId, StringComparison.Ordinal);

        // KHÁCH là ai: tin của khách thì lấy người gửi; tiếng vọng thì người gửi là trang, phải
        // lấy khách từ hội thoại.
        var khachId = laTiengVong ? hoiThoai?["from"]?["id"]?.ToString() : fromId;
        if (string.IsNullOrWhiteSpace(khachId)) return ra;

        // INBOX = nhắn riêng · COMMENT/COMMENT_LIVESTREAM = bình luận công khai dưới bài.
        // Phân biệt vì nó quyết định trợ lý có được tự trả lời không.
        var loaiTin = (m["type"]?.ToString() ?? hoiThoai?["type"]?.ToString() ?? "INBOX").ToUpperInvariant();
        var congKhai = loaiTin.StartsWith("COMMENT", StringComparison.Ordinal);

        var chu = m["message"]?.ToString() ?? m["original_message"]?.ToString();
        var dinhKem = m["attachments"] as JsonArray;
        var coDinhKem = dinhKem is { Count: > 0 };

        if (!coDinhKem && string.IsNullOrWhiteSpace(chu))
        {
            // Không nhận ra thì BỎ QUA, đừng ghi dòng trắng — dòng trắng vẫn đẩy hội thoại lên đầu
            // và vẫn tính chưa đọc, nhân viên mở ra không thấy gì. Ghi log để còn biết mà bổ sung.
            _log.LogWarning("[chat/pancake] gói không có chữ lẫn đính kèm, bỏ qua. Trường: {Truong}",
                string.Join(", ", m.AsObject().Select(x => x.Key)));
            return ra;
        }

        var kind = coDinhKem ? DoanLoai(dinhKem!) : ChatKind.Text;

        ra.Add(new(
            ChatChannel.Pancake,
            khachId!,
            m["id"]?.ToString(),
            kind,
            chu,
            coDinhKem ? dinhKem!.ToJsonString() : null,
            DocGio(m["inserted_at"]?.ToString()),
            IsEcho: laTiengVong,
            DisplayName: (laTiengVong ? hoiThoai?["from"]?["name"] : m["from"]?["name"])?.ToString(),
            Surface: congKhai ? ChatSurface.Comment : ChatSurface.DirectMessage,
            ThreadId: congKhai ? goc["data"]?["post"]?["id"]?.ToString() : null,
            ParentExternalId: congKhai ? m["parent_id"]?.ToString() : null));
        return ra;
    }

    /// <summary>
    /// Đoán loại đính kèm. Pancake đặt loại trong <c>attachments[].type</c>, nhưng chuỗi khác nhau
    /// theo nền tảng gốc (Facebook "photo", TikTok "video"…) nên khớp theo tiền tố, không khớp
    /// cứng cả chuỗi.
    /// </summary>
    private static ChatKind DoanLoai(JsonArray dinhKem)
    {
        var t = (dinhKem[0]?["type"]?.ToString() ?? "").ToLowerInvariant();
        if (t.Contains("photo") || t.Contains("image")) return ChatKind.Image;
        if (t.Contains("audio") || t.Contains("voice")) return ChatKind.Audio;
        if (t.Contains("sticker")) return ChatKind.Sticker;
        return ChatKind.File;
    }

    /// <summary>
    /// Giờ Pancake gửi dạng <c>2024-12-25T11:06:07.000000</c> — <b>không có hậu tố múi giờ</b>.
    /// Không ép Utc thì .NET coi là giờ máy chủ, và hộp thư hiện lệch đúng bằng chênh lệch múi giờ.
    /// </summary>
    private static DateTime DocGio(string? s)
        => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal
               | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
           ? d : DateTime.UtcNow;

    // ── Gửi tin ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="externalUserId"/> ở kênh này là <b>mã hội thoại</b>, không phải mã khách:
    /// API gửi của Pancake nhận <c>conversation_id</c>. Lõi vốn gọi mọi kênh bằng "mã khách", nên
    /// đây là chỗ hai bên khác khái niệm — hội thoại Pancake được lưu với mã khách = mã hội thoại
    /// để hai đầu khớp nhau.
    /// </summary>
    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => GuiAsync(tenantId, accountId, externalUserId, new { message = text }, ct);

    /// <summary>
    /// Pancake <b>không nhận URL ảnh trực tiếp</b> ở lệnh gửi tin: ảnh phải tải lên trước rồi gửi
    /// bằng <c>content_ids</c>, và <c>message</c> với <c>content_ids</c> loại trừ nhau. Chưa làm
    /// bước tải lên thì gửi chữ kèm đường dẫn còn hơn báo thành công mà khách không nhận được gì.
    /// </summary>
    public Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
        => GuiAsync(tenantId, accountId, externalUserId,
            new { message = string.IsNullOrWhiteSpace(caption) ? url : caption + "\n" + url }, ct);

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, string conversationId,
        object body, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        var token = c is not null && c.TryGetValue("pageAccessToken", out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(token))
            return new(false, false, null, "Chưa khai Page Access Token Pancake cho trang này");

        try
        {
            var http = _http.CreateClient();
            var url = $"{PageApiV1}/pages/{Uri.EscapeDataString(accountId)}"
                    + $"/conversations/{Uri.EscapeDataString(conversationId)}/messages"
                    + $"?page_access_token={Uri.EscapeDataString(token!)}";
            using var res = await http.PostAsJsonAsync(url, body, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            // ⚠️ Pancake trả lỗi bằng HTTP 200 + success:false. Tin vào mã HTTP là coi mọi lượt
            // hỏng thành công, tin biến mất mà hàng đợi báo đã gửi.
            var ok = o?["success"]?.GetValue<bool>();
            if (ok == true)
                return new(true, false, o?["message_id"]?.ToString()
                                     ?? o?["data"]?["id"]?.ToString(), null);

            var moTa = MoTaLoi(o) ?? Cat(raw);
            return SendResult.Fail(DoanLoiPancake(o, (int)res.StatusCode), $"Pancake từ chối: {moTa}");
        }
        catch (Exception ex) { return SendResult.Fail(ChatFailure.Network, ex.Message); }
    }

    /// <summary>
    /// Quy lỗi Pancake về nhóm chung. Mã 102 = token sai/đã bị sinh lại — đây là ca nguy hiểm nhất
    /// vì trên màn hình nó trông y hệt một tin gửi lỗi bình thường, trong khi thật ra cả trang đã
    /// mất đường gửi và phải vào khai lại.
    /// </summary>
    private static ChatFailure DoanLoiPancake(JsonObject? o, int httpStatus)
    {
        var ma = o?["error_code"]?.ToString() ?? o?["code"]?.ToString();
        if (ma == "102") return ChatFailure.AuthFailed;
        return ChannelFailures.FromHttp(httpStatus == 200 ? 400 : httpStatus);
    }

    private static string? MoTaLoi(JsonObject? o)
        => o?["message"]?.ToString() ?? o?["error"]?.ToString() ?? o?["error_description"]?.ToString();

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];

    // ── Kéo lịch sử (dùng lúc vừa nối) ───────────────────────────────────────

    /// <summary>Một trang hội thoại kèm con trỏ sang trang kế. Xem <see cref="ListConversationsAsync"/>.</summary>
    public record TrangHoiThoai(IReadOnlyList<JsonNode> HoiThoai, string? ConTroTiep);

    /// <summary>
    /// Liệt kê hội thoại của một trang — <b>60 cái mỗi lượt</b>, phân trang bằng mã hội thoại cuối
    /// (<c>last_conversation_id</c>) chứ không phải số trang. Chỉ dùng để kéo lịch sử lúc vừa nối;
    /// hộp thư hằng ngày đọc từ CSDL của mình, không gọi sang đây.
    /// </summary>
    public async Task<TrangHoiThoai> ListConversationsAsync(string tenantId, string accountId,
        string? conTro, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        var token = c is not null && c.TryGetValue("pageAccessToken", out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(token)) return new(Array.Empty<JsonNode>(), null);

        try
        {
            var http = _http.CreateClient();
            var url = $"{PageApiV2}/pages/{Uri.EscapeDataString(accountId)}/conversations"
                    + $"?page_access_token={Uri.EscapeDataString(token!)}";
            if (!string.IsNullOrWhiteSpace(conTro))
                url += $"&last_conversation_id={Uri.EscapeDataString(conTro!)}";

            using var res = await http.GetAsync(url, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
            var ds = (o?["conversations"] ?? o?["data"]) as JsonArray;
            if (ds is null || ds.Count == 0) return new(Array.Empty<JsonNode>(), null);

            var ra = ds.Where(x => x is not null).Select(x => x!).ToList();
            return new(ra, ra[^1]["id"]?.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/pancake] kéo danh sách hội thoại lỗi tenant={T} page={P}",
                tenantId, accountId);
            return new(Array.Empty<JsonNode>(), null);
        }
    }
}
