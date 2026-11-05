// Services/Chat/Channels/PancakeHistoryImporter.cs
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Services.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Kéo hội thoại cũ của một trang Pancake về hộp thư.
///
/// <para><b>Vì sao cần.</b> Pancake chỉ đẩy tin MỚI qua webhook. Trang vừa nối thì hộp thư trống
/// trơn cho tới khi có khách nhắn — và nếu máy chủ chưa có địa chỉ công khai (đang chạy nội bộ)
/// thì trống mãi. Đây là đường DUY NHẤT thấy được hội thoại mà không cần webhook.</para>
///
/// <para><b>Người dùng TỰ BẤM</b>, không chạy ngầm lúc nối — cùng lý lẽ với
/// <see cref="MetaHistoryImporter"/>: một trang bán hàng lâu năm có hàng nghìn hội thoại, gọi dồn
/// dập là Pancake chặn tạm, mà bị chặn thì tin trực tiếp cũng ngừng về.</para>
///
/// <para><b>Dựng gói ĐÚNG hình dạng webhook</b> rồi để <see cref="PancakeChatAdapter.Parse"/> đọc,
/// thay vì viết một bộ đọc thứ hai. Tin cũ và tin mới phải ra cùng kết quả — tách hai bộ đọc là
/// mỗi lỗi phải sửa hai nơi, và hai nơi đó sẽ trôi xa nhau.</para>
/// </summary>
public class PancakeHistoryImporter
{
    /// <summary>Trần hội thoại mỗi lượt bấm. Còn nữa thì giao diện mời bấm tiếp.</summary>
    private const int MaxConversations = ChatHistoryImport.MaxConversations;

    /// <summary>Trần tin mỗi hội thoại. Đủ ngữ cảnh bán hàng, không phải bản sao lưu.</summary>
    private const int MaxMessagesPerConversation = ChatHistoryImport.MaxMessagesPerConversation;

    /// <summary>Pancake trả 30 tin một lượt (<c>current_count</c> lùi dần).</summary>
    private const int MessagePageSize = 30;

    /// <summary>
    /// Trần cho pha BẮT KỊP (đọc từ tin mới nhất xuống tới vùng đã có). Nhỏ vì bình thường chỉ
    /// vài tin mới kể từ lượt trước — chặn để một hội thoại bỏ quên nửa năm không nuốt trọn trần
    /// thời gian của cả lượt. Phần còn thiếu pha đào sâu nhặt nốt ở những lượt sau.
    /// </summary>
    private const int MaxCatchUpMessages = MessagePageSize * 5;

    /// <summary>
    /// Trần THỜI GIAN một lượt. Chạm trần thì dừng êm, báo "còn nữa" để người dùng bấm tiếp.
    ///
    /// <para><b>Vì sao cần, dù đã có trần số hội thoại.</b> Trần số đếm việc, không đếm thời gian:
    /// 200 hội thoại × 200 tin = tối đa ~1400 lượt gọi Pancake NỐI TIẾP, cỡ 7–10 phút. Mà hàng
    /// đợi kéo lịch sử chạy MỘT lượt tại một thời điểm cho CẢ máy chủ — nên một công ty nhiều dữ
    /// liệu sẽ chặn mọi công ty khác suốt ngần ấy phút, và người ta chỉ thấy nút bấm xong rồi
    /// không có gì xảy ra.</para>
    ///
    /// <para>Dừng giữa chừng KHÔNG mất gì: phần đã đọc đã nằm trong hàng đợi, và lượt sau chống
    /// trùng bỏ qua ngay những mảnh đã nhập.</para>
    /// </summary>
    private static readonly TimeSpan TranThoiGian = TimeSpan.FromMinutes(5);

    private readonly PancakeChatAdapter _pc;
    private readonly ChannelCredentialStore _cred;
    private readonly ChatRepository _repo;
    private readonly ChatWorkSignal _tin;
    private readonly ILogger<PancakeHistoryImporter> _log;

