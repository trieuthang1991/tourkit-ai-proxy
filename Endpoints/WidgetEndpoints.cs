using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.Speech;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Widget;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Widget Chat — embed JS + token vào site khách để chat với bot AI.
///
/// Public (CORS *, không cần session — chỉ cần token):
///   GET  /api/v1/widget/config?token=trav_xxx    — config UI (botName/greeting/color) cho widget.js mount
///   POST /api/v1/widget/chat                     — buffered chat
///   POST /api/v1/widget/chat/stream              — SSE stream
///
/// Admin (cần X-Session-Id của tenant chủ token):
///   GET    /api/v1/admin/widget/tokens
///   POST   /api/v1/admin/widget/tokens
///   PATCH  /api/v1/admin/widget/tokens/{token}
///   DELETE /api/v1/admin/widget/tokens/{token}
///
/// File widget.js là static asset → wwwroot/widget.js tự serve qua UseTourkitStaticFiles.
/// </summary>
public static class WidgetEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string DEFAULT_BOT_NAME = "Trợ lý TRAV-AI";
    private const string DEFAULT_GREETING = "Xin chào Anh/Chị! Em là trợ lý tư vấn tour. Anh/Chị quan tâm tuyến nào ạ?";
    private const string DEFAULT_SYSTEM_PROMPT =
        "Bạn là trợ lý tư vấn tour du lịch của công ty. Trả lời ngắn gọn, lịch sự, " +
        "tập trung vào dịch vụ tour (giá, lịch trình, dịch vụ kèm theo). Khi khách quan tâm " +
        "tour cụ thể, hỏi rõ điểm đến, thời gian, số người, ngân sách dự kiến để tư vấn chính xác.";
    private const string DEFAULT_COLOR = "#F97316";

    public static void MapWidgetEndpoints(this IEndpointRouteBuilder routes)
    {
        // ─── PUBLIC endpoints (token-based, CORS *) ──────────────────────────────
        var pub = routes.MapGroup("/api/v1/widget");

        // GET config — widget.js fetch ngay khi mount để biết greeting/color/botName.
        pub.MapGet("/config", async (string? token, WidgetTokenRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "Thiếu token" });
            var row = await repo.GetByTokenAsync(token);
            if (row == null) return Results.NotFound(new { error = "Token không tồn tại" });
            if (!row.Enabled) return Results.Json(new { error = "Token đã bị vô hiệu" }, statusCode: 403);

            return Results.Json(new WidgetConfigResp(
                BotName: row.BotName, Greeting: row.Greeting,
                Color: row.Color, Enabled: row.Enabled), WebJson);
        });

        // POST chat — buffered. Đơn giản, đủ cho client không hỗ trợ SSE.
        // Có CRM session → dispatch CrmService; KHÔNG có → FAQ thường.
        pub.MapPost("/chat", async (WidgetChatReq req, HttpContext ctx,
            WidgetTokenRepository repo, WidgetChatService faq, WidgetChatCrmService crm) =>
        {
            var (row, err) = await ValidateAsync(req, ctx, repo);
            if (err != null) return err;

            try
            {
                if (HasCrm(row!))
                {
                    var rc = await crm.ChatStreamAsync(row!, req.Message, req.History, req.Images, req.Documents,
                        _ => Task.CompletedTask, ctx.RequestAborted);
                    return Results.Json(new { reply = rc.Reply, usedCrm = rc.UsedCrm, toolName = rc.ToolName, latencyMs = rc.LatencyMs }, WebJson);
                }
                var res = await faq.ChatAsync(row!, req.Message, req.History, req.Images, req.Documents, ctx.RequestAborted);
                return Results.Json(new { reply = res.Reply, usedCrm = false, model = res.Model, latencyMs = res.LatencyMs }, WebJson);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // DEV ONLY — gen Crypton token từ 1 session đang tồn tại (đọc password đã Crypton-encrypt
        // trong store, decrypt, re-encrypt JSON). Tiện cho test /init khi không có TourKit Shared tool.
        // Tắt mặc định ngoài Development env.
        pub.MapPost("/_dev/crypton/from-session/{sessionId}", (string sessionId, IHostEnvironment env,
            TkSessionStore sessions) =>
        {
            if (!env.IsDevelopment()) return Results.NotFound();
            var s = sessions.Get(sessionId);
            if (s == null) return Results.NotFound(new { error = "session không tồn tại" });
            var json = JsonSerializer.Serialize(new {
                username = s.Username, password = s.Password, domain = s.TenantId
            });
            return Results.Json(new { cryptonToken = Crypton.Encrypt(json), tenantId = s.TenantId, username = s.Username });
        });

        // DEV — encrypt + decrypt arbitrary plaintext (test vector cho integration partner).
        pub.MapPost("/_dev/crypton/encrypt", (CryptonDevReq req, IHostEnvironment env) =>
        {
            if (!env.IsDevelopment()) return Results.NotFound();
            return Results.Json(new { cipher = Crypton.Encrypt(req.Plain ?? "") });
        });
        pub.MapPost("/_dev/crypton/decrypt", (CryptonDevReq req, IHostEnvironment env) =>
        {
            if (!env.IsDevelopment()) return Results.NotFound();
            return Results.Json(new { plain = Crypton.Decrypt(req.Plain ?? "") });
        });

        // POST init — one-shot: Crypton token (cùng /login-token) → decrypt + login + tạo widget → trả token widget.
        // Tiện server-to-server integration. Không gửi password plain qua API.
        pub.MapPost("/init", async (InitWidgetReq req, HttpContext ctx,
            WidgetTokenRepository repo, WidgetCrmLinkService crmLink, TkSessionStore sessions, ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Token))
                return Results.BadRequest(new { error = "Thiếu token" });

            // 1. Decrypt + login TourKit. Reuse WidgetCrmLinkService.LoginFromTokenAsync — pass adminTenantId=""
            //    để skip cross-tenant check (init không có admin context, tenant derive từ token).
            var link = await crmLink.LoginFromTokenAsync(req.Token, adminTenantId: "", ctx.RequestAborted);
            if (!link.Ok || link.SessionId == null)
                return Results.BadRequest(new { error = "Init thất bại: " + (link.Error ?? "Token không hợp lệ") });

            var sess = sessions.Get(link.SessionId)!;
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var now = DateTime.UtcNow;

            // 2. Idempotent — 1 tenant = 1 widget. Giữ widget CŨ NHẤT (snippet đã dán
            //    trên site khách → không thay đổi token); xoá hẳn các duplicate mới hơn
            //    sinh ra từ bug cũ → token rác bị 404, không còn dùng được.
            var allWidgets = await repo.ListByTenantAsync(sess.TenantId, ctx.RequestAborted);
            var existing = allWidgets.OrderBy(r => r.CreatedAt).FirstOrDefault();
            if (existing != null)
            {
                // Xoá các widget trùng tenant nhưng KHÔNG phải canonical (newest junk)
                foreach (var dup in allWidgets.Where(r => r.Token != existing.Token))
                {
                    await repo.DeleteAsync(dup.Token, ctx.RequestAborted);
                    log.LogWarning("[Widget Init] xoá duplicate token={Token} tenant={Tenant} (giữ canonical={Canonical})",
                        dup.Token, dup.TenantId, existing.Token);
                }
                var updated = existing with
                {
                    BotName       = Trim(req.BotName,       existing.BotName,       128),
                    Greeting      = Trim(req.Greeting,      existing.Greeting,      1024),
                    SystemPrompt  = Trim(req.SystemPrompt,  existing.SystemPrompt,  8000),
                    Color         = NormColor(req.Color)  ?? existing.Color,
                    AllowedOrigins = SerializeOrigins(req.AllowedOrigins) ?? existing.AllowedOrigins,
                    AllowedTools   = SerializeTools(req.AllowedTools)     ?? existing.AllowedTools,
                    TourKitSessionId = req.LinkCrm ? sess.Id : existing.TourKitSessionId,
                    UpdatedAt = now,
                };
                await repo.UpdateAsync(updated, ctx.RequestAborted);
                log.LogInformation("[Widget Init] reuse widget={Token} tenant={Tenant} (đã có sẵn, đã update config)",
                    updated.Token, updated.TenantId);
                return Results.Json(new InitWidgetResp(
                    Token: updated.Token,
                    EmbedSnippet: BuildSnippet(origin, updated.Token),
                    BotName: updated.BotName,
                    Color: updated.Color,
                    TenantId: updated.TenantId,
                    CrmLinked: !string.IsNullOrEmpty(updated.TourKitSessionId),
                    AllowedTools: WidgetChatCrmService.ParseAllowedTools(updated.AllowedTools)
                ), WebJson);
            }

            // 3. Tenant chưa có widget → tạo mới. CrmLink = ref đến sess.Id.
            var row = new WidgetToken(
                Token: WidgetTokenRepository.NewToken(),
                TenantId: sess.TenantId,
                BotName: Trim(req.BotName, DEFAULT_BOT_NAME, 128),
                Greeting: Trim(req.Greeting, DEFAULT_GREETING, 1024),
                SystemPrompt: Trim(req.SystemPrompt, DEFAULT_SYSTEM_PROMPT, 8000),
                Color: NormColor(req.Color) ?? DEFAULT_COLOR,
                Enabled: true,
                AllowedOrigins: SerializeOrigins(req.AllowedOrigins),
                TotalMessages: 0,
                CreatedAt: now, UpdatedAt: now,
                TourKitSessionId: req.LinkCrm ? sess.Id : null,
                AllowedTools: SerializeTools(req.AllowedTools),
                CacheTtlSeconds: req.CacheTtlSeconds.GetValueOrDefault(300));
            await repo.InsertAsync(row);

            log.LogInformation("[Widget Init] tạo widget MỚI={Token} tenant={Tenant} crm={Crm}",
                row.Token, row.TenantId, req.LinkCrm);

            return Results.Json(new InitWidgetResp(
                Token: row.Token,
                EmbedSnippet: BuildSnippet(origin, row.Token),
                BotName: row.BotName,
                Color: row.Color,
                TenantId: row.TenantId,
                CrmLinked: req.LinkCrm,
                AllowedTools: WidgetChatCrmService.ParseAllowedTools(row.AllowedTools)
            ), WebJson);
        });

        // POST transcribe — public token-scoped STT. Quota consume đúng tenant của widget token.
        // multipart/form-data: file (audio) + token (form/query). Limit 25MB như endpoint admin.
        pub.MapPost("/transcribe", async (HttpContext ctx,
            WidgetTokenRepository repo, SpeechToTextService stt, AiCallContext aiCtx, ILogger<Program> log) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Cần multipart/form-data" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var token = form["token"].FirstOrDefault() ?? ctx.Request.Query["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest(new { error = "Thiếu token" });

            var row = await repo.GetByTokenAsync(token);
            if (row == null) return Results.NotFound(new { error = "Token không tồn tại" });
            if (!row.Enabled) return Results.Json(new { error = "Token đã bị vô hiệu" }, statusCode: 403);

            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "Thiếu file audio" });
            if (file.Length > 25 * 1024 * 1024)
                return Results.BadRequest(new { error = "File >25MB" });

            try
            {
                using var tenantScope = aiCtx.Push("widget-stt", row.TenantId);
                using var stream = file.OpenReadStream();
                var res = await stt.TranscribeAsync(stream, file.FileName, file.ContentType,
                    "vi", null, ctx.RequestAborted);
                return Results.Json(new { text = res.Text, latencyMs = res.LatencyMs }, WebJson);
            }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
            catch (Exception ex) { log.LogError(ex, "[Widget STT] crash"); return Results.Json(new { error = "Lỗi server: " + ex.Message }, statusCode: 500); }
        })
        .DisableAntiforgery();

        // POST chat/stream — SSE: data: {"delta":"..."} ... data: {"done":true, reply, usedCrm}
        pub.MapPost("/chat/stream", async (WidgetChatReq req, HttpContext ctx,
            WidgetTokenRepository repo, WidgetChatService faq, WidgetChatCrmService crm) =>
        {
            var (row, err) = await ValidateAsync(req, ctx, repo);
            if (err != null) { await err.ExecuteAsync(ctx); return; }

            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";   // Nginx — disable buffering

            async Task Send(object obj)
            {
                var json = JsonSerializer.Serialize(obj, WebJson);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                if (HasCrm(row!))
                {
                    var rc = await crm.ChatStreamAsync(row!, req.Message, req.History, req.Images, req.Documents,
                        delta => Send(new { delta }), ctx.RequestAborted);
                    await Send(new { done = true, reply = rc.Reply, usedCrm = rc.UsedCrm, toolName = rc.ToolName });
                }
                else
                {
                    var res = await faq.ChatStreamAsync(row!, req.Message, req.History, req.Images, req.Documents,
                        delta => Send(new { delta }), ctx.RequestAborted);
                    await Send(new { done = true, reply = res.Reply, usedCrm = false, model = res.Model });
                }
            }
            catch (OperationCanceledException) { /* client closed — silently exit */ }
            catch (Exception ex)
            {
                await Send(new { error = ex.Message });
                await Send(new { done = true });
            }
        });

        // ─── ADMIN endpoints (per-tenant, X-Session-Id) ──────────────────────────
        var adm = routes.MapGroup("/api/v1/admin/widget");

        adm.MapGet("/tokens", async (HttpContext ctx, WidgetTokenRepository repo, TkSessionStore sessions) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var rows = await repo.ListByTenantAsync(sess.TenantId);
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var items = rows.Select(r => ToEmbedDto(r, origin, sessions)).ToList();
            return Results.Json(new { items, defaults = new {
                botName = DEFAULT_BOT_NAME, greeting = DEFAULT_GREETING,
                systemPrompt = DEFAULT_SYSTEM_PROMPT, color = DEFAULT_COLOR,
                allowedTools = WidgetChatCrmService.DefaultAllowed,
                crmToolCatalog = ChatTools.All.Select(t => new { name = t.Name, label = t.Title, desc = t.Description }).ToList(),
            }}, WebJson);
        });

        adm.MapPost("/tokens", async (CreateWidgetTokenReq req, HttpContext ctx,
            WidgetTokenRepository repo, TkSessionStore sessions, WidgetCrmLinkService crmLink) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            // Nếu admin paste token CRM → link trước, lấy sessionId.
            string? crmSessionId = null;
            if (!string.IsNullOrWhiteSpace(req.TourKitToken))
            {
                var link = await crmLink.LoginFromTokenAsync(req.TourKitToken, sess.TenantId, ctx.RequestAborted);
                if (!link.Ok) return Results.BadRequest(new { error = "Link CRM thất bại: " + link.Error });
                crmSessionId = link.SessionId;
            }

            var now = DateTime.UtcNow;
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            // Idempotent — 1 tenant = 1 widget. Giữ widget CŨ NHẤT + xoá duplicates.
            var allWidgets = await repo.ListByTenantAsync(sess.TenantId, ctx.RequestAborted);
            var existing = allWidgets.OrderBy(r => r.CreatedAt).FirstOrDefault();
            if (existing != null)
            {
                foreach (var dup in allWidgets.Where(r => r.Token != existing.Token))
                    await repo.DeleteAsync(dup.Token, ctx.RequestAborted);
                var updated = existing with
                {
                    BotName       = Trim(req.BotName,       existing.BotName,       128),
                    Greeting      = Trim(req.Greeting,      existing.Greeting,      1024),
                    SystemPrompt  = Trim(req.SystemPrompt,  existing.SystemPrompt,  8000),
                    Color         = NormColor(req.Color)  ?? existing.Color,
                    AllowedOrigins = SerializeOrigins(req.AllowedOrigins) ?? existing.AllowedOrigins,
                    AllowedTools   = SerializeTools(req.AllowedTools)     ?? existing.AllowedTools,
                    TourKitSessionId = crmSessionId ?? existing.TourKitSessionId,
                    CacheTtlSeconds  = req.CacheTtlSeconds.GetValueOrDefault(existing.CacheTtlSeconds),
                    UpdatedAt = now,
                };
                await repo.UpdateAsync(updated, ctx.RequestAborted);
                return Results.Json(ToEmbedDto(updated, origin, sessions), WebJson);
            }

            var row = new WidgetToken(
                Token: WidgetTokenRepository.NewToken(),
                TenantId: sess.TenantId,
                BotName: Trim(req.BotName, DEFAULT_BOT_NAME, 128),
                Greeting: Trim(req.Greeting, DEFAULT_GREETING, 1024),
                SystemPrompt: Trim(req.SystemPrompt, DEFAULT_SYSTEM_PROMPT, 8000),
                Color: NormColor(req.Color) ?? DEFAULT_COLOR,
                Enabled: true,
                AllowedOrigins: SerializeOrigins(req.AllowedOrigins),
                TotalMessages: 0,
                CreatedAt: now, UpdatedAt: now,
                TourKitSessionId: crmSessionId,
                AllowedTools: SerializeTools(req.AllowedTools),
                CacheTtlSeconds: req.CacheTtlSeconds.GetValueOrDefault(300));
            await repo.InsertAsync(row);

            return Results.Json(ToEmbedDto(row, origin, sessions), WebJson);
        });

        adm.MapPatch("/tokens/{token}", async (string token, UpdateWidgetTokenReq req, HttpContext ctx,
            WidgetTokenRepository repo, TkSessionStore sessions, WidgetCrmLinkService crmLink) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var row = await repo.GetByTokenAsync(token);
            if (row == null || row.TenantId != sess.TenantId)
                return Results.NotFound(new { error = "Token không tồn tại" });

            // Re-link CRM nếu admin paste token mới; hoặc unlink nếu UnlinkCrm=true.
            string? newCrmSessionId = row.TourKitSessionId;
            if (req.UnlinkCrm == true) newCrmSessionId = null;
            else if (!string.IsNullOrWhiteSpace(req.TourKitToken))
            {
                var link = await crmLink.LoginFromTokenAsync(req.TourKitToken, sess.TenantId, ctx.RequestAborted);
                if (!link.Ok) return Results.BadRequest(new { error = "Link CRM thất bại: " + link.Error });
                newCrmSessionId = link.SessionId;
            }

            var updated = row with
            {
                BotName = req.BotName != null ? Trim(req.BotName, row.BotName, 128) : row.BotName,
                Greeting = req.Greeting != null ? Trim(req.Greeting, row.Greeting, 1024) : row.Greeting,
                SystemPrompt = req.SystemPrompt != null ? Trim(req.SystemPrompt, row.SystemPrompt, 8000) : row.SystemPrompt,
                Color = req.Color != null ? (NormColor(req.Color) ?? row.Color) : row.Color,
                Enabled = req.Enabled ?? row.Enabled,
                AllowedOrigins = req.AllowedOrigins != null ? SerializeOrigins(req.AllowedOrigins) : row.AllowedOrigins,
                TourKitSessionId = newCrmSessionId,
                AllowedTools = req.AllowedTools != null ? SerializeTools(req.AllowedTools) : row.AllowedTools,
                CacheTtlSeconds = req.CacheTtlSeconds ?? row.CacheTtlSeconds,
                UpdatedAt = DateTime.UtcNow
            };
            await repo.UpdateAsync(updated);
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(ToEmbedDto(updated, origin, sessions), WebJson);
        });

        adm.MapDelete("/tokens/{token}", async (string token, HttpContext ctx,
            WidgetTokenRepository repo, TkSessionStore sessions) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var row = await repo.GetByTokenAsync(token);
            if (row == null || row.TenantId != sess.TenantId)
                return Results.NotFound(new { error = "Token không tồn tại" });

            await repo.DeleteAsync(token);
            return Results.Ok(new { ok = true });
        });

        // POST link CRM = dùng session đăng nhập hiện tại của admin (tiện UX không cần Crypton token).
        adm.MapPost("/tokens/{token}/link-current-session", async (string token, HttpContext ctx,
            WidgetTokenRepository repo, TkSessionStore sessions) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var row = await repo.GetByTokenAsync(token);
            if (row == null || row.TenantId != sess.TenantId)
                return Results.NotFound(new { error = "Token không tồn tại" });

            var updated = row with { TourKitSessionId = sess.Id, UpdatedAt = DateTime.UtcNow };
            await repo.UpdateAsync(updated);
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(ToEmbedDto(updated, origin, sessions), WebJson);
        });

        // POST test CRM — verify session còn sống + đang gọi đúng tenant. Trả 5 tour mẫu.
        adm.MapPost("/tokens/{token}/test-crm", async (string token, HttpContext ctx,
            WidgetTokenRepository repo, WidgetCrmLinkService crmLink, TkSessionStore sessions) =>
        {
            var sess = ResolveSession(ctx, sessions);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var row = await repo.GetByTokenAsync(token);
            if (row == null || row.TenantId != sess.TenantId)
                return Results.NotFound(new { error = "Token không tồn tại" });
            if (string.IsNullOrEmpty(row.TourKitSessionId))
                return Results.BadRequest(new TestCrmResp(false, "Widget chưa link CRM", null, null));

            var (ok, err, count, titles) = await crmLink.TestCrmAsync(row.TourKitSessionId, ctx.RequestAborted);
            return Results.Json(new TestCrmResp(ok, err, count, titles), WebJson);
        });
    }

    // ── DTO mapper ──
    private static WidgetTokenWithEmbed ToEmbedDto(WidgetToken r, string origin, TkSessionStore sessions)
    {
        var crmLinked = !string.IsNullOrEmpty(r.TourKitSessionId) && sessions.Get(r.TourKitSessionId) != null;
        var tools = WidgetChatCrmService.ParseAllowedTools(r.AllowedTools);
        return new WidgetTokenWithEmbed(
            r.Token, r.TenantId, r.BotName, r.Greeting, r.SystemPrompt, r.Color, r.Enabled,
            r.AllowedOrigins, r.TotalMessages, r.CreatedAt, r.UpdatedAt,
            EmbedSnippet: BuildSnippet(origin, r.Token),
            CrmLinked: crmLinked, AllowedTools: tools, CacheTtlSeconds: r.CacheTtlSeconds);
    }

    private static bool HasCrm(WidgetToken r)
        => !string.IsNullOrEmpty(r.TourKitSessionId);

    // ── Helpers ──
    private static TkSession? ResolveSession(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
              ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        return string.IsNullOrEmpty(sid) ? null : sessions.Get(sid);
    }

    private static async Task<(WidgetToken? row, IResult? err)> ValidateAsync(
        WidgetChatReq req, HttpContext ctx, WidgetTokenRepository repo)
    {
        if (string.IsNullOrWhiteSpace(req?.Token))
            return (null, Results.BadRequest(new { error = "Thiếu token" }));
        if (string.IsNullOrWhiteSpace(req.Message))
            return (null, Results.BadRequest(new { error = "Thiếu nội dung tin nhắn" }));

        var row = await repo.GetByTokenAsync(req.Token);
        if (row == null) return (null, Results.NotFound(new { error = "Token không tồn tại" }));
        if (!row.Enabled) return (null, Results.Json(new { error = "Token đã bị vô hiệu" }, statusCode: 403));

        // Origin check: nếu AllowedOrigins set thì reject nếu thiếu hoặc không khớp.
        if (!string.IsNullOrEmpty(row.AllowedOrigins))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(row.AllowedOrigins) ?? new();
                if (list.Count > 0)
                {
                    var origin = ctx.Request.Headers.Origin.FirstOrDefault();
                    if (string.IsNullOrEmpty(origin) || !list.Any(a => OriginMatches(origin, a)))
                        return (null, Results.Json(new { error = "Origin không được phép" }, statusCode: 403));
                }
            }
            catch { /* malformed JSON — fail-open hơn fail-closed cho admin chưa gen list đúng */ }
        }
        return (row, null);
    }

    // Wildcard match đơn giản: "*.example.com" → khớp foo.example.com / bar.example.com.
    // Không hỗ trợ regex — admin chỉ cần "https://site.com" hoặc "*.site.com".
    private static bool OriginMatches(string origin, string allowed)
    {
        if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        if (allowed.StartsWith("*."))
        {
            var suffix = allowed.Substring(1).ToLowerInvariant();   // ".example.com"
            return origin.ToLowerInvariant().Contains(suffix);
        }
        return false;
    }

    private static string Trim(string? input, string fallback, int max)
    {
        var s = string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        return s.Length > max ? s.Substring(0, max) : s;
    }

    private static string? NormColor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        c = c.Trim();
        if (!c.StartsWith("#")) c = "#" + c;
        if (c.Length != 7 && c.Length != 4) return null;
        return c.Length == 7 && c.Skip(1).All(ch => "0123456789abcdefABCDEF".Contains(ch)) ? c : null;
    }

    private static string? SerializeOrigins(List<string>? origins)
    {
        if (origins == null || origins.Count == 0) return null;
        var clean = origins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct().ToList();
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    // Filter chỉ những tool nằm trong ChatTools.All (defense in depth — admin không gửi rác).
    private static string? SerializeTools(List<string>? tools)
    {
        if (tools == null) return null;
        var valid = new HashSet<string>(ChatTools.All.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var clean = tools.Where(t => !string.IsNullOrWhiteSpace(t) && valid.Contains(t.Trim()))
                         .Select(t => t.Trim()).Distinct().ToList();
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    private static string BuildSnippet(string origin, string token)
    {
        var sb = new StringBuilder();
        sb.Append("<script async src=\"").Append(origin).Append("/widget.js\"\n");
        sb.Append("  data-token=\"").Append(token).Append("\"></script>");
        return sb.ToString();
    }
}
