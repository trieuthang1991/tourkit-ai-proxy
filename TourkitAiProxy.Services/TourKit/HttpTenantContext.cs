// Services/TourKit/HttpTenantContext.cs
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// HttpContext-backed implementation: đọc X-Session-Id header / query, lookup TkSessionStore.
/// Scoped — 1 instance per request.
/// </summary>
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _http;
    private readonly TkSessionStore _sessions;

    public HttpTenantContext(IHttpContextAccessor http, TkSessionStore sessions)
    {
        _http = http; _sessions = sessions;
    }

    public string TenantId
        => TryGetTenantId() ?? throw new InvalidOperationException(
            "Anonymous request — caller phải đảm bảo session đã được verify trước khi gọi ITenantContext.TenantId");

    public string? TryGetTenantId()
    {
        var ctx = _http.HttpContext;
        if (ctx == null) return null;
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        return _sessions.Get(sid)?.TenantId;
    }
}
