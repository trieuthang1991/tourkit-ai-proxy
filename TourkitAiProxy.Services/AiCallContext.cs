using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Services;

/// <summary>
/// Tên feature cho AI usage log + quota tag. Dùng trong <see cref="AiCallContext.Push"/> khi endpoint /
/// workflow gọi AI. Ghi vào cột <c>dbo.AiUsageHistory.Feature</c>.
///
/// <b>NGUYÊN TẮC:</b> Đổi giá trị chuỗi = đổi tag lịch sử → phá query admin dashboard / báo cáo cost.
/// Chỉ thêm mới, KHÔNG đổi tên chuỗi các key cũ. Nếu cần rename → phải backfill DB.
/// </summary>
public static class AiFeatures
{
    // ── HTTP endpoint features — auto-detect qua AiCallContext.FeatureFromPath ──
    public const string Chat            = "chat";
    public const string Completions     = "completions";
    public const string Deals           = "deals";              // /deals/analyze + /deals/{id}/rescore
    public const string Reviews         = "reviews";            // /reviews/batch
    public const string Mail            = "mail";
    public const string Visa            = "visa";
    public const string TourBuilder     = "tour-builder";
    public const string NccImport       = "ncc-import";
    public const string Widget          = "widget";
    public const string WidgetCrm       = "widget-crm";
    public const string WidgetCrmPlan   = "widget-crm-plan";
    public const string Other           = "other";
    public const string Unknown         = "unknown";
    /// Lệnh gọi thử để KIỂM key AI riêng của công ty trước khi lưu (BYO). Chạy bằng key của khách nên
    /// không trừ lượt — xem TenantAiKeyValidator.
    public const string ByoKeyValidate  = "byo-key-validate";

    // ── Các cụm ĐI QUA /completions — khai bằng header X-Ai-Feature ──────────────────
    //
    // /completions là cổng gọi AI dùng chung, có từ thời proxy chỉ làm đúng một việc. Mọi thứ đi
    // qua nó đều rơi chung vào "completions", nên nhìn cột đó tăng thì không biết do trình báo giá
    // chạy nhiều hay do một cái nút bị bấm nhiều — tức là con số có mà không dùng được.
    //
    // Bốn mã dưới đây tách đúng bốn việc khác nhau đang dùng chung cổng đó. Bên gọi tự khai; khai
    // sai tên thì rơi về "completions" chứ không tạo mã mới — xem CompletionsFeature.
    /// Trình Tính giá Tour — sinh hành trình, bóc tách, dựng bảng giá.
    public const string Quote           = "quote";
    /// Bài đăng marketing sinh từ tour đã báo giá.
    public const string QuoteMarketing  = "quote-marketing";
    /// Nút "AI gợi ý" trong các hộp thoại nhập liệu.
    public const string AiSuggest       = "ai-suggest";
    /// Soạn tin Zalo gửi báo giá cho khách.
    public const string ZaloCompose     = "zalo-compose";

    /// <summary>
    /// Đọc nhãn cụm do bên gọi khai ở header <c>X-Ai-Feature</c>.
    ///
    /// <para><b>Tập ĐÓNG, cố ý.</b> Nhận chuỗi tự do thì bảng chi phí mọc thêm một hàng mỗi lần ai
    /// đó gõ khác đi một chữ, và đến lúc cần đọc thì lại không gộp được. Tên lạ → trả <c>null</c>
    /// để rơi về <c>completions</c>, tức đúng hành vi cũ.</para>
    ///
    /// <para>Chỉ có tác dụng trên đường <c>/completions</c> — xem <c>Resolve</c>. Cho khai trên mọi
    /// đường là mở cửa cho một lượt gọi tự dán nhãn của cụm khác, rồi hạn mức và chi phí ghi sai chỗ.</para>
    /// </summary>
    public static string? CompletionsFeature(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        Quote          => Quote,
        QuoteMarketing => QuoteMarketing,
        AiSuggest      => AiSuggest,
        ZaloCompose    => ZaloCompose,
        _              => null,
    };

    // ── Background workflow features — Push() từ workflow entry ──
    public const string MailAutoSync        = "mail-auto-sync";
    public const string DealAutoReview      = "deal-auto-review";
    public const string CustomerAutoReview  = "customer-auto-review";
    // Bản tin chủ động (ceo-brief). Feature riêng để tách chi phí AI của bản tin tự động khỏi
    // thao tác tay trong dbo.AiUsageHistory. Bản tin sales không gọi AI nên không xuất hiện ở đây.
    public const string Digest              = "digest";

    // ── Assistant action tools — Push() từ ActionExecutor (review_customer/prepare_meeting/score_deal) ──
    // Non-HTTP path (chạy sau khi user bấm "Xác nhận") → PHẢI Push để trừ quota tenant +
    // log đúng feature, tránh rơi vào "unknown" (xem docs class comment ở trên).
    public const string AssistantAction     = "assistant-action";

    /// Bot trả lời khách trong hộp thư chat đa kênh. Feature RIÊNG (không gộp vào "chat" của Trợ lý
    /// số liệu) vì hai thứ khác hẳn về lượng và về người trả tiền: trợ lý là nhân viên tự hỏi, còn
    /// cái này là KHÁCH nhắn tới — số lượt do người ngoài quyết định.
    public const string ChatInbox           = "chat-inbox";

    /// Lượt chọn API cho trợ lý hộp thư chat tra dữ liệu tour. Tách khỏi <see cref="ChatInbox"/> vì
    /// nó là lượt AI THỨ HAI cho cùng một tin khách — gộp chung thì nhìn vào dbo.AiUsageHistory sẽ
    /// tưởng lượng tin tăng gấp đôi, trong khi thật ra là mỗi tin tốn hai lượt.
    public const string ChatInboxTourPlan   = "chat-inbox-tour-plan";

