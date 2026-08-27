// Services/Chat/Channels/MetaHistoryImporter.cs
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Services.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Kéo các đoạn hội thoại CŨ của Messenger / Instagram về hộp thư.
///
/// <para><b>Vì sao chỉ hai kênh này.</b> Sáu kênh thì bốn kênh không có đường nào lấy lại lịch sử:
/// Telegram Bot API không cho đọc quá khứ (bot chỉ thấy tin gửi tới nó SAU khi được tạo); Zalo
/// Open API không có đầu đọc hội thoại; TikTok có nhưng đòi tư cách Messaging Partner phải xin
/// duyệt riêng. WhatsApp thì có đường khác hẳn — Meta tự ĐẨY về sau khi mình gọi
/// <c>smb_app_data</c>, xem <see cref="WhatsAppChatAdapter"/>. Chỉ Messenger và Instagram là cho
/// mình chủ động ĐỌC, qua Graph.</para>
///
/// <para><b>Người dùng tự bấm, không chạy tự động lúc nối.</b> Một Trang bán hàng lâu năm có thể
/// có hàng chục nghìn tin; đó là một quyết định về dữ liệu và hạn mức gọi API, không phải thứ nên
/// âm thầm làm thay họ.</para>
///
/// <para><b>Đi qua hàng đợi <c>chat_inbound_events</c> chứ không ghi thẳng.</b> Nhờ vậy được lại
/// nguyên bộ: chống trùng ở tầng CSDL, chạy lại được khi mất điện giữa chừng, và dùng chung đúng
/// một đường ghi tin với webhook — chứ không phải đường thứ hai âm thầm lệch dần khỏi đường thật.</para>
/// </summary>
public class MetaHistoryImporter
{
    private const string GraphBase = "https://graph.facebook.com";
    private const string InstagramBase = "https://graph.instagram.com";
    private const string ApiVersion = "v21.0";

    /// <summary>
    /// Số hội thoại tối đa một lượt. Chặn để một Trang mười năm tuổi không nuốt trọn hạn mức gọi
    /// Graph của cả ngày — hết hạn mức là <b>tin trực tiếp cũng ngừng về</b>, tức lấy lịch sử làm
    /// hỏng chính việc đang chạy. Hết chặn thì bấm lại lượt nữa: đã ghi rồi thì bỏ qua.
    /// </summary>
    private const int MaxConversations = 200;

    /// <summary>Số tin tối đa mỗi hội thoại. Đủ cho ngữ cảnh bán hàng, không phải bản sao lưu.</summary>
    private const int MaxMessagesPerConversation = 200;

    /// <summary>Graph cho tối đa 500 mục một trang.</summary>
    private const int PageSize = 100;

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly ChatRepository _repo;
    private readonly ChatWorkSignal _tin;
    private readonly ILogger<MetaHistoryImporter> _log;

    public MetaHistoryImporter(IHttpClientFactory http, ChannelCredentialStore cred,
        ChatRepository repo, ChatWorkSignal tin, ILogger<MetaHistoryImporter> log)
    { _http = http; _cred = cred; _repo = repo; _tin = tin; _log = log; }

    /// <param name="SoHoiThoai">Số hội thoại đã đọc.</param>
    /// <param name="SoTin">Số tin đã xếp vào hàng đợi — <b>chưa</b> phải số tin ghi được, vì tin đã
    /// có sẵn sẽ bị chống trùng loại bỏ ở bước sau.</param>
    /// <param name="ConNua">Còn hội thoại chưa đọc vì chạm mức chặn. Giao diện nói ra để người dùng
    /// biết bấm thêm lượt nữa, thay vì tưởng đã lấy hết.</param>
    public record KetQua(int SoHoiThoai, int SoTin, bool ConNua, string? Loi);

    public static bool Supports(ChatChannel kenh)
        => kenh is ChatChannel.Messenger or ChatChannel.Instagram;

