using Microsoft.AspNetCore.Http;
using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Extension cho route builder: .RequireAdminSession() → bọc endpoint kiểm header
/// X-Admin-Session, resolve AdminSession qua AdminSessionStore. Miss/expired → 401.
/// Khi pass: attach Username vào HttpContext.Items["AdminUser"] cho handler đọc.
/// </summary>
public static class RequireAdminSessionExtensions
{
    public const string HttpItemKey = "AdminUser";

    public static RouteHandlerBuilder RequireAdminSession(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
            var token = ctx.HttpContext.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            ctx.HttpContext.Items[HttpItemKey] = s.Username;
            return await next(ctx);
        });
    }

    public static RouteGroupBuilder RequireAdminSession(this RouteGroupBuilder builder)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
            var token = ctx.HttpContext.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            ctx.HttpContext.Items[HttpItemKey] = s.Username;
            return await next(ctx);
        });
    }
}
