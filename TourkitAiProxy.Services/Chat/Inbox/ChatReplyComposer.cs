using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Bộ sinh câu trả lời cho khách — MỘT chỗ cho cả hai người dùng nó:
/// <list type="bullet">
///   <item>worker <see cref="ChatInboundService"/> — bot tự trả lời khi khách nhắn;</item>
///   <item>đường <c>POST /conversations/{id}/suggest</c> — nhân viên bấm xin bản nháp.</item>
/// </list>
///
/// <para>Tách ra để hai đường dùng CHUNG một khung an toàn (cấm bịa giá, lịch, số chỗ), chung lời
/// dặn của công ty, chung model và chung cách đếm hạn mức. Để bộ sinh nằm riêng trong worker thì
/// đường gợi ý phải dựng lối thứ hai — và hai lối thì sớm muộn lệch nhau, mà lệch ở đây nghĩa là
/// bản nháp nhân viên gửi đi không còn chịu luật cấm bịa số.</para>
/// </summary>
public class ChatReplyComposer
{
    private readonly ChatRepository _repo;
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly AiCallContext _aiCtx;
    private readonly ChatTourLookup _tour;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatReplyComposer> _log;

    public ChatReplyComposer(ChatRepository repo, ProviderRegistry providers, AiModelRegistry models,
        AiCallContext aiCtx, ChatTourLookup tour, IConfiguration cfg, ILogger<ChatReplyComposer> log)
    { _repo = repo; _providers = providers; _models = models; _aiCtx = aiCtx; _tour = tour; _cfg = cfg; _log = log; }

    /// <summary>
    /// Bản nháp cho NHÂN VIÊN: đọc đoạn hội thoại, lấy tin khách mới nhất làm câu hỏi, sinh câu
    /// trả lời.
    ///
    /// <para><b>CHỈ TRẢ CHỮ.</b> Không ghi tin vào hội thoại, không xếp hàng gửi, không đụng
    /// outbox. Đây là chốt cứng của cả tính năng — xem <c>ChatSuggestGuardTests</c>.</para>
    ///
    /// <para>Trả về một <b>lý do có tên</b> chứ không phải <c>null</c> trần: bốn ca không sinh
    /// được chữ cần bốn câu khác nhau trên màn hình, mà một giá trị null thì chỗ gọi không phân
    /// biệt nổi — và sẽ lại tự đoán, mỗi chỗ đoán một kiểu.</para>
    /// </summary>
    /// <param name="tone">Mã hướng soạn do nhân viên chọn bằng chip (formal · callback · ask-info
    /// · apologize), hoặc <c>null</c> = soạn thường. Mã được dịch sang câu dặn ở
    /// <see cref="ChatRules.SuggestionToneHint"/>; ở đây KHÔNG bao giờ nhận chữ dặn thẳng từ
    /// ngoài — xem chú thích hàm đó về đường tiêm lời nhắc.</param>
    /// <param name="sessionId">Phiên TourKit của nhân viên đang bấm nút. Chỉ được dùng khi công ty
    /// bật "theo quyền nhân viên"; tắt thì lượt này vẫn xem cả kho — phạm vi do ô cấu hình quyết,
    /// không do việc ai bấm nút.</param>
    public async Task<SuggestionOutcome> SuggestAsync(string tenantId, ChatConversation hoiThoai,
        ChatBotSettings cfgBot, CancellationToken ct, string? tone = null, string? sessionId = null)
    {
        // Lấy dư rồi mới lọc — cùng lý do với worker: hàm dựng nhắc bỏ tin hỏng và tin không chữ,
        // nên xin đúng số lượt là hụt mất mấy dòng. Cộng thêm 2 cho chính câu hỏi và một tin ảnh.
        var lichSu = await _repo.ListMessagesAsync(tenantId, hoiThoai.Id, cfgBot.HistoryTurns * 2 + 2, ct);

        var (cauHoi, hoiLuc, truoc) = ChatRules.SplitLatestQuestion(lichSu);
        if (cauHoi is null) return new(Suggestion.NothingNewFromCustomer, null);

        // RANH GIỚI bot ↔ gợi ý. Bot lo lượt trả lời tự động, gợi ý lo những lượt bot không lo —
        // và không bao giờ hai bên cùng nói. Xem ChatRules.BotIsAboutToReply.
        if (ChatRules.BotIsAboutToReply(hoiThoai, cfgBot.Enabled, hoiLuc, DateTime.UtcNow))
            return new(Suggestion.BotIsHandlingIt, null);

        var nhacLai = ChatRules.BuildConversationPrompt(truoc, cauHoi, cfgBot.HistoryTurns);

        // Hướng soạn nối vào CUỐI lời nhắc, sau đoạn hội thoại. Đặt trước thì đoạn hội thoại —
        // vốn dài hơn nhiều — đẩy nó ra khỏi tầm chú ý của mô hình, và chip bấm vào như không.
        // Dịch từ MÃ, không lấy chữ do ngoài gửi vào: xem ChatRules.SuggestionToneHint.
        if (ChatRules.SuggestionToneHint(tone) is { } dan)
            nhacLai += "\n\nYêu cầu riêng cho câu trả lời này: " + dan;

        // Tra tour theo CÂU KHÁCH VỪA HỎI, không theo cả đoạn hội thoại đã ghép: bộ chọn API cần
        // đúng một câu để rút điểm đến và ngày ra: ném cả đoạn vào là nó bám vào chuyện cũ.
        var chu = await GenerateAsync(tenantId, hoiThoai.Id, nhacLai, cfgBot, ct, cauHoi, sessionId,
            hoiThoai.AssignedUsername);

        // GenerateAsync nuốt mọi lỗi và trả null — đúng cho worker (im còn hơn gửi rác), nhưng ở đây
        // phải nói ra: nhân viên vừa bấm một cái nút và đang chờ chữ hiện lên.
        return chu is null ? new(Suggestion.AiFailed, null) : new(Suggestion.Ok, chu);
    }

