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

        // POST /tour-quotes — COMMIT vào SQL (1 lần ghi DB). Dùng cho nút "Lưu báo giá" của user.
        v1.MapPost("/tour-quotes", (SaveTourQuoteRequest req, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var id = repo.Save(req, sess.TenantId, sess.Username);
                // Commit endpoint cũng dọn draft Redis (nếu user save bằng commit thẳng không qua draft).
                var saved = repo.Commit(sess.TenantId, id, sess.Username);
                return Results.Json(new { ok = true, id, item = saved, committed = true });
            }
            catch (Exception ex) { log.LogError(ex, "Save TourQuote lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // POST /tour-quotes/draft — AUTO-SAVE vào Redis (TTL 24h), KHÔNG đụng DB.
        // FE debounce 1.5s sau mỗi keystroke → gọi endpoint này. Trả lại id để FE giữ URL ?id=.
        v1.MapPost("/tour-quotes/draft", (SaveTourQuoteRequest req, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var id = repo.SaveDraft(req, sess.TenantId, sess.Username);
                return Results.Json(new { ok = true, id, isDraft = true, savedAt = DateTime.UtcNow.ToString("o") });
            }
            catch (Exception ex) { log.LogError(ex, "SaveDraft lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // POST /tour-quotes/{id}/commit — flush draft Redis → SQL (1 lần ghi DB).
        // Dùng khi FE muốn "lock-in" mà không cần re-send full data (đã có sẵn ở Redis).
        v1.MapPost("/tour-quotes/{id}/commit", (string id, HttpContext ctx,
            TourQuoteRepository repo, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var item = repo.Commit(sess.TenantId, id, sess.Username);
                if (item == null) return Results.NotFound(new { error = "Không tìm thấy draft + SQL row" });
                return Results.Json(new { ok = true, id, item, committed = true });
            }
            catch (Exception ex) { log.LogError(ex, "Commit TourQuote lỗi"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
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
            if (item == null) return Results.NotFound(new { error = $"Không tìm thấy báo giá {id}" });
            // FE distinguish: nếu có draftSavedAt → user chưa commit; show badge "đang nháp"
            var draftAt = repo.GetDraftSavedAt(sess.TenantId, id);
            return Results.Json(new { item, draftSavedAt = draftAt, isDraft = draftAt != null });
        });

        // PUBLIC viewer — KHÔNG cần session. Khách bấm link từ Zalo/SMS → trả báo giá full.
        // Id là Guid 32-hex nên đủ khó guess. Không leak danh sách (chỉ Get-by-id).
        v1.MapGet("/tour-quotes/{id}/public", (string id, TourQuoteRepository repo) =>
        {
            var q = repo.GetPublic(id);
            return q == null
                ? Results.NotFound(new { error = "Báo giá không tồn tại hoặc đã bị xóa" })
                : Results.Json(new { item = q });
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