    public PancakeHistoryImporter(PancakeChatAdapter pc, ChannelCredentialStore cred,
        ChatRepository repo, ChatWorkSignal tin, ILogger<PancakeHistoryImporter> log)
    { _pc = pc; _cred = cred; _repo = repo; _tin = tin; _log = log; }

    public async Task<ChatHistoryImport.KetQua> ImportAsync(string tenantId, string accountId,
        CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, ChatChannel.Pancake, accountId, ct);
        var token = c is not null && c.TryGetValue("pageAccessToken", out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(token))
            return new(0, 0, false, "Trang này chưa có khoá — nối lại rồi thử.");

        var soHt = 0;
        var soTin = 0;
        var conNua = false;
        string? conTro = null;
        var hetGio = DateTime.UtcNow + TranThoiGian;

        // Đã lấy tới đâu ở lượt trước, cho TỪNG hội thoại. Một truy vấn cho cả trang.
        //
        // Thiếu con số này thì lượt bấm thứ hai tải lại đúng những tin mới nhất đã có, chống trùng
        // loại sạch, và người dùng KHÔNG BAO GIỜ lấy được lịch sử xa hơn — triệu chứng: hội thoại
        // dừng ở một mốc cũ (tháng 3) rồi bấm mãi cũng không lùi thêm.
        var daCo = await _repo.CountMessagesPerConversationAsync(
            tenantId, ChatChannel.Pancake, accountId, ct);

        try
        {
            while (soHt < MaxConversations)
            {
                if (DateTime.UtcNow >= hetGio) { conNua = true; break; }
                var trang = await _pc.ListConversationsAsync(tenantId, accountId, conTro, ct);

                // Pancake từ chối (khoá chết…) thì NÓI RA. Trước đây lỗi này trả về danh sách rỗng
                // y hệt lúc trang thật sự chưa có hội thoại nào, và người dùng chỉ thấy báo
                // "0 hội thoại" — không có cách nào biết khoá đã hỏng.
                if (trang.Loi is not null)
                {
                    _log.LogWarning("[chat/lịch-sử] pancake/{Acc} dừng: {L}", accountId, trang.Loi);
                    return new(soHt, soTin, false, trang.Loi);
                }
                if (trang.HoiThoai.Count == 0) break;

                foreach (var ht in trang.HoiThoai)
                {
                    if (soHt >= MaxConversations) break;
                    // Kiểm giờ ở VÒNG TRONG, không chỉ vòng ngoài: một trang danh sách là 60 hội
                    // thoại, mỗi hội thoại tới 7 lượt gọi — chờ hết trang mới kiểm thì đã quá trần
                    // vài phút.
                    if (DateTime.UtcNow >= hetGio) { conNua = true; break; }
                    if (ht["id"]?.ToString() is not { Length: > 0 } maHt) continue;

                    soTin += await MotHoiThoaiAsync(tenantId, accountId, token!, ht, maHt,
                        daCo.GetValueOrDefault(maHt), ct);
                    soHt++;

                }

                if (conNua) break;
                // Con trỏ không đổi nghĩa là Pancake trả lại đúng trang cũ — đi tiếp là lặp vô tận.
                if (string.IsNullOrWhiteSpace(trang.ConTroTiep) || trang.ConTroTiep == conTro) break;
                conTro = trang.ConTroTiep;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "[chat/lịch-sử] pancake/{Acc} hỏng giữa chừng", accountId);
            // Trả phần ĐÃ lấy được thay vì coi như trắng tay: những gì đã xếp hàng đợi vẫn sẽ ghi,
            // và người dùng cần con số thật để quyết định có bấm lại không.
            return new(soHt, soTin, true, "Dừng giữa chừng: " + ex.Message);
        }

