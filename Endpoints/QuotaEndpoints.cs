using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Quota AI per-tenant.
///   GET  /api/v1/quota                       — snapshot tenant hiện tại (cần X-Session-Id)
///   GET  /api/v1/admin/quota                 — toàn bộ tenant (admin)
///   POST /api/v1/admin/quota/{tenant}/topup  — cộng thêm lượt cho tenant (admin)
///
/// Admin protect: nếu `Admin:Token` cấu hình trong appsettings → yêu cầu header `X-Admin-Token` khớp.
/// Không cấu hình → endpoint admin mở (dev mode).
public static class QuotaEndpoints
{
    public static void MapQuotaEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── User: tenant của mình ───────────────────────────────────────────────
        v1.MapGet("/quota", (HttpContext ctx, TenantQuotaStore store, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            return Results.Json(store.Snapshot(sess.TenantId));
        });

        // ─── Admin: liệt kê toàn bộ tenant ───────────────────────────────────────
        v1.MapGet("/admin/quota", (HttpContext ctx, TenantQuotaStore store, IConfiguration cfg) =>
        {
            if (!AdminOk(ctx, cfg)) return Results.Json(new { error = "Admin token sai/thiếu" }, statusCode: 403);
            return Results.Json(new { items = store.ListAll() });
        });

        // ─── Admin: top-up cho 1 tenant ──────────────────────────────────────────
        v1.MapPost("/admin/quota/{tenant}/topup", (string tenant, TopUpReq req, HttpContext ctx,
            TenantQuotaStore store, IConfiguration cfg) =>
        {
            if (!AdminOk(ctx, cfg)) return Results.Json(new { error = "Admin token sai/thiếu" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(tenant)) return Results.BadRequest(new { error = "tenant trống" });
            if (req.Amount <= 0) return Results.BadRequest(new { error = "amount phải > 0" });
            try
            {
                var snap = store.TopUp(tenant, req.Amount);
                return Results.Json(snap);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });
    }

    public record TopUpReq(int Amount);

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
        ?? ctx.Request.Query["sessionId"].FirstOrDefault();

    private static bool AdminOk(HttpContext ctx, IConfiguration cfg)
    {
        var expected = cfg["Admin:Token"];
        if (string.IsNullOrWhiteSpace(expected)) return true;   // dev mode, không cấu hình → open
        var got = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        return string.Equals(expected, got, StringComparison.Ordinal);
    }
}
