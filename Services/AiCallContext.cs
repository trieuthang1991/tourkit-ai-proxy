using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services;

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
/// `using var _ = _ctx.Push("deals", tenant, sessionId)` trước khi gọi provider → AsyncLocal flow qua
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
        if (http == null) return new Ctx("unknown", null, null);
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
        if (p.Contains("/visa/"))         return "visa";
        if (p.Contains("/deals/"))        return "deals";
        if (p.Contains("/tour-builder/")) return "tour-builder";
        if (p.Contains("/mail/"))         return "mail";
        if (p.Contains("/reviews/"))      return "reviews";
        if (p.Contains("/chat"))          return "chat";
        if (p.Contains("/completions"))   return "completions";
        return "other";
    }
}
