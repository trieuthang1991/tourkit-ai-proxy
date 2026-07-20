using System.Collections.Concurrent;
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

    // Cache thị trường per-tenant — đổi chậm, TTL 6h là an toàn.
    private static readonly ConcurrentDictionary<string, (List<string> Names, DateTime Exp)> _marketsCache = new();
    private static readonly TimeSpan MarketsTtl = TimeSpan.FromHours(6);

    // Cache thống kê chất lượng dữ liệu NCC per-tenant (R1) — quét toàn bộ NCC nên nặng → TTL 10 phút.
    private static readonly ConcurrentDictionary<string, (object Stats, DateTime Exp)> _nccStatsCache = new();
    private static readonly TimeSpan NccStatsTtl = TimeSpan.FromMinutes(10);

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
            // Re-save (cùng id) KHÔNG reset status/createdAt — giữ nguyên từ bản cũ.
            var existing = store.Get<SavedTour>(COLL, s.TenantId, id!);
            var tour = new SavedTour(
                Id: id!,
                Title: GetStr(body, "title"),
                Request: Clone(body, "request"),
                Itinerary: Clone(body, "itinerary"),
                Marketing: Clone(body, "marketing"),
                Rows: Clone(body, "rows"),
                NccCoveragePct: GetInt(body, "nccCoveragePct") ?? 0,
                CreatedAt: existing?.CreatedAt ?? DateTime.UtcNow.ToString("o"),
                CreatedBy: existing?.CreatedBy ?? (s.FullName ?? s.Username),
                Status: GetStr(body, "status") ?? existing?.Status ?? "draft");
            store.Set(COLL, s.TenantId, id!, tour);
            return Results.Json(new { ok = true, id, tour });
        });

        // PATCH /tours/{id}/status — đổi trạng thái nháp tour (draft|sent|success) → badge Wizard landing.
        v1.MapPatch("/tours/{id}/status", async (string id, HttpContext ctx, TenantStore store, TkSessionStore sessions) =>
        {
            var s = sessions.Get(Sid(ctx));
            if (s == null) return Unauthorized();
            string? status = null;
            try
            {
                var b = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("status", out var v) && v.ValueKind == JsonValueKind.String)
                    status = v.GetString();
            }
            catch { }
            status = (status ?? "").Trim().ToLowerInvariant();
            if (status != "draft" && status != "sent" && status != "success")
                return Results.BadRequest(new { error = "status phải là draft|sent|success" });
            var t = store.Get<SavedTour>(COLL, s.TenantId, id);
            if (t == null) return Results.NotFound(new { error = "Không tìm thấy nháp tour" });
            store.Set(COLL, s.TenantId, id, t with { Status = status });
            return Results.Json(new { ok = true, id, status });
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

        // Danh sách NCC để HIỂN THỊ (search + paging) — proxy /api/providers (endpoint mới). Cho trang "Nhà cung cấp".
        // Query params: filter (keyword), pageIndex, pageSize, serviceId (optional — filter theo loại DV).
        v1.MapGet("/ncc/list", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, string? filter, int? pageIndex, int? pageSize, int? serviceId) =>
        {
            var sid = Sid(ctx); if (sessions.Get(sid) == null) return Unauthorized();
            return await Proxy(() => ncc.ProviderListAsync(sid!, filter, pageIndex ?? 1, pageSize ?? 20, serviceId, ctx.RequestAborted));
        });

        // R1 (Sheet BugTRAV-AI): thống kê "nâng cao chất lượng dữ liệu" cho banner màn NCC list.
        // Đếm TỔNG toàn bộ NCC → thiếu email / thiếu SĐT (quét /api/ai/providers) + NCC chưa có bảng giá
        // (distinct providerId từ /api/ai/provider-prices → thiếu = total - có). Nặng nên cache 10 phút/tenant.
        v1.MapGet("/ncc/stats", async (HttpContext ctx, TourKitNccClient ncc, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx); var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            if (_nccStatsCache.TryGetValue(sess.TenantId, out var c) && c.Exp > DateTime.UtcNow)
                return Results.Json(c.Stats);
            try
            {
                const int PS = 500, MAXPAGES = 60;  // trần an toàn ~30k NCC
                int total = 0, missingEmail = 0, missingPhone = 0;
                for (int page = 1; page <= MAXPAGES; page++)
                {
                    var d = await ncc.ProviderListAsync(sid!, null, page, PS, null, ctx.RequestAborted);
                    if (page == 1) total = GetIntProp(d, "total");
                    var items = GetArrayItems(d, "items");
                    if (items.Count == 0) break;
                    foreach (var it in items)
                    {
                        if (IsBlankProp(it, "email")) missingEmail++;
                        if (IsBlankProp(it, "phone")) missingPhone++;
                    }
                    if ((long)page * PS >= total) break;
                }

                var withPrice = new HashSet<int>();
                for (int page = 0; page < MAXPAGES; page++)  // provider-prices dùng pageIndex 0-based
                {
                    var d = await ncc.ProviderPricesAsync(sid!, page, PS, ctx.RequestAborted);
                    var items = GetArrayItems(d, "items");
                    if (items.Count == 0) break;
                    foreach (var it in items)
                    {
                        var pid = GetIntProp(it, "providerId");
                        if (pid > 0) withPrice.Add(pid);
                    }
                    if ((long)(page + 1) * PS >= GetIntProp(d, "total")) break;
                }

                var stats = new
                {
                    total,
                    missingEmail,
                    missingPhone,
                    missingPrice = Math.Max(0, total - withPrice.Count),
                    withPrice = withPrice.Count
                };
                _nccStatsCache[sess.TenantId] = (stats, DateTime.UtcNow.Add(NccStatsTtl));
                return Results.Json(stats);
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "[ncc/stats] fail"); return Results.Json(new { error = "Không tính được thống kê NCC: " + ex.Message }, statusCode: 502); }
        });

        // ─── Thị trường THẬT (proxy TourKit /api/tours/markets, cache 6h per-tenant) ──
        // Tour-builder + Wizard dùng để fill dropdown Thị trường thay vì hardcode 12 string.
        v1.MapGet("/markets", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();

            var key = sess.TenantId;
            if (_marketsCache.TryGetValue(key, out var entry) && entry.Exp > DateTime.UtcNow)
                return Results.Json(entry.Names);

            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                var data = await api.GetAsync(jwt, "/api/tours/markets", ctx.RequestAborted);

                var names = new List<string>();
                if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in data.EnumerateArray())
                    {
                        if (it.ValueKind == JsonValueKind.Object &&
                            it.TryGetProperty("name", out var n) &&
                            n.ValueKind == JsonValueKind.String)
                        {
                            var name = n.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                        }
                    }
                }
                _marketsCache[key] = (names, DateTime.UtcNow.Add(MarketsTtl));
                return Results.Json(names);
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("[markets] upstream {Status}: {Msg}", ex.Status, ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[markets] fail");
                return Results.Json(new { error = "Không lấy được thị trường: " + ex.Message }, statusCode: 502);
            }
        });

        // ─── Permissions của user hiện tại ──────────────────────────────────────
        // Proxy `/api/auth/permissions` upstream → trả list mã quyền (CH_HT_XEM, NC_NC_XEM, …).
        // Frontend cache 1 lần sau login → filter nav "Tích hợp" + gate các page /widget-admin,
        // /visa-config, /workflows theo CH_HT_XEM (mirror web CRM). Không cache server-side vì
        // upstream đã cache theo tenant + response nhẹ (~vài KB).
        v1.MapGet("/permissions", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Unauthorized();
            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);
                JsonElement data;
                try { data = await api.GetAsync(jwt, "/api/auth/permissions", ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid!, ctx.RequestAborted);
                    data = await api.GetAsync(jwt, "/api/auth/permissions", ctx.RequestAborted);
                }
                return Results.Json(data);
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("[permissions] upstream {Status}: {Msg}", ex.Status, ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[permissions] fail");
                return Results.Json(new { error = "Không lấy được quyền: " + ex.Message }, statusCode: 502);
            }
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

    // Helpers cho /ncc/stats — envelope AI surface camelCase (total/items/email/phone/providerId).
    private static int GetIntProp(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
    private static List<JsonElement> GetArrayItems(JsonElement e, string name)
    {
        var list = new List<JsonElement>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var it in a.EnumerateArray()) list.Add(it);
        return list;
    }
    private static bool IsBlankProp(JsonElement e, string name)
        => !(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
             && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()));
}
