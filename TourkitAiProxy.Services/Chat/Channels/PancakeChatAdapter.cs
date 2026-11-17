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

        var pageId = goc["page_id"]?.ToString();
        var hoiThoai = goc["data"]?["conversation"];

        // Webhook thật gửi MỘT tin (data.message). Lượt kéo lịch sử dựng gói cùng hình dạng nhưng
        // mang CẢ TRANG tin (data.messages) — xem PancakeHistoryImporter. Đi chung một đường đọc là
        // cố ý: tin cũ và tin mới phải ra cùng một kết quả, nếu không thì mỗi lỗi phải sửa hai nơi
        // và hai nơi đó sẽ trôi xa nhau.
        // Gói do PancakeHistoryImporter dựng mang cờ này. Phải chuyển xuống từng tin: lõi dùng nó
        // để KHÔNG sinh câu trả lời của trợ lý, KHÔNG cho bot câm, và giữ đúng giờ gửi gốc.
        var laLichSu = goc["tourkit_lich_su"]?.GetValue<bool>() == true;

        if (goc["data"]?["messages"] is JsonArray nhieuTin)
        {
            foreach (var t in nhieuTin) MotTin(goc, t, pageId, hoiThoai, laLichSu, ra);
            return ra;
        }

        MotTin(goc, goc["data"]?["message"], pageId, hoiThoai, laLichSu, ra);
        return ra;
    }

    /// <summary>Đọc MỘT tin trong gói (webhook thật hoặc gói lịch sử) và xếp vào <paramref name="ra"/>.</summary>
    private void MotTin(JsonNode goc, JsonNode? m, string? pageId, JsonNode? hoiThoai,
        bool laLichSu, List<InboundChatEvent> ra)
    {
        if (m is null) return;

        // NGƯỜI GỬI. Gói tin không có cờ "của khách hay của trang" — phải so id người gửi với
        // id trang. Bỏ bước này thì tin nhân viên trả lời từ chính app Pancake quay về thành tin
        // của khách, và trợ lý sẽ trả lời đè lên người thật.
        var fromId = m["from"]?["id"]?.ToString();
        var laTiengVong = !string.IsNullOrWhiteSpace(pageId)
                       && string.Equals(fromId, pageId, StringComparison.Ordinal);

        // "Mã khách" ở kênh này là MÃ HỘI THOẠI, không phải mã người gửi — xem SendTextAsync: API
        // gửi của Pancake nhận conversation_id. Lấy mã người gửi là câu trả lời đi tới một hội
        // thoại không tồn tại và KHÔNG BAO GIỜ tới khách, mà hộp thư vẫn báo đã gửi.
        //
        // Hai mã này thật sự khác nhau, không phải lo xa: đo trên dữ liệu thật 05/11/2026, cả 3/3
        // hội thoại đều lệch — hội thoại pzl_u_490605425456992929_3510358233866837464 có người gửi
        // pzl_u_3510358233866837464.
        //
        // Còn một hệ quả nữa: chat NHÓM (Zalo group) có nhiều người gửi khác nhau trong cùng một
        // hội thoại. Khoá theo người gửi thì một nhóm vỡ thành nhiều hội thoại rời trong hộp thư.
        var khachId = hoiThoai?["id"]?.ToString() ?? m["conversation_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(khachId)) return;

        // INBOX = nhắn riêng · COMMENT/COMMENT_LIVESTREAM = bình luận công khai dưới bài.
        // Phân biệt vì nó quyết định trợ lý có được tự trả lời không.
        var loaiTin = (m["type"]?.ToString() ?? hoiThoai?["type"]?.ToString() ?? "INBOX").ToUpperInvariant();
        var congKhai = loaiTin.StartsWith("COMMENT", StringComparison.Ordinal);

        // original_message TRƯỚC, message sau. Pancake trả `message` đã DỰNG HTML — đo trên dữ liệu
        // thật 05/11/2026: 30/30 tin có thẻ, câu "đúng rồi" về thành "<div>đúng rồi</div>". Đọc
        // `message` trước là hộp thư hiện nguyên thẻ cho nhân viên đọc, và trợ lý cũng học theo đó.
        // Thứ tự ngược lại (bản đầu) không lộ ra ở webhook vì gói webhook thường mang chữ trần.
        var chu = m["original_message"]?.ToString() is { Length: > 0 } tho
            ? tho
            : BoThe(m["message"]?.ToString());
        chu = ThayNhacTen(chu, m["message_tags"] as JsonArray);
        // CHỈ giữ phần tử thật sự là TỆP. Mảng attachments của Pancake còn chở tin trích dẫn, thả
        // cảm xúc, thông báo hệ thống và cả định dạng chữ in đậm — xem ChatAttachment.LocTepPancake.
        // Lọc chung một chỗ với màn hiển thị, để tin không bị phân loại một kiểu rồi hiện một kiểu.
        var dinhKem = m["attachments"] as JsonArray;
        var tep = ChatAttachment.LocTepPancake(dinhKem);
        var coDinhKem = tep.Count > 0;

        // Hai loại bị lọc khỏi danh sách TỆP nhưng vẫn phải hiện ra, vì bên Pancake có:
        //   · thông báo nền tảng ("X đã rời khỏi nhóm") — chữ rỗng, phải tự dựng câu;
        //   · thẻ ngân hàng Zalo — số tài khoản là thứ nhân viên cần đọc và chép.
        // Chỉ dựng khi tin KHÔNG có chữ sẵn: thẻ/thông báo luôn đi một mình trong ca thật, mà nếu
        // có chữ thật thì chữ đó mới là nội dung chính, đừng đè lên.
        var laThongBao = false;
        if (string.IsNullOrWhiteSpace(chu) && dinhKem is not null)
            foreach (var a in dinhKem.OfType<JsonObject>())
            {
                if (MoTaThongBao(a) is { Length: > 0 } cauTb) { chu = cauTb; laThongBao = true; break; }
                if (MoTaTheNganHang(a) is { Length: > 0 } cauTk) { chu = cauTk; break; }
            }

        if (!coDinhKem && string.IsNullOrWhiteSpace(chu))
        {
            // Không nhận ra thì BỎ QUA, đừng ghi dòng trắng — dòng trắng vẫn đẩy hội thoại lên đầu
            // và vẫn tính chưa đọc, nhân viên mở ra không thấy gì. Ghi log để còn biết mà bổ sung.
            // Ghi loại attachment đã bị lọc, không ghi tên trường: ca thường gặp nhất là tin CHỈ
            // có một lượt thả cảm xúc, và biết đúng loại thì khỏi phải đi dò lại từ đầu.
            _log.LogDebug("[chat/pancake] gói không có chữ lẫn tệp, bỏ qua. Loại đính kèm: {Loai}",
                dinhKem is null ? "(không có)"
                    : string.Join(", ", dinhKem.OfType<JsonObject>()
                        .Select(x => x["type"]?.ToString() ?? "?").Distinct()));
            return;
        }

        // Lấy loại từ danh sách ĐÃ LỌC: lấy từ mảng gốc thì một tin chữ kèm styled_message bị đoán
        // thành tin tệp, và hộp thư hiện một đính kèm rỗng không bấm được.
        var kind = coDinhKem ? DoanLoai(tep) : ChatKind.Text;

        ra.Add(new(
            ChatChannel.Pancake,
            khachId!,
            m["id"]?.ToString(),
            kind,
            chu,
            // Lưu bản ĐÃ LỌC, không lưu mảng gốc: mảng gốc chở cả tin trích dẫn và nhật ký cuộc
            // gọi, có cái dài hàng nghìn ký tự — phình cột attachment mà không dùng tới.
            coDinhKem ? new JsonArray(tep.Select(x => x.DeepClone()).ToArray()).ToJsonString() : null,
            DocGio(m["inserted_at"]?.ToString()),
            IsEcho: laTiengVong,
            // Tên hội thoại TRƯỚC tên người gửi: dòng trong hộp thư nay là một HỘI THOẠI, nên
            // nhóm Zalo phải mang tên nhóm ("SUPPORT TOURKIT - HAPPY TRIP"), không mang tên người
            // vừa nhắn — nếu không thì tên dòng đổi theo từng tin, tuỳ ai trong nhóm nói sau cùng.
            DisplayName: (hoiThoai?["from"]?["name"] ?? m["from"]?["name"])?.ToString(),
            Surface: congKhai ? ChatSurface.Comment : ChatSurface.DirectMessage,
            ThreadId: congKhai ? goc["data"]?["post"]?["id"]?.ToString() : null,
            ParentExternalId: congKhai ? m["parent_id"]?.ToString() : null,
            IsHistory: laLichSu,
            // Tên người gửi RIÊNG của tin này — chỉ ghi khi nó KHÁC tên hội thoại, tức là hội
            // thoại nhiều người (nhóm Zalo). Ở hội thoại một-một thì người gửi chính là khách,
            // nhắc lại tên dưới mỗi tin là thừa và làm rối màn hình.
            SenderName: laThongBao ? null : TenNguoiGui(m, hoiThoai),
            IsSystemNotice: laThongBao,
            // Ảnh đại diện đi CÙNG tên: có tên riêng nghĩa là hội thoại nhiều người, và đó mới là
            // chỗ cần ảnh riêng từng tin. Hội thoại một-một dùng ảnh của hội thoại là đủ.
            SenderAvatarUrl: laThongBao || TenNguoiGui(m, hoiThoai) is null
                ? null : (m["from"] as JsonObject)?["avatar_url"]?.ToString()));
    }

    /// <summary>
    /// Đổi thông báo của nền tảng thành một câu tiếng Việt đọc được — <b>hàm thuần</b>.
    ///
    /// <para>Zalo gửi những việc này dưới dạng <c>attachments[{type:"zalo_system_message"}]</c>
    /// với phần chữ <b>rỗng</b>, nên không dựng câu ở đây thì tin bị bỏ hẳn và hội thoại mất hẳn
    /// dấu vết ai vào ai ra — trong khi bên Pancake vẫn thấy.</para>
    ///
    /// <para>Trả <c>null</c> khi không dựng nổi câu nào (vd <c>unsupported_message</c>): thà bỏ
    /// còn hơn ghi một dòng trống không ai hiểu.</para>
    /// </summary>
    public static string? MoTaThongBao(JsonObject? att)
    {
        if (att is null) return null;

        // Dạng đơn giản: Pancake cho sẵn câu chữ (vd "Tác nhân AI sẽ phản hồi.").
        if (att["type"]?.ToString() == "system_message")
            return att["message"]?.ToString() is { Length: > 0 } s ? s : null;

        if (att["type"]?.ToString() != "zalo_system_message") return null;
        var d = att["data"] as JsonObject;
        var viec = d?["type"]?.ToString();
        var nguoiLam = (d?["implementer"] as JsonObject)?["name"]?.ToString();
        var ai = string.Join(", ", ((d?["update_members"] as JsonArray) ?? new JsonArray())
            .OfType<JsonObject>().Select(x => x["dName"]?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        return viec switch
        {
            // Người tự rời: Zalo đặt chính họ ở cả implementer lẫn update_members.
            "msg_member_leave_group" =>
                $"{(string.IsNullOrWhiteSpace(ai) ? nguoiLam : ai)} đã rời khỏi nhóm",
            "remove_member" when ai.Length > 0 && !string.IsNullOrWhiteSpace(nguoiLam) =>
                $"{nguoiLam} đã mời {ai} ra khỏi nhóm",
            "remove_member" when ai.Length > 0 => $"{ai} đã rời khỏi nhóm",
            "members_join_group" when ai.Length > 0 && !string.IsNullOrWhiteSpace(nguoiLam) =>
                $"{nguoiLam} đã thêm {ai} vào nhóm",
            "members_join_group" when ai.Length > 0 => $"{ai} đã tham gia nhóm",
            _ => null,   // unsupported_message và mọi việc chưa biết: bỏ, đừng đoán
        };
    }

    /// <summary>
    /// Đổi thẻ ngân hàng Zalo thành chữ đọc được. Phần mã QR đi theo đường ĐÍNH KÈM (ảnh), xem
    /// <c>ChatAttachment</c> — ở đây chỉ dựng dòng chữ mang <b>tên ngân hàng và số tài khoản</b>,
    /// vì đó mới là thứ nhân viên cần đọc, chép và tìm kiếm lại được.
    /// </summary>
    public static string? MoTaTheNganHang(JsonObject? att)
    {
        if (att?["bank_card"] is not JsonObject t) return null;
        var ten = t["bank_name"]?.ToString();
        var so = t["bank_number"]?.ToString();
        if (string.IsNullOrWhiteSpace(so)) return string.IsNullOrWhiteSpace(ten) ? null : ten;
        return string.IsNullOrWhiteSpace(ten) ? $"Số tài khoản: {so}" : $"{ten} · {so}";
    }

    /// <summary>
    /// Đổi mã người được nhắc thành TÊN — <b>hàm thuần</b>.
    ///
    /// <para>Pancake để nhắc-tên ở hai dạng: <c>original_message</c> mang mã trần
    /// (<c>"chị @6043324489577600945 làm hồ sơ…"</c>), còn <c>message</c> đã dựng HTML có sẵn tên
    /// (<c>&lt;span class='message-tag'&gt;@Hoai Thu&lt;/span&gt;</c>). Mình đọc
    /// <c>original_message</c> (vì bản kia là HTML), nên phải tự dịch — không thì hộp thư hiện
    /// một dãy 19 chữ số giữa câu và chẳng ai biết đang gọi ai.</para>
    ///
    /// <para>Dịch bằng <c>message_tags</c> chứ KHÔNG dùng <c>pos</c>/<c>len</c> trong đó: hai số
    /// ấy tính theo bản ĐÃ DỰNG (<c>"@Hoai Thu"</c> dài 9), không khớp vị trí trong bản thô
    /// (<c>"@6043324489577600945"</c> dài 20). Thay theo chuỗi thì không phụ thuộc vị trí.</para>
    /// </summary>
    public static string? ThayNhacTen(string? chu, JsonArray? the)
    {
        if (string.IsNullOrEmpty(chu) || the is null || the.Count == 0) return chu;
        foreach (var t in the.OfType<JsonObject>())
        {
            var ma = t["id"]?.ToString();
            var ten = t["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(ma) || string.IsNullOrWhiteSpace(ten)) continue;
            // `name` của Pancake đã kèm sẵn dấu @ ("@Hoai Thu"), nên thay cả "@" + mã — để lại
            // dấu @ cũ là ra "@@Hoai Thu".
            chu = chu!.Replace("@" + ma, ten, StringComparison.Ordinal);
        }
        return chu;
    }

    /// <summary>
    /// Bỏ thẻ HTML khỏi nội dung tin — <b>hàm thuần</b>, chỉ dùng khi không có
    /// <c>original_message</c>.
    ///
    /// <para><c>&lt;br&gt;</c> và <c>&lt;/div&gt;</c> thành XUỐNG DÒNG chứ không thành rỗng: bỏ
    /// trắng thì tin nhiều dòng dính liền thành một câu dài vô nghĩa.</para>
    /// </summary>
    public static string? BoThe(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        // KHÔNG chặn sớm khi chuỗi không có '<'. Trường này luôn là bản Pancake đã dựng, nên
        // "gi&aacute; 5 &amp; 7" là thực thể HTML cần giải chứ không phải chữ khách gõ ra — chặn
        // sớm là hộp thư hiện nguyên "&aacute;" giữa câu tiếng Việt.
        var s = System.Text.RegularExpressions.Regex.Replace(
            html!, @"<\s*(br|/p|/div|/li)\s*/?\s*>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        // Gộp dòng trống thừa do thẻ lồng nhau sinh ra, rồi cắt hai đầu.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Đoán loại đính kèm. Pancake đặt loại trong <c>attachments[].type</c>, nhưng chuỗi khác nhau
    /// theo nền tảng gốc (Facebook "photo", TikTok "video"…) nên khớp theo tiền tố, không khớp
    /// cứng cả chuỗi.
    /// </summary>
    /// <summary>
    /// Tên thành viên đã gửi tin này trong hội thoại NHIỀU NGƯỜI, hoặc <c>null</c> ở hội thoại
    /// một-một.
    ///
    /// <para>Pancake để tên ở ba trường tuỳ nền tảng gốc: <c>name</c>, <c>displayName</c>,
    /// <c>zaloName</c>. Lấy cái đầu tiên có chữ.</para>
    ///
    /// <para>Trả <c>null</c> khi tên trùng tên hội thoại — đó là ca một-một, lặp lại tên khách
    /// dưới từng tin chỉ làm rối.</para>
    /// </summary>
    private static string? TenNguoiGui(JsonNode m, JsonNode? hoiThoai)
    {
        var ten = (m["from"] as JsonObject) is { } f
            ? f["name"]?.ToString() ?? f["displayName"]?.ToString() ?? f["zaloName"]?.ToString()
            : null;
        if (string.IsNullOrWhiteSpace(ten)) return null;
        var tenHoiThoai = (hoiThoai?["from"] as JsonObject)?["name"]?.ToString();
        return string.Equals(ten, tenHoiThoai, StringComparison.Ordinal) ? null : ten;
    }

    private static ChatKind DoanLoai(IReadOnlyList<JsonObject> tep)
    {
        var t = (tep[0]["type"]?.ToString() ?? "").ToLowerInvariant();
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
    ///
    /// <para><b>⚠️ <c>action</c> là trường BẮT BUỘC</b>, không phải tuỳ chọn. Thiếu nó Pancake trả
    /// HTTP 200 kèm <c>Missing required field: 'action'</c> và tin KHÔNG tới khách — gặp thật
    /// 05/11/2026, ba dòng hàng đợi hỏng liên tiếp.</para>
    ///
    /// <para><b>Chỉ làm <c>reply_inbox</c> (nhắn riêng).</b> Trả lời BÌNH LUẬN cần
    /// <c>action: reply_comment</c> kèm <c>message_id</c> của chính bình luận đó, mà đường gửi của
    /// lõi không mang mã ấy xuống tới đây. Nên bình luận gửi đi sẽ bị Pancake từ chối và báo lỗi
    /// tường minh — thà vậy còn hơn im lặng gửi nhầm vào hộp thư riêng của khách.</para>
    /// </summary>
    /// <summary>
    /// Thân lệnh gửi tin nhắn riêng. Tách ra để <b>test được</b> — hình dạng thân là chỗ đã hỏng
    /// một lần (thiếu <c>action</c>), và nó chỉ lộ ra lúc gọi mạng thật nếu không khoá bằng test.
    /// </summary>
    /// <remarks>
    /// Khai <c>[JsonPropertyName]</c> rõ ràng, KHÔNG dựa vào quy ước đặt tên của bộ tuần tự hoá:
    /// mặc định của .NET giữ nguyên chữ hoa (<c>"Action"</c>), mà Pancake chỉ nhận <c>"action"</c>.
    /// Và chỗ gọi có thể đổi tuỳ chọn tuần tự hoá bất cứ lúc nào mà không ai nghĩ tới chỗ này.
    /// </remarks>
    public record ThanGui(
        [property: System.Text.Json.Serialization.JsonPropertyName("action")] string Action,
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

    public static ThanGui ThanGuiChu(string text) => new("reply_inbox", text);

    /// <summary>Thân lệnh gửi tệp. Pancake không nhận URL ảnh trực tiếp nên gửi dưới dạng chữ.</summary>
    public static ThanGui ThanGuiTep(string url, string? caption)
        => new("reply_inbox", string.IsNullOrWhiteSpace(caption) ? url : caption + "\n" + url);

    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct)
        => GuiAsync(tenantId, accountId, externalUserId, ThanGuiChu(text), ct);

    /// <summary>
    /// Pancake <b>không nhận URL ảnh trực tiếp</b> ở lệnh gửi tin: ảnh phải tải lên trước rồi gửi
    /// bằng <c>content_ids</c>, và <c>message</c> với <c>content_ids</c> loại trừ nhau. Chưa làm
    /// bước tải lên thì gửi chữ kèm đường dẫn còn hơn báo thành công mà khách không nhận được gì.
    /// </summary>
    public Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
        => GuiAsync(tenantId, accountId, externalUserId, ThanGuiTep(url, caption), ct);

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

    /// <summary>
    /// Đổi lỗi khoá của Pancake thành câu nói rõ PHẢI LÀM GÌ. Hai mã dưới đây chiếm gần hết ca
    /// thật, và câu gốc của Pancake ("access_token renewed please use new access_token") không
    /// gợi ra nổi rằng người dùng cần vào Pancake lấy khoá mới rồi khai lại.
    /// </summary>
    private static string MoTaKhoaHong(JsonObject o)
    {
        var ma = o["error_code"]?.ToString();
        var goc = MoTaLoi(o) ?? "Pancake từ chối";
        return ma switch
        {
            // 105: ai đó bấm "Tạo Token" bên Pancake → khoá cũ bị huỷ ngay lúc đó.
            "105" => "Khoá trang đã bị thay bên Pancake nên khoá đang lưu hết tác dụng. "
                   + "Vào Pancake → trang → Cài đặt → Công cụ, copy Public API access token mới "
                   + "rồi khai lại trang này.",
            "102" => "Pancake không nhận khoá trang đang lưu. Lấy lại khoá ở Cài đặt → Công cụ "
                   + "của trang rồi khai lại.",
            _ => goc,
        };
    }

    private static string? MoTaLoi(JsonObject? o)
        => o?["message"]?.ToString() ?? o?["error"]?.ToString() ?? o?["error_description"]?.ToString();

    private static string Cat(string s) => s.Length <= 200 ? s : s[..200];

    // ── Kéo lịch sử (dùng lúc vừa nối) ───────────────────────────────────────

    /// <param name="Loi">Pancake từ chối vì lý do gì. <b>Phải có vế này</b>: khoá trang chết thì
    /// danh sách rỗng y hệt lúc trang thật sự không có hội thoại nào, và người dùng chỉ thấy
    /// "0 hội thoại" — đúng kiểu hỏng câm. Gặp thật 05/11/2026 với <c>error_code 105</c>
    /// ("access_token renewed please use new access_token").</param>
    public record TrangHoiThoai(IReadOnlyList<JsonNode> HoiThoai, string? ConTroTiep, string? Loi = null);

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
        if (string.IsNullOrWhiteSpace(token))
            return new(Array.Empty<JsonNode>(), null, "Trang này chưa có khoá — khai lại rồi thử.");

        try
        {
            var http = _http.CreateClient();
            var url = $"{PageApiV2}/pages/{Uri.EscapeDataString(accountId)}/conversations"
                    + $"?page_access_token={Uri.EscapeDataString(token!)}";
            if (!string.IsNullOrWhiteSpace(conTro))
                url += $"&last_conversation_id={Uri.EscapeDataString(conTro!)}";

            using var res = await http.GetAsync(url, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();

            // Pancake báo hỏng bằng HTTP 200 + success:false. Không soi vế này thì khoá chết trả
            // về y hệt "trang không có hội thoại nào" — người dùng thấy 0 và không biết vì sao.
            if (o?["success"]?.GetValue<bool>() == false)
                return new(Array.Empty<JsonNode>(), null, MoTaKhoaHong(o));
            if (!res.IsSuccessStatusCode)
                return new(Array.Empty<JsonNode>(), null,
                    MoTaLoi(o) ?? $"Pancake trả HTTP {(int)res.StatusCode}");

            var ds = (o?["conversations"] ?? o?["data"]) as JsonArray;
            if (ds is null || ds.Count == 0) return new(Array.Empty<JsonNode>(), null);

            var ra = ds.Where(x => x is not null).Select(x => x!).ToList();
            return new(ra, ra[^1]["id"]?.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/pancake] kéo danh sách hội thoại lỗi tenant={T} page={P}",
                tenantId, accountId);
            return new(Array.Empty<JsonNode>(), null, ex.Message);
        }
    }

    /// <summary>
    /// Đọc tin của MỘT hội thoại — <b>30 tin mỗi lượt</b>, lùi dần bằng <c>current_count</c> (vị
    /// trí tính từ đầu danh sách, không phải con trỏ). Tin trả về xếp MỚI TRƯỚC CŨ SAU.
    ///
    /// <para>Trả nguyên thân JSON để chỗ gọi tự bọc thành gói lịch sử — thân này mang cả
    /// <c>conv_from</c> (khách là ai) lẫn <c>messages</c>, đủ dựng gói mà không phải gọi thêm.</para>
    /// </summary>
    public async Task<JsonObject?> ReadMessagesAsync(string pageId, string token, string convId,
        int daDoc, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            var url = $"{PageApiV1}/pages/{Uri.EscapeDataString(pageId)}"
                    + $"/conversations/{Uri.EscapeDataString(convId)}/messages"
                    + $"?page_access_token={Uri.EscapeDataString(token)}"
                    + (daDoc > 0 ? $"&current_count={daDoc}" : "");
            using var res = await http.GetAsync(url, ct);
            var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
            // Lỗi báo bằng HTTP 200 + success:false — soi thân, đừng soi mã.
            return o?["success"]?.GetValue<bool>() == false ? null : o;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/pancake] đọc tin hội thoại {C} của trang {P} lỗi", convId, pageId);
            return null;
        }
    }

    /// <summary>
    /// Đoán TÊN TRANG để điền sẵn lúc khai — <b>không có đường hỏi thẳng</b>.
    ///
    /// <para>Đã dò tận nơi: <c>GET /pages/{id}</c>, <c>/info</c>, <c>/settings</c> đều KHÔNG tồn
    /// tại (trả trang HTML của Pancake, trong khi route có thật trả JSON <c>error_code 102</c>).
    /// Đặc tả cũng không có endpoint nào lấy thông tin một trang bằng khoá trang.</para>
    ///
    /// <para>Nên lấy gián tiếp: tên trang xuất hiện ở <c>from</c> của thứ do CHÍNH TRANG tạo ra —
    /// bài đăng của trang, hay tin do trang trả lời. Tìm trong bài đăng trước (rẻ, một lượt gọi),
    /// không thấy thì lục danh sách hội thoại.</para>
    ///
    /// <para>Thất bại trả <c>null</c>, KHÔNG đoán bừa: thà để người khai tự gõ còn hơn gắn một cái
    /// nhãn sai mà sau này không ai truy được nó từ đâu ra.</para>
    /// </summary>
    public async Task<string?> GuessPageNameAsync(string pageId, string token, CancellationToken ct)
        => (await GuessPageInfoAsync(pageId, token, ct)).Name;

    /// <summary>
    /// Đoán tên VÀ nền tảng của trang từ chính khoá trang.
    ///
    /// <para>Nền tảng phải đoán cùng lúc với tên chứ không gọi riêng một lượt: hai thứ nằm trong
    /// CÙNG một thân phản hồi, tách ra là nhân đôi số lượt gọi sang Pancake cho đúng một việc.</para>
    ///
    /// <para>Nền tảng đoán hụt thì về null và hội thoại hiện "Pancake" như trước — KHÔNG đoán bừa
    /// là "facebook" chỉ vì đa số trang là Facebook. Nói sai nguồn của khách còn tệ hơn không nói:
    /// người trực chọn giọng và tính hạn trả lời theo đúng thứ này.</para>
    /// </summary>
    public async Task<(string? Name, string? Platform)> GuessPageInfoAsync(
        string pageId, string token, CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            var den = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tu = DateTimeOffset.UtcNow.AddYears(-3).ToUnixTimeSeconds();
            var url = $"{PageApiV1}/pages/{Uri.EscapeDataString(pageId)}/posts"
                    + $"?page_access_token={Uri.EscapeDataString(token)}"
                    + $"&since={tu}&until={den}&page_number=1&page_size=10";
            using var res = await http.GetAsync(url, ct);
            var tin = DocThongTinTrang(await res.Content.ReadAsStringAsync(ct), pageId);
            if (tin.Name is { Length: > 0 }) return tin;
        }
        catch (Exception ex) { _log.LogDebug(ex, "[chat/pancake] đoán tên trang qua bài đăng hụt"); }

        // Trang chưa đăng bài nào (hoặc token không có quyền đọc bài) — lục hội thoại.
        try
        {
            var http = _http.CreateClient();
            var url = $"{PageApiV2}/pages/{Uri.EscapeDataString(pageId)}/conversations"
                    + $"?page_access_token={Uri.EscapeDataString(token)}";
            using var res = await http.GetAsync(url, ct);
            return DocThongTinTrang(await res.Content.ReadAsStringAsync(ct), pageId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[chat/pancake] đoán tên trang qua hội thoại hụt");
            return (null, null);
        }
    }

    /// <summary>
    /// Lục cả cây JSON tìm object dạng <c>{"id": &lt;mã trang&gt;, "name": ...}</c> — <b>hàm thuần</b>.
    ///
    /// <para>Lục cả cây chứ không đi theo đường dẫn cố định vì tên trang nằm ở chỗ khác nhau tuỳ
    /// nguồn: bài đăng để ở <c>posts[].from</c>, hội thoại để ở <c>conversations[].from</c>, tin
    /// nhắn để ở <c>messages[].from</c>. Bám một đường dẫn là mỗi nguồn phải viết một hàm.</para>
    ///
    /// <para>Khớp theo ĐÚNG mã trang, không lấy <c>name</c> đầu tiên gặp được: phần lớn
    /// <c>from</c> trong các thân này là KHÁCH, lấy bừa là đặt tên trang bằng tên một khách hàng.</para>
    /// </summary>
    public static string? DocTenTrang(string? rawJson, string pageId)
        => DocThongTinTrang(rawJson, pageId).Name;

    /// <summary>
    /// Như <see cref="DocTenTrang"/> nhưng lấy cả <c>platform</c> của đúng trang đó — <b>hàm thuần</b>.
    ///
    /// <para>Vẫn khớp theo ĐÚNG mã trang: phần lớn object <c>from</c> trong các thân này là KHÁCH,
    /// lấy bừa là gán nhầm tên (và nền tảng) của một khách hàng cho trang.</para>
    ///
    /// <para>Trang khớp mà không kèm <c>platform</c> thì vế đó về null — thân của Pancake không
    /// phải chỗ nào cũng có trường này, và trống thì thà im còn hơn đoán.</para>
    /// </summary>
    public static (string? Name, string? Platform) DocThongTinTrang(string? rawJson, string pageId)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.IsNullOrWhiteSpace(pageId)) return (null, null);
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawJson!); } catch { return (null, null); }
        return Luc(goc, pageId, 0) ?? (null, null);

        static (string?, string?)? Luc(JsonNode? nut, string pageId, int sau)
        {
            if (nut is null || sau > 12) return null;   // chặn cây lồng sâu bất thường
            switch (nut)
            {
                case JsonObject o:
                    if (o["id"]?.ToString() == pageId
                        && o["name"]?.ToString() is { Length: > 0 } ten)
                        return (ten, o["platform"]?.ToString() is { Length: > 0 } nt ? nt : null);
                    foreach (var kv in o)
                        if (Luc(kv.Value, pageId, sau + 1) is { } t) return t;
                    return null;
                case JsonArray a:
                    foreach (var x in a)
                        if (Luc(x, pageId, sau + 1) is { } t) return t;
                    return null;
                default: return null;
            }
        }
    }
}
