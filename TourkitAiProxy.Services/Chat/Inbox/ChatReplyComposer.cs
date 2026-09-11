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
///   <item>đường <c>POST /conversations/{id}/goi-y</c> — nhân viên bấm xin bản nháp.</item>
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
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatReplyComposer> _log;

    public ChatReplyComposer(ChatRepository repo, ProviderRegistry providers, AiModelRegistry models,
        AiCallContext aiCtx, IConfiguration cfg, ILogger<ChatReplyComposer> log)
    { _repo = repo; _providers = providers; _models = models; _aiCtx = aiCtx; _cfg = cfg; _log = log; }

    /// <summary>
    /// Bản nháp cho NHÂN VIÊN: đọc đoạn hội thoại, lấy tin khách mới nhất làm câu hỏi, sinh câu
    /// trả lời.
    ///
    /// <para>Trả <c>null</c> ở hai ca, và chỗ gọi phải phân biệt được chúng bằng ngữ cảnh chứ
    /// không bằng giá trị: khách chưa nói gì mới (tin cuối là của mình — xem
    /// <see cref="ChatRules.TachCauHoiCuoi"/>), hoặc AI hỏng.</para>
    ///
    /// <para><b>CHỈ TRẢ CHỮ.</b> Không ghi tin vào hội thoại, không xếp hàng gửi, không đụng
    /// outbox. Đây là chốt cứng của cả tính năng — xem <c>ChatSuggestGuardTests</c>.</para>
    /// </summary>
    public async Task<string?> GoiYAsync(string tenantId, long hoiThoaiId, ChatBotSettings cfgBot,
        CancellationToken ct)
    {
        // Lấy dư rồi mới lọc — cùng lý do với worker: hàm dựng nhắc bỏ tin hỏng và tin không chữ,
        // nên xin đúng số lượt là hụt mất mấy dòng. Cộng thêm 2 cho chính câu hỏi và một tin ảnh.
        var lichSu = await _repo.ListMessagesAsync(tenantId, hoiThoaiId, cfgBot.HistoryTurns * 2 + 2, ct);

        var (cauHoi, truoc) = ChatRules.TachCauHoiCuoi(lichSu);
        if (cauHoi is null) return null;

        var nhacLai = ChatRules.BuildConversationPrompt(truoc, cauHoi, cfgBot.HistoryTurns);
        return await SinhAsync(tenantId, hoiThoaiId, nhacLai, cfgBot, ct);
    }

    /// <summary>
    /// Sinh câu trả lời.
    ///
    /// <para>AI hỏng thì trả <c>null</c> — <b>im lặng còn hơn gửi câu rác cho khách</b>. Hội thoại
    /// vẫn nằm trong hộp thư, nhân viên thấy và trả lời tay được.</para>
    /// </summary>
    public async Task<string?> SinhAsync(string tenantId, long hoiThoaiId, string cauHoi,
        ChatBotSettings cfgBot, CancellationToken ct)
    {
        // Khung an toàn: máy chủ khai đè được (Chat:SystemPrompt) để sửa nóng khi cần, còn mặc
        // định nằm trong mã. Lời dặn RIÊNG của công ty NỐI THÊM vào, không thay thế — khung chứa
        // luật chống bịa giá tour, bỏ nó là bot hứa giữ chỗ với khách thật.
        var khung = _cfg["Chat:SystemPrompt"] ?? DefaultSystemPrompt;
        var loiDan = cfgBot.BuildSystemPrompt(khung);
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
}