        if (soTin > 0) _tin.Signal(ChatLane.In);
        conNua = conNua || soHt >= MaxConversations;
        _log.LogInformation("[chat/lịch-sử] pancake/{Acc}: {H} hội thoại, {T} tin vào hàng đợi{C}",
            accountId, soHt, soTin, conNua ? " (còn nữa)" : "");
        return new(soHt, soTin, conNua, null);
    }

    /// <summary>
    /// Đọc tin của MỘT hội thoại rồi xếp vào hàng đợi theo từng trang.
    ///
    /// <para>Xếp theo trang chứ không gom hết rồi mới xếp: hội thoại dài mà đứt mạng giữa chừng thì
    /// phần đã đọc vẫn còn, không phải đọc lại từ đầu.</para>
    /// </summary>
    /// <param name="daCo">Số tin của hội thoại này ĐÃ nằm trong hộp thư. Lượt kéo bắt đầu từ vị
    /// trí đó để đi tiếp phần CŨ HƠN, thay vì tải lại đúng những tin mới nhất đã có.
    ///
    /// <para>Con số này nhỏ hơn hoặc bằng vị trí thật bên Pancake (mình lọc bỏ thả cảm xúc, thông
    /// báo hệ thống…), nên cùng lắm là <b>chồng lấn</b> vài tin — chống trùng lo nốt. Sai theo
    /// hướng này là an toàn; sai hướng ngược lại sẽ NHẢY CÓC, bỏ sót tin giữa chừng.</para></param>
    private async Task<int> MotHoiThoaiAsync(string tenantId, string accountId, string token,
        JsonNode hoiThoai, string maHoiThoai, ChatRepository.TienDoHoiThoai? daCo,
        CancellationToken ct)
    {
        var dem = 0;   // số tin lấy được RIÊNG lượt này

        // ── Pha 1: BẮT KỊP tin mới ───────────────────────────────────────────
        //
        // Bắt buộc phải có, và đây là chỗ bản trước sai: nhảy thẳng tới vị trí đã dừng thì tin
        // MỚI nằm ở đầu danh sách (vị trí 0..n) bị bỏ qua vĩnh viễn — hội thoại đứng im ở mốc
        // của lần nhập đầu dù khách vẫn đang nhắn. Webhook vốn lo việc này, nhưng máy chủ chạy
        // nội bộ thì không có webhook, và kể cả có thì lúc webhook chết đây là đường vá.
        //
        // Dừng ngay khi chạm vùng đã có, nên thường chỉ tốn ĐÚNG MỘT lượt gọi.
        if (daCo is { } tiendo)
            dem += await DocTungTrangAsync(tenantId, accountId, token, hoiThoai, maHoiThoai,
                tuViTri: 0, tranTin: MaxCatchUpMessages, dungKhiCuHon: tiendo.MoiNhat, ct);

        // ── Pha 2: ĐÀO SÂU xuống lịch sử cũ hơn ──────────────────────────────
        dem += await DocTungTrangAsync(tenantId, accountId, token, hoiThoai, maHoiThoai,
            tuViTri: daCo?.So ?? 0, tranTin: MaxMessagesPerConversation, dungKhiCuHon: null, ct);
        return dem;
    }

    /// <param name="tuViTri">Vị trí bắt đầu đọc, tính từ tin MỚI NHẤT.</param>
    /// <param name="dungKhiCuHon">Dừng khi cả trang đã nằm trong vùng mình có (pha bắt kịp);
    /// <c>null</c> = đọc tới hết trần (pha đào sâu).</param>
    private async Task<int> DocTungTrangAsync(string tenantId, string accountId, string token,
        JsonNode hoiThoai, string maHoiThoai, int tuViTri, int tranTin,
        DateTime? dungKhiCuHon, CancellationToken ct)
    {
        var viTri = tuViTri;
        var dem = 0;

        while (dem < tranTin)
        {
            var than = await _pc.ReadMessagesAsync(accountId, token, maHoiThoai, viTri, ct);
            if (than?["messages"] is not JsonArray ds || ds.Count == 0) break;

            var lay = ds.Where(x => x is not null)
                        .Take(tranTin - dem).ToList();
            if (lay.Count == 0) break;

            // Hội thoại trong gói: ưu tiên conv_from của thân tin (chính xác hơn, có cả tên khách),
            // thiếu thì lấy từ dòng hội thoại ở danh sách.
            var ht = new JsonObject
            {
                ["id"] = maHoiThoai,
                ["type"] = hoiThoai["type"]?.ToString() ?? "INBOX",
                ["from"] = (than["conv_from"] ?? hoiThoai["from"])?.DeepClone(),
            };

            var goi = new JsonObject
            {
                ["page_id"] = accountId,
                ["event_type"] = "messaging",
                // ⚠️ CỜ SỐNG CÒN. Thiếu nó thì lõi coi tin ba năm trước là tin VỪA TỚI: trợ lý
                // sinh một câu trả lời cho TỪNG tin rồi gửi thẳng cho khách hôm nay, bot câm 30
                // phút, và cả năm hội thoại đóng dấu giờ nhập nên dồn vào một phút.
                //
                // Đã xảy ra thật 05/11/2026: lượt nhập đầu không có cờ này, hàng đợi gửi bắt đầu
                // đẩy câu trả lời cho tin cũ. Chỉ không tới tay khách vì lệnh gửi lúc đó đang hỏng
                // vì một lý do khác — tức là thoát nạn nhờ may, không nhờ thiết kế.
                ["tourkit_lich_su"] = true,
                ["data"] = new JsonObject
                {
                    ["conversation"] = ht,
                    ["messages"] = new JsonArray(lay.Select(x => x!.DeepClone()).ToArray()),
                    // Bình luận cần mã bài để gom về đúng luồng; hội thoại hộp thư thì không có.
                    ["post"] = hoiThoai["post_id"]?.ToString() is { Length: > 0 } bai
                        ? new JsonObject { ["id"] = bai } : null,
                },
            };

            // Mã chống trùng ghép từ hội thoại + vị trí đã đọc: bấm lại lượt hai thì hàng đợi loại
            // ngay, không phải đọc lại rồi mới phát hiện trùng ở tầng tin.
            await _repo.EnqueueInboundAsync(tenantId, ChatChannel.Pancake, accountId,
                // Khoá chống trùng theo VỊ TRÍ tuyệt đối trong hội thoại, không theo số đã lấy
                // lượt này — nếu không, lượt bấm thứ hai sinh lại đúng khoá "…:0" của lượt đầu và
                // cả trang tin cũ bị loại oan.
                $"ls:{maHoiThoai}:{viTri}", goi.ToJsonString(), ct);

            dem += lay.Count;
            viTri += lay.Count;
            if (lay.Count < MessagePageSize) break;   // trang cuối — hết lịch sử

            // Pha bắt kịp: trong MỖI trang Pancake xếp tin CŨ → MỚI (đo trên dữ liệu thật
            // 05/11/2026, đặc tả ghi ngược lại). Nên tin đầu trang là tin cũ nhất của trang —
            // nó đã cũ hơn mốc mình có nghĩa là cả trang nằm trong vùng đã biết, dừng được.
            if (dungKhiCuHon is { } moc
                && DocGioTin(lay[0]) is { } cuNhat && cuNhat <= moc) break;
        }

        return dem;
    }

    /// <summary>
    /// Giờ gửi của một tin Pancake. Cùng quy ước với <c>PancakeChatAdapter</c>: chuỗi KHÔNG có
    /// hậu tố múi giờ, phải ép UTC — không ép thì mốc so sánh lệch 7 tiếng và pha bắt kịp hoặc
    /// dừng quá sớm (mất tin) hoặc không bao giờ dừng (tốn gọi).
    /// </summary>
    private static DateTime? DocGioTin(JsonNode? m)
        => DateTime.TryParse(m?["inserted_at"]?.ToString(),
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal
               | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
           ? d : null;
}