    public async Task<KetQua> ImportAsync(string tenantId, ChatChannel kenh, string accountId,
        CancellationToken ct)
    {
        if (!Supports(kenh))
            return new(0, 0, false, $"Kênh {kenh} không có đường đọc lại hội thoại cũ.");

        var c = await _cred.GetAsync(tenantId, kenh, accountId, ct);
        if (c is null || !c.TryGetValue("pageAccessToken", out var token) || string.IsNullOrWhiteSpace(token))
            return new(0, 0, false, "Tài khoản này chưa có khoá đăng nhập — nối lại rồi thử.");

        // Messenger đọc theo id TRANG; Instagram đọc theo chính "me" của token đã cấp.
        var goc = kenh == ChatChannel.Instagram ? InstagramBase : GraphBase;
        var chuNha = kenh == ChatChannel.Instagram
            ? "me"
            : (c.GetValueOrDefault("pageId") is { Length: > 0 } pid ? pid : "me");

        var http = _http.CreateClient();
        var soHt = 0;
        var soTin = 0;
        string? trang = null;

        try
        {
            while (soHt < MaxConversations)
            {
                var duong = $"{goc}/{ApiVersion}/{Uri.EscapeDataString(chuNha)}/conversations"
                          + $"?fields=id,participants,updated_time&limit={PageSize}"
                          + (trang is null ? "" : $"&after={Uri.EscapeDataString(trang)}");

                var o = await JsonAsync(http, duong, token!, kenh, ct);
                if (o?["error"] is { } loi)
                    return new(soHt, soTin, false, MoTaLoi(loi));
                if (o?["data"] is not JsonArray ds || ds.Count == 0) break;

                foreach (var ht in ds.OfType<JsonNode>())
                {
                    if (soHt >= MaxConversations) break;
                    var maHt = ht["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(maHt)) continue;

                    soTin += await MotHoiThoaiAsync(http, tenantId, kenh, accountId, goc, token!, maHt!,
                        ht["participants"]?["data"] as JsonArray, chuNha, ct);
                    soHt++;
                }

                // Graph chỉ còn trang sau khi CÓ paging.next. Có cursor mà không có next nghĩa là
                // hết — đi tiếp là lặp vô tận trên chính trang cuối.
                trang = o["paging"]?["next"] is null ? null : o["paging"]?["cursors"]?["after"]?.ToString();
                if (string.IsNullOrWhiteSpace(trang)) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "[chat/lịch-sử] {Kenh}/{Acc} hỏng giữa chừng", kenh, accountId);
            // Trả về phần ĐÃ lấy được thay vì coi như trắng tay: những hội thoại đã xếp hàng đợi
            // vẫn sẽ được ghi, và người dùng cần biết con số thật để quyết định bấm lại.
            return new(soHt, soTin, true, "Dừng giữa chừng: " + ex.Message);
        }

        if (soTin > 0) _tin.Signal(ChatLane.In);
        _log.LogInformation("[chat/lịch-sử] {Kenh}/{Acc}: {H} hội thoại, {T} tin vào hàng đợi",
            kenh, accountId, soHt, soTin);
        return new(soHt, soTin, soHt >= MaxConversations, null);
    }

    /// <summary>
    /// Đọc tin của MỘT hội thoại rồi xếp vào hàng đợi theo từng trang.
    ///
    /// <para>Xếp theo trang chứ không gom hết rồi mới xếp: hội thoại dài mà mất mạng giữa chừng thì
    /// phần đã đọc vẫn còn, không phải đọc lại từ đầu.</para>
    /// </summary>
    private async Task<int> MotHoiThoaiAsync(HttpClient http, string tenantId, ChatChannel kenh,
        string accountId, string goc, string token, string maHoiThoai, JsonArray? nguoiThamGia,
        string chuNha, CancellationToken ct)
    {
        // Khách = người tham gia KHÔNG phải mình. Trang cũng nằm trong participants, lấy bừa phần
        // tử đầu là một nửa số hội thoại mang tên chính công ty mình.
        var khach = nguoiThamGia?.OfType<JsonNode>()
            .FirstOrDefault(x => x["id"]?.ToString() is { Length: > 0 } id && id != chuNha);
        var maKhach = khach?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(maKhach)) return 0;

        var dem = 0;
        string? trang = null;

        while (dem < MaxMessagesPerConversation)
        {
            var duong = $"{goc}/{ApiVersion}/{Uri.EscapeDataString(maHoiThoai)}/messages"
                      + "?fields=id,message,from,created_time,"
                      + "attachments{id,name,mime_type,size,image_data,video_data,file_url}"
                      + $"&limit={PageSize}"
                      + (trang is null ? "" : $"&after={Uri.EscapeDataString(trang)}");

            var o = await JsonAsync(http, duong, token, kenh, ct);
            if (o?["error"] is not null) break;   // hội thoại lỗi thì bỏ qua, đừng chặn cả lượt
            if (o?["data"] is not JsonArray ds || ds.Count == 0) break;

            var lay = ds.OfType<JsonNode>().Take(MaxMessagesPerConversation - dem).ToList();
            if (lay.Count == 0) break;

            var than = new JsonObject
            {
                ["tourkit_lich_su"] = new JsonObject
                {
                    ["khach"] = maKhach,
                    ["ten"] = khach?["name"]?.ToString(),
                    ["cuaToi"] = chuNha,
                    ["tin"] = new JsonArray(lay.Select(x => x.DeepClone()).ToArray()),
                },
            };

            // Mã chống trùng ghép từ hội thoại + trang: bấm lại lượt hai thì hàng đợi bỏ qua ngay,
            // không phải đọc lại rồi mới phát hiện trùng ở tầng tin.
            var maChongTrung = $"ls:{maHoiThoai}:{trang ?? "0"}";
            await _repo.EnqueueInboundAsync(tenantId, kenh, accountId, maChongTrung,
                than.ToJsonString(), ct);

            dem += lay.Count;
            trang = o["paging"]?["next"] is null ? null : o["paging"]?["cursors"]?["after"]?.ToString();
            if (string.IsNullOrWhiteSpace(trang)) break;
        }

        return dem;
    }

