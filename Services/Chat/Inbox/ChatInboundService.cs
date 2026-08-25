// Services/Chat/Inbox/ChatInboundService.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat.Channels;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Đường xử lý tin khách nhắn tới: chống trùng → ghi → gộp cụm → bot trả lời → xếp hàng đợi gửi.
///
/// <para><b>Đợt 1 CHƯA nối nghiệp vụ CRM.</b> Bot trả lời bằng kiến thức chung + lời dặn của công
/// ty, không tra cứu tour/khách/giá. Nối CRM là việc của đợt sau — làm sớm thì mỗi tin khách nhắn
/// kéo theo một loạt truy vấn CRM trong khi phần đường ống còn chưa chắc.</para>
/// </summary>
public class ChatInboundService
{
    /// Chờ khách im bấy lâu rồi mới gộp cụm tin lại xử lý.
    private static readonly TimeSpan NghiGopTin = TimeSpan.FromSeconds(4);

    private readonly ChatRepository _repo;
    private readonly IEnumerable<IChatChannelAdapter> _adapters;
    private readonly ProviderRegistry _providers;
    private readonly AiCallContext _aiCtx;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatInboundService> _log;

    public ChatInboundService(ChatRepository repo, IEnumerable<IChatChannelAdapter> adapters,
        ProviderRegistry providers, AiCallContext aiCtx, IConfiguration cfg,
        ILogger<ChatInboundService> log)
    { _repo = repo; _adapters = adapters; _providers = providers; _aiCtx = aiCtx; _cfg = cfg; _log = log; }

    public IChatChannelAdapter? Adapter(ChatChannel kenh)
        => _adapters.FirstOrDefault(a => a.Channel == kenh);

    /// <summary>
    /// Xử lý một loạt sự kiện của cùng một kênh.
    ///
    /// <para>Gọi từ NỀN, sau khi webhook đã trả 200. Kênh nào cũng gửi lại khi không thấy 200, mà
    /// xử lý mất vài giây (có gọi AI) — trả lời chậm là kênh gửi lại và khách nhận tin nhân đôi.</para>
    /// </summary>
    /// <param name="accountId">Tài khoản (Trang/OA/bot) đã kiểm chữ ký khớp — do endpoint webhook
    /// xác định TRƯỚC khi gọi hàm này, xem <see cref="IChatChannelAdapter.VerifyAsync"/>.</param>
    public async Task HandleAsync(string tenantId, string accountId, IReadOnlyList<InboundChatEvent> sk,
        CancellationToken ct)
    {
        foreach (var e in sk)
        {
            try { await MotSuKienAsync(tenantId, accountId, e, ct); }
            catch (Exception ex)
            {
                // Một sự kiện hỏng không được kéo cả loạt: mỗi tin là một khách khác nhau.
                _log.LogError(ex, "[chat] xử lý sự kiện hỏng tenant={T} kênh={K} uid={U}",
                    tenantId, e.Channel, e.ExternalUserId);
            }
        }
    }

