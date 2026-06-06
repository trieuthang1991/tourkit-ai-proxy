using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Chat-Analytics ("Trợ lý số liệu") — chat trái, bảng số liệu phải, AI tự chọn API TourKit để lấy số.
///
///   POST /api/v1/login-token   — body {token}; token = Crypton.Encrypt(JSON {username,password,domain}).
///                                 Giải mã → login TourKit.Api → tạo phiên → trả {sessionId,...}.
///   POST /api/v1/chat          — body {messages, sessionId?, provider?, model?}; sessionId cũng có thể
///                                 nằm ở header X-Session-Id. Trả {reply, toolName, data{stats,raw}, ...}.
///
/// JWT TourKit KHÔNG bao giờ trả ra client — giữ server-side trong TkSessionStore (xem file đó).
/// </summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── POST /login-token ──────────────────────────────────────────────────
        v1.MapPost("/login-token", async (
            LoginTokenRequest req, TkSessionStore sessions, ILogger<Program> log, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "Thiếu token" });

            string plain;
            try { plain = Crypton.Decrypt(req.Token.Trim()); }
            catch { plain = ""; }
            if (string.IsNullOrWhiteSpace(plain))
                return Results.BadRequest(new { error = "Token không hợp lệ hoặc giải mã thất bại" });

            string? username, password, domain;
            try
            {
                using var doc = JsonDocument.Parse(plain);
                var r = doc.RootElement;
                username = Field(r, "username");
                password = Field(r, "password");
                domain   = Field(r, "domain") ?? Field(r, "tenantId");
            }
            catch
            {
                return Results.BadRequest(new { error = "Nội dung token không phải JSON {username,password,domain}" });
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(domain))
                return Results.BadRequest(new { error = "Token thiếu username/password/domain" });

            try
            {
                var s = await sessions.CreateAsync(domain!, username!, password!, ctx.RequestAborted);
                return Results.Json(ToLoginResponse(s));
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("login-token: {Msg}", ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "login-token unhandled");
                return Results.Json(new { error = "Lỗi server khi đăng nhập TourKit", detail = ex.Message }, statusCode: 500);
            }
        });

        // ─── POST /login ─── đăng nhập trực tiếp bằng form {username,password,domain} ──
        v1.MapPost("/login", async (
            LoginCredRequest req, TkSessionStore sessions, ILogger<Program> log, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.Domain))
                return Results.BadRequest(new { error = "Thiếu username/password/domain" });

            try
            {
                var s = await sessions.CreateAsync(req.Domain.Trim(), req.Username.Trim(), req.Password, ctx.RequestAborted);
                return Results.Json(ToLoginResponse(s));
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("login: {Msg}", ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "login unhandled");
                return Results.Json(new { error = "Lỗi server khi đăng nhập TourKit", detail = ex.Message }, statusCode: 500);
            }
        });

        // ─── GET /session ─── lấy lại info phiên (tên/công ty) cho header sau reload/restart ──
        v1.MapGet("/session", (TkSessionStore sessions, HttpContext ctx) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                      ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var s = sessions.Get(sid);
            return s == null
                ? Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401)
                : Results.Json(ToLoginResponse(s));
        });

        // ─── POST /chat ───────────────────────────────────────────────────────────
        v1.MapPost("/chat", async (
            ChatRequest req, ChatAgentService agent, TkSessionStore sessions, ILogger<Program> log, HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? req.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) == null)
                return Results.Json(new { error = "Phiên không hợp lệ — vui lòng đăng nhập lại bằng token" }, statusCode: 401);

            if (req.Messages == null || req.Messages.Count == 0)
                return Results.BadRequest(new { error = "messages rỗng" });

            try
            {
                var result = await agent.AskAsync(req, sessionId!, ctx.RequestAborted);
                return Results.Json(result);
            }
            catch (TourKitApiException ex)
            {
                log.LogWarning("chat: TourKit {Status} {Msg}", ex.Status, ex.Message);
                return Results.Json(new { error = ex.Message, status = ex.Status }, statusCode: ex.Status);
            }
            catch (TourkitAiProxy.Services.Providers.UpstreamException ex)
            {
                log.LogWarning("chat: AI upstream {Status}: {Body}", ex.Status, ex.Body);
                var msg = ex.Status == 401
                    ? "Nhà cung cấp AI từ chối (401): token/model AI hết hạn hoặc không hợp lệ. Hãy đổi model AI khác (nút \"AI: …\" góc trên phải) rồi thử lại."
                    : $"Nhà cung cấp AI đang lỗi (HTTP {ex.Status}). Thử lại sau hoặc đổi model AI khác.";
                return Results.Json(new { error = msg, status = ex.Status }, statusCode: 502);
            }
            catch (OperationCanceledException)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "chat unhandled");
                return Results.Json(new { error = $"Lỗi server ({ex.GetType().Name})", detail = ex.Message }, statusCode: 500);
            }
        });

        // ─── POST /chat/stream (SSE) ── stage + stream chữ phân tích ────────────────
        v1.MapPost("/chat/stream", async (
            ChatRequest req, ChatAgentService agent, TkSessionStore sessions, ILogger<Program> log, HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? req.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) == null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Phiên không hợp lệ — vui lòng đăng nhập lại" });
                return;
            }
            if (req.Messages == null || req.Messages.Count == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "messages rỗng" });
                return;
            }

            ctx.Response.Headers["Content-Type"]      = "text/event-stream";
            ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.StartAsync(ctx.RequestAborted);

            async Task Emit(object payload)
            {
                // camelCase để khớp client (kind/title/stats/raw…) — giống Results.Json của endpoint buffered.
                var bytes = System.Text.Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(payload, SseJson) + "\n\n");
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try { await agent.AskStreamAsync(req, sessionId!, Emit, ctx.RequestAborted); }
            catch (OperationCanceledException) { }
            catch (TourKitApiException ex)
            {
                log.LogWarning("chat-stream: TourKit {Status} {Msg}", ex.Status, ex.Message);
                try { await Emit(new { error = ex.Message, status = ex.Status }); await Emit(new { done = true }); } catch { }
            }
            catch (Services.Providers.UpstreamException ex)
            {
                var msg = ex.Status == 401
                    ? "Nhà cung cấp AI từ chối (401): token/model AI hết hạn. Đổi model AI khác rồi thử lại."
                    : $"Nhà cung cấp AI đang lỗi (HTTP {ex.Status}).";
                try { await Emit(new { error = msg, status = ex.Status }); await Emit(new { done = true }); } catch { }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "chat-stream unhandled");
                try { await Emit(new { error = $"Lỗi server ({ex.GetType().Name})" }); await Emit(new { done = true }); } catch { }
            }
        });

        // ─── POST /chat/cache/clear ─── xóa cache số liệu của công ty (buộc gọi mới) ──
        v1.MapPost("/chat/cache/clear", (HttpContext ctx, TkSessionStore sessions, Services.Cache.ChatCache cache) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var s = sessions.Get(sid);
            if (s == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            var n = cache.ClearTenant(s.TenantId);
            return Results.Json(new { ok = true, cleared = n });
        });

        return routes;
    }

    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    private static LoginTokenResponse ToLoginResponse(TourkitAiProxy.Services.TourKit.TkSession s)
        => new(
            SessionId:   s.Id,
            TenantId:    s.TenantId,
            FullName:    s.FullName,
            CompanyName: s.CompanyName,
            ExpiresAt:   new DateTimeOffset(s.JwtExpiresAt, TimeSpan.Zero).ToUnixTimeMilliseconds());

    private static string? Field(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }
}