    /// <summary>
    /// Gọi Graph. <b>Token đi khác chỗ ở hai kênh:</b> Instagram đòi header <c>Authorization</c>,
    /// Facebook nhận <c>?access_token=</c> trên URL. Chép nguyên cách của kênh này sang kênh kia là
    /// mọi lượt gọi đều bị từ chối.
    /// </summary>
    private async Task<JsonNode?> JsonAsync(HttpClient http, string url, string token,
        ChatChannel kenh, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            kenh == ChatChannel.Instagram ? url : url + $"&access_token={Uri.EscapeDataString(token)}");
        if (kenh == ChatChannel.Instagram) req.Headers.Add("Authorization", "Bearer " + token);

        using var res = await http.SendAsync(req, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Đổi lỗi Graph thành câu người đọc hiểu. Hai mã dưới đây chiếm gần hết ca thật, và câu gốc
    /// của Meta thì không gợi ra cách chữa.
    /// </summary>
    private static string MoTaLoi(JsonNode loi)
    {
        var ma = loi["code"]?.ToString();
        var noi = loi["message"]?.ToString() ?? "không rõ lý do";
        return ma switch
        {
            "190" => "Khoá đăng nhập đã hết hạn. Bấm Kết nối lại rồi thử tiếp.",
            "4" or "17" or "613" =>
                "Facebook đang chặn tạm vì gọi quá nhiều. Chờ khoảng một giờ rồi bấm lại — "
                + "phần đã lấy được vẫn giữ nguyên.",
            "10" or "200" =>
                "Ứng dụng chưa được cấp quyền đọc hội thoại cũ (cần pages_messaging và "
                + "pages_read_engagement). Bấm Kết nối lại để cấp thêm quyền.",
            _ => $"Facebook từ chối: {noi}",
        };
    }
}