    private async Task MotSuKienAsync(string tenantId, string accountId, InboundChatEvent e, CancellationToken ct)
    {
        await _repo.UpsertContactAsync(tenantId, e.Channel, e.ExternalUserId, e.DisplayName, ct);
        var hoiThoai = await _repo.GetOrCreateConversationAsync(tenantId, e.Channel, e.ExternalUserId, accountId, ct);

        // Nền tảng báo trạng thái tin MÌNH đã gửi — không phải tin mới, xử lý xong là về.
        // Trước đây chỗ này bóc ra rồi BỎ, nên tin gửi đi dừng mãi ở "đã gửi" dù giao diện đã vẽ
        // sẵn dấu tích hai mức.
        if (e.Moc is { } moc)
        {
            var soDong = await _repo.DanhDauMocAsync(tenantId, hoiThoai.Id, moc.TrangThai, moc.DenLuc, ct);
            _log.LogDebug("[chat] mốc {TT} tới {Luc:o} — đổi {N} tin, hội thoại {H}",
                moc.TrangThai, moc.DenLuc, soDong, hoiThoai.Id);
            return;
        }

        // ── Tiếng vọng: nhân viên trả lời từ app của kênh ────────────────────
        if (e.IsEcho)
        {
            var idV = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.Ra,
                ChatSender.NhanVien, null, e.Kind, e.Text, e.AttachmentJson, e.ExternalMsgId,
                ChatState.DaGui, ct);
            if (idV is null) return;   // đã ghi lần trước
            await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.TomTat(e.Text), false, ct);
            // Có người thật đang trả lời → bot phải câm, nếu không nó nói đè lên người ta.
            await _repo.PauseBotAsync(tenantId, hoiThoai.Id, (int)ChatRules.BotCamMacDinh.TotalMinutes, ct);
            return;
        }

        // ── Tin của khách ────────────────────────────────────────────────────
        var id = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.Vao,
            ChatSender.Khach, null, e.Kind, e.Text, e.AttachmentJson, e.ExternalMsgId, ChatState.DaNhan, ct);
        if (id is null) return;   // webhook gửi lại — bỏ qua, KHÔNG sinh thêm câu trả lời

        await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.TomTat(e.Text), true, ct);

        // Gộp tin nhắn liên tiếp: chờ khách im rồi mới xử lý cả cụm. Chờ thẳng ở đây thay vì hẹn
        // giờ riêng — đang chạy nền, vài giây không ảnh hưởng ai, mà đỡ hẳn một cơ chế hẹn giờ.
        await Task.Delay(NghiGopTin, ct);

        var moi = await _repo.GetConversationAsync(tenantId, hoiThoai.Id, ct);
        if (moi is null || !ChatRules.BotDuocTraLoi(moi, DateTime.UtcNow)) return;

        var choXuLy = await _repo.ListPendingInboundAsync(tenantId, hoiThoai.Id, ct);
        if (choXuLy.Count == 0) return;   // luồng khác đã xử lý cụm này

        // Chỉ trả lời khi có CHỮ. Ảnh/sticker/vị trí thì ghi vào hộp thư cho nhân viên xem, bot
        // đoán bừa nội dung ảnh sẽ trả lời lạc đề.
        var cauHoi = ChatRules.GhepCum(choXuLy.Select(m => m.Body));
        await _repo.MarkProcessedAsync(tenantId, choXuLy.Select(m => m.Id), ct);
        if (string.IsNullOrWhiteSpace(cauHoi)) return;

        var traLoi = await SinhTraLoiAsync(tenantId, hoiThoai.Id, cauHoi, ct);
        if (string.IsNullOrWhiteSpace(traLoi)) return;

        var idRa = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.Ra,
            ChatSender.Ai, null, ChatKind.Chu, traLoi, null, null, ChatState.Cho, ct);
        if (idRa is null) return;

        await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.TomTat(traLoi), false, ct);
        await _repo.EnqueueOutboxAsync(tenantId, hoiThoai.Id, idRa.Value, ct);
    }

    /// <summary>
    /// Sinh câu trả lời.
    ///
    /// <para>AI hỏng thì trả <c>null</c> — <b>im lặng còn hơn gửi câu rác cho khách</b>. Hội thoại
    /// vẫn nằm trong hộp thư, nhân viên thấy và trả lời tay được.</para>
    /// </summary>
    private async Task<string?> SinhTraLoiAsync(string tenantId, long hoiThoaiId, string cauHoi,
        CancellationToken ct)
    {
        var loiDan = _cfg["Chat:SystemPrompt"] ?? MacDinhLoiDan;
        try
        {
            // Gọi từ NỀN nên không có HttpContext — phải Push thủ công, nếu không là bỏ qua hạn
            // mức tenant và log ra feature "unknown".
            using var _ = _aiCtx.Push(AiFeatures.ChatInbox, tenantId);
            var provider = _providers.Resolve(null);
            var res = await provider.CompleteAsync(new CompleteRequest(
                Prompt: cauHoi, Provider: null, Model: null,
                MaxTokens: 700, Temperature: 0.5, System: loiDan), ct);

            var text = res.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _log.LogWarning("[chat] AI trả rỗng, hội thoại={C}", hoiThoaiId);
                return null;
            }
            return text;
        }
        catch (QuotaExhaustedException)
        {
            _log.LogWarning("[chat] tenant={T} hết lượt AI — bot im, nhân viên trả lời tay", tenantId);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[chat] gọi AI hỏng, hội thoại={C}", hoiThoaiId);
            return null;
        }
    }

    /// <summary>
    /// Lời dặn mặc định cho bot.
    ///
    /// <para><b>Cấm bịa số là dòng quan trọng nhất.</b> Đợt 1 bot chưa tra được CRM, nên nếu không
    /// cấm nó sẽ tự nghĩ ra giá tour và lịch khởi hành — khách đọc xong tưởng thật, và công ty phải
    /// chịu. Thà nói "để em kiểm rồi báo lại".</para>
    /// </summary>
    private const string MacDinhLoiDan = """
        Bạn là nhân viên tư vấn của một công ty du lịch Việt Nam, đang trả lời khách qua tin nhắn.

        Cách trả lời:
        - Tiếng Việt, xưng "em", gọi khách là "anh/chị". Ngắn gọn, 2-4 câu, như tin nhắn thật.
        - Thân thiện nhưng không màu mè, không dùng emoji quá một cái.

        TUYỆT ĐỐI KHÔNG được bịa: giá tour, lịch khởi hành, số chỗ còn, khuyến mãi, chính sách hoàn
        huỷ. Bạn KHÔNG có dữ liệu thật của công ty. Gặp câu hỏi cần số liệu thì nói thật là sẽ kiểm
        tra rồi báo lại, và hỏi thêm thông tin cần thiết (ngày đi, số khách, điểm đến).

        Không hứa thay công ty. Không tự nhận đã đặt chỗ hay đã giữ chỗ cho khách.
        """;
}
