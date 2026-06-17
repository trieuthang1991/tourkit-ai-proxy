using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Tour;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Soạn Tour GIT bằng AI — bóc tách mô tả tự do thành form prefill.
///   POST /api/v1/tour-builder/parse — body {prompt, provider, model, apiKey} → TourBuilderDraft
/// </summary>
public static class TourBuilderEndpoints
{
    public static void MapTourBuilderEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapPost("/tour-builder/parse", async (TourBuilderRequest req, TourBuilderService svc,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace, CancellationToken ct) =>
        {
            try
            {
                var draft = await svc.ParseAsync(req, ct);
                var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
                if (traceObj != null) return Results.Json(new { draft, _trace = traceObj });
                return Results.Json(draft);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Bóc tách lỗi: " + ex.Message }, statusCode: 502);
            }
        });

        // ── LƯU VÀO CRM: tour-builder → tạo/sửa Tour GIT thật (qua TourKit.Api /api/ai/tours) — cần đăng nhập ──
        //    Body: { form: {...tour-builder form...}, crmTourId } (crmTourId>0 = sửa tour đó).
        //    Map expenses[] (Phần thu) → revenues[]; KH tìm-hoặc-tạo phía server.
        v1.MapPost("/tour-builder/save-crm", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            if (string.IsNullOrEmpty(sid) || sessions.Get(sid) == null)
                return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "Body JSON không hợp lệ" }); }
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "Body rỗng" });

            var form = body.TryGetProperty("form", out var f) && f.ValueKind == JsonValueKind.Object ? f : body;
            int crmTourId = body.TryGetProperty("crmTourId", out var ctv) && ctv.ValueKind == JsonValueKind.Number && ctv.TryGetInt32(out var cti) ? cti : 0;

            string? Str(string k) => form.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
            int Int(string k) => form.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

            var revenues = new List<object>();
            if (form.TryGetProperty("expenses", out var exp) && exp.ValueKind == JsonValueKind.Array)
                foreach (var e in exp.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    decimal Dec(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
                    var t = e.TryGetProperty("title", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() : null;
                    revenues.Add(new { title = t, unitPrice = Dec("unitPrice"), quantity = Dec("quantity"), vatPercent = Dec("vatPercent") });
                }

            var payload = new
            {
                crmTourId,
                title = Str("title"),
                marketName = Str("marketName"),
                startDate = Str("startDate"),       // ISO string ("" → null) → DateTime? server-side
                endDate = Str("endDate"),
                adultCount = Int("adultCount"),
                childCount = Int("childCount"),
                customerName = Str("customerName"),
                customerPhone = Str("customerPhone"),
                customerEmail = Str("customerEmail"),
                note = Str("note"),
                revenues,
            };

            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid, ctx.RequestAborted);
                JsonElement data;
                try { data = await api.PostAsync(jwt, "/api/ai/tours", payload, ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid, ctx.RequestAborted);
                    data = await api.PostAsync(jwt, "/api/ai/tours", payload, ctx.RequestAborted);
                }
                log.LogInformation("[tour-builder] save-crm OK crmTourId={Id} revenues={N}", crmTourId, revenues.Count);
                return Results.Json(new { ok = true, result = data });
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "[tour-builder] save-crm"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        }).DisableAntiforgery();
    }
}