    /// Đọc tên trạng thái của công ty để chọn sẵn cấu hình. KHÔNG trừ quota — xem ghi chú
    /// <see cref="AiCallContext.Ctx.FreeOfQuota"/>.
    public const string StatusSemantics     = "status-semantics";
}

/// Trích context từ HttpContext cho AI usage logging:
///   • feature từ path (/api/v1/visa/* → visa, /api/v1/deals/* → deals…)
///   • sessionId từ header X-Session-Id
///   • tenantId từ TkSessionStore (nếu sessionId hợp lệ)
///
/// Provider gọi `Resolve()` mỗi lần CompleteAsync để gắn vào log.
///
/// AsyncLocal override (`Push`): batch fire-and-forget (DealBatchService, Reviews/BatchService) sau khi
/// endpoint trả về thì HttpContext đã null → Resolve sẽ trả unknown/null/null, AI usage log sẽ thấy
/// feature=unknown + tenant=null + bypass quota. Endpoint kick off batch phải gọi
/// `using var _ = _ctx.Push(AiFeatures.Deals, tenant, sessionId)` trước khi gọi provider → AsyncLocal flow qua
/// Task.Run/Parallel.ForEachAsync nên background work vẫn có context đúng.
public class AiCallContext
{
    private readonly IHttpContextAccessor _accessor;
    private readonly TkSessionStore _sessions;
    private static readonly AsyncLocal<Ctx?> _override = new();

    public AiCallContext(IHttpContextAccessor accessor, TkSessionStore sessions)
    {
        _accessor = accessor; _sessions = sessions;
    }

    /// <param name="FreeOfQuota">
    /// Lời gọi THIẾT LẬP, không phải việc người dùng làm → không chặn và không trừ lượt.
    ///
    /// <para>Dành cho đúng một loại: hệ thống tự hỏi AI để dựng cấu hình mặc định (đọc tên trạng
    /// thái của công ty). Chặn nó lại thì hậu quả lệch hẳn so với cái tiết kiệm được: mất một lượt
    /// AI rẻ, đổi lấy một bộ lọc trạng thái vô tác dụng — bản tin nhắc cả cơ hội đã đóng, và người
    /// dùng không hiểu vì sao (gặp thật ở một tenant hết quota: 14 trạng thái đặt tên riêng, lưới
    /// đỡ từ khoá không loại được cái nào).</para>
    ///
    /// <para>VẪN ghi vào AiUsageHistory — miễn tính tiền không có nghĩa là giấu chi phí.</para>
    ///
    /// <para>Chống lạm dụng: chỉ miễn cho lần hỏi TỰ ĐỘNG (cache trống). Người bấm "Phân loại lại"
    /// là thao tác chủ động → tính quota như mọi lời gọi khác.</para>
    /// </param>
    public record Ctx(string Feature, string? SessionId, string? Tenant, bool FreeOfQuota = false);

    public Ctx Resolve()
    {
        // Override ưu tiên: batch fire-and-forget set bằng Push() trước khi gọi provider.
        if (_override.Value != null) return _override.Value;

        var http = _accessor.HttpContext;
        if (http == null) return new Ctx(AiFeatures.Unknown, null, null);
        var path = http.Request.Path.Value ?? "";
        var feature = FeatureFromPath(path);

        // CHỈ trên /completions mới cho bên gọi tự khai cụm. Các đường khác đã có tên riêng suy ra
        // từ chính đường dẫn — cho khai đè ở đó là mở cửa để một lượt gọi dán nhãn của cụm khác,
        // rồi hạn mức trừ nhầm chỗ và bảng chi phí chỉ về sai hướng.
        if (feature == AiFeatures.Completions
            && AiFeatures.CompletionsFeature(http.Request.Headers["X-Ai-Feature"].FirstOrDefault()) is { } khai)
            feature = khai;

        var sid = http.Request.Headers["X-Session-Id"].FirstOrDefault();
        var tenant = !string.IsNullOrEmpty(sid) ? _sessions.Get(sid)?.TenantId : null;
        return new Ctx(feature, sid, tenant);
    }

    /// Set override AsyncLocal cho khối using. Background task (Task.Run / Parallel.ForEachAsync) ở trong
    /// using sẽ thấy context này khi gọi Resolve(). Restore khi Dispose.
    public IDisposable Push(string feature, string? tenant, string? sessionId = null,
        bool freeOfQuota = false)
    {
        var prev = _override.Value;
        _override.Value = new Ctx(feature, sessionId, tenant, freeOfQuota);
        return new Pop(prev);
    }

    private sealed class Pop : IDisposable
    {
        private readonly Ctx? _prev;
        public Pop(Ctx? prev) { _prev = prev; }
        public void Dispose() => _override.Value = _prev;
    }

    private static string FeatureFromPath(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.Contains("/visa/"))         return AiFeatures.Visa;
        if (p.Contains("/deals/"))        return AiFeatures.Deals;
        if (p.Contains("/tour-builder/")) return AiFeatures.TourBuilder;
        if (p.Contains("/mail/"))         return AiFeatures.Mail;
        if (p.Contains("/ncc-import/"))   return AiFeatures.NccImport;
        if (p.Contains("/reviews/"))      return AiFeatures.Reviews;
        if (p.Contains("/chat"))          return AiFeatures.Chat;
        if (p.Contains("/completions"))   return AiFeatures.Completions;
        return AiFeatures.Other;
    }
}
