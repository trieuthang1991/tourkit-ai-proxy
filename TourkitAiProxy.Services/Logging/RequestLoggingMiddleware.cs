using System.Diagnostics;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Logging;

/// <summary>
/// Log 1 line/request: <c>{Method} {Path} → {Status} ({Ms}ms) ip={Ip}</c>.
///  • 2xx/3xx → Information · 4xx → Warning · 5xx → Error
///  • Resolve <c>TenantId</c> từ <c>X-Session-Id</c> header (hoặc <c>?sessionId=</c>) → push vào
///    log4net LogicalThreadContext để mọi log tiếp theo trong request tag đúng tenant.
///  • KHÔNG đọc body (giữ stream nguyên vẹn cho endpoint).
///  • Bỏ qua static files (giảm noise) — CSS/JS/font/image path prefix quen thuộc.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _log;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
    {
        _next = next; _log = log;
    }

    public async Task InvokeAsync(HttpContext context, TkSessionStore sessions)
    {
        var path = context.Request.Path.Value ?? "";
        // Skip static files noise — chỉ log request có ý nghĩa.
        if (IsStaticAsset(path))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var method = context.Request.Method;
        var fullPath = path + context.Request.QueryString;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        // Session id header/query — không đọc body để tránh consume stream.
        var sid = context.Request.Headers["X-Session-Id"].FirstOrDefault()
                  ?? context.Request.Query["sessionId"].FirstOrDefault();
        string? tenant = null;
        if (!string.IsNullOrEmpty(sid))
        {
            try
            {
                tenant = sessions.Get(sid)?.TenantId;
                if (!string.IsNullOrEmpty(tenant))
                    log4net.LogicalThreadContext.Properties["TenantId"] = tenant;
            }
            catch { /* session resolve fail không được phá request */ }
        }

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var status = context.Response.StatusCode;
            const string msg = "{Method} {Path} → {Status} ({Ms}ms) tenant={Tenant} ip={Ip}";
            var t = tenant ?? "-";
            if (status >= 500) _log.LogError(msg, method, fullPath, status, sw.ElapsedMilliseconds, t, ip);
            else if (status >= 400) _log.LogWarning(msg, method, fullPath, status, sw.ElapsedMilliseconds, t, ip);
            else _log.LogInformation(msg, method, fullPath, status, sw.ElapsedMilliseconds, t, ip);
        }
    }

    /// Static file heuristic — path chứa segment quen thuộc / có extension asset.
    /// Không log những request này để tránh spam (100+ lần/page load).
    private static bool IsStaticAsset(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // extension check nhanh
        var dot = path.LastIndexOf('.');
        if (dot > 0 && dot < path.Length - 1)
        {
            var ext = path[(dot + 1)..].ToLowerInvariant();
            if (ext is "js" or "jsx" or "css" or "map" or "ico" or "png" or "jpg" or "jpeg"
                       or "gif" or "svg" or "webp" or "woff" or "woff2" or "ttf" or "eot" or "html")
                return true;
        }
        return path.StartsWith("/dist/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/pages/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/components/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/core/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/steps/", StringComparison.OrdinalIgnoreCase);
    }
}
