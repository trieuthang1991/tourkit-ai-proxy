using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services;

/// Trích context từ HttpContext cho AI usage logging:
///   • feature từ path (/api/v1/visa/* → visa, /api/v1/deals/* → deals…)
///   • sessionId từ header X-Session-Id
///   • tenantId từ TkSessionStore (nếu sessionId hợp lệ)
///
/// Provider gọi `Resolve()` mỗi lần CompleteAsync để gắn vào log.
public class AiCallContext
{
    private readonly IHttpContextAccessor _accessor;
    private readonly TkSessionStore _sessions;

    public AiCallContext(IHttpContextAccessor accessor, TkSessionStore sessions)
    {
        _accessor = accessor; _sessions = sessions;
    }

    public record Ctx(string Feature, string? SessionId, string? Tenant);

    public Ctx Resolve()
    {
        var http = _accessor.HttpContext;
        if (http == null) return new Ctx("unknown", null, null);
        var path = http.Request.Path.Value ?? "";
        var feature = FeatureFromPath(path);
        var sid = http.Request.Headers["X-Session-Id"].FirstOrDefault();
        var tenant = !string.IsNullOrEmpty(sid) ? _sessions.Get(sid)?.TenantId : null;
        return new Ctx(feature, sid, tenant);
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