    /// <summary>
    /// Sinh câu trả lời.
    ///
    /// <para>AI hỏng thì trả <c>null</c> — <b>im lặng còn hơn gửi câu rác cho khách</b>. Hội thoại
    /// vẫn nằm trong hộp thư, nhân viên thấy và trả lời tay được.</para>
    /// </summary>
    /// <param name="cauKhachHoi">RIÊNG câu khách vừa hỏi, tách khỏi <paramref name="cauHoi"/> (vốn
    /// là cả đoạn hội thoại đã ghép). Chỉ dùng để chọn API tra tour — bộ chọn cần một câu để rút
    /// điểm đến và ngày, ném cả đoạn vào là nó bám chuyện cũ. <c>null</c> = không tra.</param>
    /// <param name="sessionId">Phiên TourKit để tra dữ liệu. <c>null</c> ở đường bot tự trả lời →
    /// rơi sang tài khoản dịch vụ của công ty; chưa khai thì không tra.</param>
    public async Task<string?> GenerateAsync(string tenantId, long hoiThoaiId, string cauHoi,
        ChatBotSettings cfgBot, CancellationToken ct, string? cauKhachHoi = null,
        string? sessionId = null, string? nguoiPhuTrach = null)
    {
        // Khung an toàn: máy chủ khai đè được (Chat:SystemPrompt) để sửa nóng khi cần, còn mặc
        // định nằm trong mã. Lời dặn RIÊNG của công ty NỐI THÊM vào, không thay thế — khung chứa
        // luật chống bịa giá tour, bỏ nó là bot hứa giữ chỗ với khách thật.
        // Tra dữ liệu tour TRƯỚC khi chốt khung an toàn: có bảng số thật trong tay thì luật đổi từ
        // "cấm nói mọi số" sang "được nói đúng số trong bảng". Hai khung không được trộn — một lời
        // nhắc vừa bảo "bạn KHÔNG có dữ liệu" vừa đính kèm dữ liệu là tự mâu thuẫn, và model xử lý
        // mâu thuẫn bằng cách chọn bừa một vế.
        ChatTourLookup.KetQua? duLieuTour = null;
        if (!string.IsNullOrWhiteSpace(cauKhachHoi))
            duLieuTour = await _tour.TraAsync(tenantId, cfgBot, cauKhachHoi!, sessionId,
                nguoiPhuTrach, ct);

        var khungMacDinh = duLieuTour is null ? DefaultSystemPrompt : SystemPromptCoDuLieu;
        var khung = _cfg["Chat:SystemPrompt"] ?? khungMacDinh;
        var loiDan = cfgBot.BuildSystemPrompt(khung);

        if (duLieuTour is not null)
        {
            cauHoi = $"DỮ LIỆU TOUR THẬT (nguồn: {duLieuTour.ToolTitle}, lấy lúc "
                   + $"{DateTime.UtcNow.AddHours(7):HH:mm dd/MM/yyyy} giờ Việt Nam):\n"
                   + duLieuTour.Json + "\n\n" + cauHoi;
            _log.LogInformation("[chat] tenant={T} hội thoại={C} trả lời KÈM dữ liệu tour ({Nguon})",
                tenantId, hoiThoaiId, duLieuTour.ToolTitle);
        }

        try
        {
            // Gọi từ NỀN nên không có HttpContext — phải Push thủ công, nếu không là bỏ qua hạn
            // mức tenant và log ra feature "unknown".
            using var _ = _aiCtx.Push(AiFeatures.ChatInbox, tenantId);

            // Hỏi registry chứ KHÔNG truyền null. Truyền null thì provider tự chọn model mặc
            // định của nó — đo được trên lịch sử dùng thật: claude-sonnet-4-5, đắt nhất cả hệ,
            // và bỏ qua luôn cả Models:Primary:Model. Đây lại là tính năng duy nhất nói thẳng
            // với khách hàng thật, nên là chỗ cần chốt model nhất chứ không phải chỗ được thả nổi.
            var resolved = _models.Resolve(AiFeature.ChatInbox);
            var provider = _providers.Resolve(resolved.Provider);
            var res = await provider.CompleteAsync(new CompleteRequest(
                Prompt: cauHoi, Provider: provider.Id, Model: resolved.Model,
                MaxTokens: 700, Temperature: 0.5, System: loiDan, ApiKey: resolved.ApiKey), ct);

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
    private const string DefaultSystemPrompt = """
        Bạn là nhân viên tư vấn của một công ty du lịch Việt Nam, đang trả lời khách qua tin nhắn.

        Cách trả lời:
        - Tiếng Việt, xưng "em", gọi khách là "anh/chị". Ngắn gọn, 2-4 câu, như tin nhắn thật.
        - Thân thiện nhưng không màu mè, không dùng emoji quá một cái.

        TUYỆT ĐỐI KHÔNG được bịa: giá tour, lịch khởi hành, số chỗ còn, khuyến mãi, chính sách hoàn
        huỷ. Bạn KHÔNG có dữ liệu thật của công ty. Gặp câu hỏi cần số liệu thì nói thật là sẽ kiểm
        tra rồi báo lại, và hỏi thêm thông tin cần thiết (ngày đi, số khách, điểm đến).

        Không hứa thay công ty. Không tự nhận đã đặt chỗ hay đã giữ chỗ cho khách.
        """;

    /// <summary>
    /// Khung an toàn dùng KHI đã tra được dữ liệu tour thật.
    ///
    /// <para>Khác <see cref="DefaultSystemPrompt"/> ở đúng một điểm, và điểm đó là cả tính năng:
    /// <b>được nói số — nhưng chỉ số có trong bảng</b>. Luật cấm bịa không hề nới ra, nó chỉ đổi
    /// mốc: trước là "cấm mọi số vì không có dữ liệu", nay là "cấm mọi số NGOÀI bảng".</para>
    ///
    /// <para>Hai dòng cuối là hai bẫy đã biết, không phải lời khuyên chung chung. Bảng luôn có thể
    /// bị cắt bớt (khoá <c>_truncated</c>) nên kết luận "công ty không có tour đó" từ một danh sách
    /// cụt là phủ định sai — nghe rất thuyết phục mà sai. Và số chỗ còn thì đổi theo phút, nên phải
    /// kèm mốc thời gian chứ không nói như một sự thật vĩnh viễn.</para>
    /// </summary>
    private const string SystemPromptCoDuLieu = """
        Bạn là nhân viên tư vấn của một công ty du lịch Việt Nam, đang trả lời khách qua tin nhắn.

        Cách trả lời:
        - Tiếng Việt, xưng "em", gọi khách là "anh/chị". Ngắn gọn, 2-4 câu, như tin nhắn thật.
        - Thân thiện nhưng không màu mè, không dùng emoji quá một cái.
        - Danh sách dài thì kể 3-5 mục tiêu biểu rồi mời khách cho biết thêm nhu cầu.
        - Không dùng markdown (**, ##). Xuống dòng bằng dòng trống, gạch đầu dòng bằng "-".

        Phần đầu tin nhắn có khối DỮ LIỆU TOUR THẬT lấy từ hệ thống của công ty.
        - Được phép nói giá, ngày khởi hành, số chỗ còn, tên tour — MIỄN LÀ có trong khối đó.
        - TUYỆT ĐỐI KHÔNG bịa, không nội suy, không làm tròn theo cảm tính bất kỳ con số nào KHÔNG
          có trong khối đó. Thiếu thông tin thì nói thật là sẽ kiểm rồi báo lại.
        - KHÔNG nhắc tới "dữ liệu", "hệ thống", "API" hay tên kỹ thuật nào với khách.

        Khối dữ liệu có thể đã bị cắt bớt: nếu không thấy thứ khách hỏi, nói là "em chưa thấy trong
        danh sách đang mở, để em kiểm thêm" — KHÔNG khẳng định công ty không có.

        Số chỗ còn thay đổi liên tục: nói kèm mốc thời gian và nhắc khách xác nhận lại trước khi
        chốt. Không hứa thay công ty. Không tự nhận đã đặt chỗ hay đã giữ chỗ cho khách.
        """;
}

/// <summary>Vì sao lượt xin gợi ý không ra chữ. Mỗi giá trị là một câu khác nhau trên màn hình.</summary>
public enum Suggestion : short
{
    /// <summary>Có bản nháp.</summary>
    Ok = 0,
    /// <summary>Tin mới nhất là của mình — khách chưa hỏi gì thêm để mà trả lời.</summary>
    NothingNewFromCustomer = 1,
    /// <summary>Bot đang lo câu này. Muốn tự trả lời thì tạm dừng bot trước.</summary>
    BotIsHandlingIt = 2,
    /// <summary>Gọi AI hỏng hoặc hết lượt.</summary>
    AiFailed = 3,
}

/// <param name="Outcome">Vì sao có/không có chữ.</param>
/// <param name="Text">Bản nháp — chỉ khác null khi <paramref name="Outcome"/> là <see cref="Suggestion.Ok"/>.</param>
public record SuggestionOutcome(Suggestion Outcome, string? Text);
