using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin governance — login user/pass → in-mem session token.
///   POST /api/v1/admin/auth/login   {username, password}            → {token, username, expiresAt}
///   POST /api/v1/admin/auth/logout  header X-Admin-Session           → {ok}
///   GET  /api/v1/admin/auth/me      header X-Admin-Session           → {username, expiresAt}
///
/// Phân biệt với /api/v1/admin/quota/* (giữ Admin:Token cũ cho webhook Tingee).
/// </summary>
public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/auth");

        g.MapPost("/login", (LoginReq req, AdminUserStore users, AdminSessionStore sessions) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new { error = "Thiếu username/password" }, statusCode: 400);
            if (!users.Authenticate(req.Username.Trim(), req.Password))
                return Results.Json(new { error = "Sai username hoặc password" }, statusCode: 401);
            var s = sessions.Create(req.Username.Trim());
            return Results.Json(new { token = s.Token, username = s.Username, expiresAt = sessions.ExpiresAt(s) });
        });

        g.MapPost("/logout", (HttpContext ctx, AdminSessionStore sessions) =>
        {
            var token = ctx.Request.Headers["X-Admin-Session"].FirstOrDefault();
            sessions.Remove(token);
            return Results.Json(new { ok = true });
        });

        g.MapGet("/me", (HttpContext ctx, AdminSessionStore sessions) =>
        {
            var token = ctx.Request.Headers["X-Admin-Session"].FirstOrDefault();
            var s = sessions.Get(token);
            if (s == null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            return Results.Json(new { username = s.Username, expiresAt = sessions.ExpiresAt(s) });
        });

        return routes;
    }

    public record LoginReq(string Username, string Password);
}
