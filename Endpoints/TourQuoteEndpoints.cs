using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.TourQuotes;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// CRUD báo giá tour — persist trong dbo.TourQuotes per-tenant. Replace flow localStorage cũ.
///   POST   /api/v1/tour-quotes          — upsert (id null = create)
///   GET    /api/v1/tour-quotes          — list paginated + filter search
///   GET    /api/v1/tour-quotes/{id}     — detail (kèm full DataJson)
///   DELETE /api/v1/tour-quotes/{id}
/// </summary>
public static class TourQuoteEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize     = 200;

    public static void MapTourQuoteEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapPost("/tour-quotes", (SaveTourQuoteRequest req, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var id = repo.Save(req, sess.TenantId, sess.Username);
                var saved = repo.Get(sess.TenantId, id);
                return Results.Json(new { ok = true, id, item = saved });
            }
            catch (Exception ex) { log.LogError(ex, "Save TourQuote lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        v1.MapGet("/tour-quotes", (HttpContext ctx, TourQuoteRepository repo,
            TkSessionStore sessions, ILogger<Program> log,
            int? page, int? pageSize, string? search) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            var pIdx  = page is > 0 ? page.Value : 1;
            var pSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            try
            {
                var (items, total) = repo.List(sess.TenantId, pIdx, pSize, search);
                return Results.Json(new { items, total, page = pIdx, pageSize = pSize });
            }
            catch (Exception ex) { log.LogError(ex, "List TourQuotes lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        v1.MapGet("/tour-quotes/{id}", (string id, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            var item = repo.Get(sess.TenantId, id);
            return item == null
                ? Results.NotFound(new { error = $"Không tìm thấy báo giá {id}" })
                : Results.Json(item);
        });

        v1.MapDelete("/tour-quotes/{id}", (string id, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            return repo.Delete(sess.TenantId, id)
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "Không tìm thấy báo giá để xóa" });
        });
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
