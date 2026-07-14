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
    }
}
