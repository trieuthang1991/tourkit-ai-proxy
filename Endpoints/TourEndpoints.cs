using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Store;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Wizard server-side: nháp tour (Redis/file theo tenant) + NCC thật (proxy TourKit).
///
///   GET    /api/v1/tours                       — list nháp tour của công ty
///   GET    /api/v1/tours/{id}                  — 1 nháp
///   POST   /api/v1/tours                       — lưu nháp (tự sinh id nếu thiếu)
///   DELETE /api/v1/tours/{id}                  — xoá nháp
///   GET    /api/v1/ncc/categories              — loại dịch vụ (Khách sạn, Vận chuyển…)
///   GET    /api/v1/ncc/providers?serviceId=|?marketId= — NCC theo loại DV / theo thị trường
///   GET    /api/v1/ncc/providers/{id}/services?categoryId= — bảng giá hợp đồng của 1 NCC
public static class TourEndpoints
{
    private const string COLL = "tours";

    public static IEndpointRouteBuilder MapTourEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── Nháp tour ───────────────────────────────────────────────────────────
        v1.MapGet("/tours", (HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            var list = store.List<SavedTour>(COLL, s.TenantId)
                .OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal).ToList();
            return Results.Json(list);
        });

        v1.MapGet("/tours/{id}", (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            var t = store.Get<SavedTour>(COLL, s.TenantId, id);
            return t == null ? Results.NotFound(new { error = "Không tìm thấy nháp tour" }) : Results.Json(t);
        });

        v1.MapPost("/tours", (JsonElement body, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();

            var id = GetStr(body, "id");
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
            var tour = new SavedTour(
                Id: id!,
                Title: GetStr(body, "title"),
                Request: Clone(body, "request"),
                Itinerary: Clone(body, "itinerary"),
                Marketing: Clone(body, "marketing"),
                Rows: Clone(body, "rows"),
                NccCoveragePct: GetInt(body, "nccCoveragePct") ?? 0,
                CreatedAt: DateTime.UtcNow.ToString("o"),
                CreatedBy: s.FullName ?? s.Username);
            store.Set(COLL, s.TenantId, id!, tour);
            return Results.Json(new { ok = true, id, tour });
        });

        v1.MapDelete("/tours/{id}", (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            return store.Delete(COLL, s.TenantId, id) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Không tìm thấy" });
        });

        // ─── NCC thật (proxy TourKit) ──────────────────────────────────────────────
        v1.MapGet("/ncc/categories", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.CategoriesAsync(sid!, ctx.RequestAborted));
        });

        v1.MapGet("/ncc/providers", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, int? serviceId, int? marketId) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            if (serviceId.HasValue) return await Proxy(() => ncc.ProvidersByServiceAsync(sid!, serviceId.Value, ctx.RequestAborted));
            return await Proxy(() => ncc.ProvidersAsync(sid!, marketId, ctx.RequestAborted));
        });

        v1.MapGet("/ncc/providers/{id:int}/services", async (int id, HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, int? categoryId) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.ProviderServicesAsync(sid!, id, categoryId, ctx.RequestAborted));
        });

        return routes;
    }

    private static async Task<IResult> Proxy(Func<Task<JsonElement>> call)
    {
        try { return Results.Json(await call()); }
        catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        catch (Exception ex) { return Results.Json(new { error = "NCC lỗi: " + ex.Message }, statusCode: 502); }
    }

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized() => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

    private static string? GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static JsonElement Clone(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.Clone() : default;
}
