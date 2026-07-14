using System.Text.Json;
using TourkitAiProxy.Models;              // ActionExecuteRequest
using TourkitAiProxy.Services.Chat;       // ActionExecutor
using TourkitAiProxy.Services.TourKit;    // TkSessionStore

namespace TourkitAiProxy.Endpoints;

/// POST /api/v1/assistant/action/execute — gọi SAU khi user bấm "Xác nhận" trên action-proposal
/// (Chat-Analytics). Resolve session (tenant + JWT + username) từ X-Session-Id, dispatch tới
/// ActionExecutor, trả ActionResult camelCase.
public static class AssistantActionEndpoints
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static void MapAssistantActionEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapPost("/assistant/action/execute", async (
            HttpContext ctx, ActionExecuteRequest req,
            TkSessionStore sessions, ActionExecutor exec) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var sess = sessions.Get(sid);
            if (sess is null)
                return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, SseJson, statusCode: 401);

            try
            {
                // GetValidJwtAsync tự re-login nếu JWT soft-expire — KHÔNG dùng sess.Jwt thô
                // (giống mọi call TourKit khác trong ChatAgentService/JsonPlannerAgent/NativeToolUseAgent).
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);

                var result = await exec.ExecuteAsync(
                    req, sess.TenantId, jwt, sess.Username, sid, ctx.RequestAborted);
                return Results.Json(result, SseJson);   // camelCase, khớp client
            }
            catch (NotImplementedException)
            {
                return Results.Json(new { error = "Hành động này chưa được hỗ trợ." }, SseJson, statusCode: 501);
            }
            catch (TourKitApiException ex)
            {
                return Results.Json(new { error = ex.Message }, SseJson, statusCode: ex.Status is >= 400 and < 600 ? ex.Status : 502);
            }
        });

        // POST /assistant/action/resolve — gọi SAU khi user chọn 1 lựa chọn trong action-clarify
        // (nhiều bản ghi khớp tên, vd 3 nhân viên trùng tên "Nguyễn Văn A"). ChosenId (id THẬT của bản
        // ghi được chọn) được inject vào Params dưới key quy ước theo Field, rồi rebuild lại envelope
        // hành động qua ChatAgentService.BuildActionEnvelopeAsync — KHÔNG re-resolve theo tên gốc (đó
        // là nguyên nhân lặp vô hạn khi nhiều bản ghi trùng tên: resume bằng label thì vẫn mơ hồ y hệt).
        v1.MapPost("/assistant/action/resolve", async (
            HttpContext ctx, ActionResolveRequest req,
            TkSessionStore sessions, ChatAgentService chat) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var sess = sessions.Get(sid);
            if (sess is null)
                return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, SseJson, statusCode: 401);

            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid!, ctx.RequestAborted);

                var p = new Dictionary<string, object?>(req.Params ?? new Dictionary<string, object?>());
                switch ((req.Field ?? "").ToLowerInvariant())
                {
                    case "staff":
                        // Tích lũy CSV — 1 action có thể cần chọn nhiều nhân viên tuần tự (staffNames là CSV).
                        var existing = ActionExecutor.Str(p, "staffResolvedIds");
                        p["staffResolvedIds"] = string.IsNullOrWhiteSpace(existing)
                            ? req.ChosenId : existing + "," + req.ChosenId;
                        break;
                    case "customer":
                        p["customerResolvedId"] = req.ChosenId;
                        // Mang tên thật + SĐT (hint) của khách ĐÃ CHỌN theo — để lịch hẹn có customerPhone
                        // (app yêu cầu SĐT hợp lệ) mà KHÔNG phải lookup lại theo id.
                        if (!string.IsNullOrWhiteSpace(req.ChosenLabel)) p["customerName"] = req.ChosenLabel;
                        if (ChatAgentService.LooksLikePhone(req.ChosenHint)) p["customerPhone"] = req.ChosenHint;
                        break;
                    case "deal":
                        p["dealResolvedId"] = req.ChosenId;
                        break;
                }

                var payload = await chat.BuildActionEnvelopeAsync(
                    req.Action, p, sid!, sess.TenantId, jwt, sess.Username, null, null, ctx.RequestAborted);

                if (payload is null)
                    return Results.Json(new { error = "Hành động không hợp lệ." }, SseJson, statusCode: 400);

                return Results.Json(payload, SseJson);   // camelCase, khớp client — cùng shape SSE action-* events
            }
            catch (TourKitApiException ex)
            {
                return Results.Json(new { error = ex.Message }, SseJson, statusCode: ex.Status is >= 400 and < 600 ? ex.Status : 502);
            }
        });
    }
}
