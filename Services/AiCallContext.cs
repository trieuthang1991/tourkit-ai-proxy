using TourkitAiProxy.Services.TourKit;

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

    // ── Background workflow features — Push() từ workflow entry ──
    public const string MailAutoSync        = "mail-auto-sync";
    public const string DealAutoReview      = "deal-auto-review";
    public const string CustomerAutoReview  = "customer-auto-review";

    // ── Assistant action tools — Push() từ ActionExecutor (review_customer/score_deal) ──
    // Non-HTTP path (chạy sau khi user bấm "Xác nhận") → PHẢI Push để trừ quota tenant +
    // log đúng feature, tránh rơi vào "unknown" (xem docs class comment ở trên).
    public const string AssistantAction     = "assistant-action";
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

    public record Ctx(string Feature, string? SessionId, string? Tenant);

    public Ctx Resolve()
    {
        // Override ưu tiên: batch fire-and-forget set bằng Push() trước khi gọi provider.
        if (_override.Value != null) return _override.Value;

        var http = _accessor.HttpContext;
        if (http == null) return new Ctx(AiFeatures.Unknown, null, null);
        var path = http.Request.Path.Value ?? "";
        var feature = FeatureFromPath(path);
        var sid = http.Request.Headers["X-Session-Id"].FirstOrDefault();
        var tenant = !string.IsNullOrEmpty(sid) ? _sessions.Get(sid)?.TenantId : null;
        return new Ctx(feature, sid, tenant);
    }

    /// Set override AsyncLocal cho khối using. Background task (Task.Run / Parallel.ForEachAsync) ở trong
    /// using sẽ thấy context này khi gọi Resolve(). Restore khi Dispose.
    public IDisposable Push(string feature, string? tenant, string? sessionId = null)
    {
        var prev = _override.Value;
        _override.Value = new Ctx(feature, sessionId, tenant);
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
