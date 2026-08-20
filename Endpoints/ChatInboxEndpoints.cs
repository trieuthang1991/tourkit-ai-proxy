// Endpoints/ChatInboxEndpoints.cs
using System.Text.Json;
using TourkitAiProxy.Services.Chat.Channels;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Hộp thư chat đa kênh.
///
/// <para>Hai nhóm đường dẫn KHÁC HẲN nhau về xác thực, cố ý tách rõ:</para>
/// <list type="bullet">
/// <item><b>Webhook</b> (<c>/api/v1/chat/webhook/…</c>) — <b>công khai</b>, vì kênh gọi tới chứ
/// không phải người dùng. Bảo vệ bằng CHỮ KÝ.</item>
/// <item><b>Hộp thư</b> (<c>/api/v1/chat/…</c>) — cần <c>X-Session-Id</c> như mọi trang trong app.</item>
/// </list>
/// </summary>
public static class ChatInboxEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatInboxEndpoints(this IEndpointRouteBuilder routes)
    {
        MapWebhook(routes);
        MapInbox(routes);
        return routes;
    }

    // ── Webhook ─────────────────────────────────────────────────────────────

    private static void MapWebhook(IEndpointRouteBuilder routes)
    {
        // Tenant nằm trên ĐƯỜNG DẪN vì webhook không có phiên đăng nhập: mỗi công ty khai một URL
        // riêng ở trang quản trị OA của họ. Không nhận tenant từ thân request — thân do người ngoài
        // gửi, tin vào đó là ai cũng ghi được tin vào hộp thư công ty khác.
        routes.MapPost("/api/v1/chat/webhook/zalo/{tenantId}", async (
            string tenantId, HttpContext ctx, ChatInboundService svc, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            var adapter = svc.Adapter(ChatChannel.Zalo);
            if (adapter is null) return Results.NotFound();

            // Đọc THÂN THÔ: chữ ký ký trên đúng chuỗi này, parse rồi dựng lại là chữ ký hỏng.
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            if (!await adapter.VerifyAsync(tenantId, raw, ctx.Request.Headers, ct))
            {
                log.LogWarning("[chat/webhook] chữ ký sai, tenant={T}", tenantId);
                return Results.Unauthorized();
            }

            var sk = adapter.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            // TRẢ 200 NGAY rồi xử lý nền. Zalo gửi lại khi không thấy 200, mà xử lý có gọi AI nên
            // mất vài giây — trả lời chậm là khách nhận tin nhân đôi.
            _ = Task.Run(async () =>
            {
                try { await svc.HandleAsync(tenantId, sk, CancellationToken.None); }
                catch (Exception ex) { log.LogError(ex, "[chat/webhook] xử lý nền hỏng tenant={T}", tenantId); }
            }, CancellationToken.None);

            return Results.Ok();
        });
    }

    // ── Hộp thư ─────────────────────────────────────────────────────────────

    private static void MapInbox(IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/chat");

        g.MapGet("/conversations", async (HttpContext ctx, TkSessionStore sessions, ChatRepository repo,
            short? status, string? search, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();

            // Không có quyền xem toàn công ty → chỉ thấy phần của mình + phần chưa ai nhận.
            // Kẹp ở SQL, không lọc phía client.
            var xemHet = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            var chiCuaToi = xemHet ? null : a.Username;

            var items = await repo.ListConversationsAsync(a.TenantId, status, chiCuaToi, search, 60, ct);
            var dem = await repo.CountByStatusAsync(a.TenantId, chiCuaToi, ct);
            return Results.Json(new
            {
                items = items.Select(Shape),
                counts = new
                {
                    moi = dem.TryGetValue(0, out var d0) ? d0 : 0,
                    dangXuLy = dem.TryGetValue(1, out var d1) ? d1 : 0,
                    daDong = dem.TryGetValue(2, out var d2) ? d2 : 0,
                },
                xemToanCongTy = xemHet,
            }, Web);
        });

        g.MapGet("/conversations/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions,
            ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();   // id của tenant khác cũng rơi vào đây

            var tin = await repo.ListMessagesAsync(a.TenantId, id, 120, ct);
            var cuaSo = ChatRules.TinhCuaSo((ChatChannel)v.Channel, v.ContactRepliedAt, DateTime.UtcNow);
            return Results.Json(new
            {
                conversation = Shape(v),
                messages = tin.Select(m => new
                {
                    m.Id, m.Direction, m.SenderKind, m.SenderUsername, m.Kind,
                    m.Body, m.Attachment, m.State, m.ErrorMessage, m.CreatedUtc,
                }),
                // Giao diện KHOÁ ô soạn dựa vào đây — để bấm gửi rồi mới báo hỏng là muộn.
                sendWindow = new
                {
                    open = cuaSo.Open,
                    reason = cuaSo.Reason,
                    hoursLeft = cuaSo.Open && cuaSo.Left != TimeSpan.MaxValue
                        ? Math.Round(cuaSo.Left.TotalHours, 1) : (double?)null,
                },
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/send", async (long id, SendReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Chưa nhập nội dung" });

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var cuaSo = ChatRules.TinhCuaSo((ChatChannel)v.Channel, v.ContactRepliedAt, DateTime.UtcNow);
            if (!cuaSo.Open) return Results.BadRequest(new { error = cuaSo.Reason });

            var msgId = await repo.AppendMessageAsync(a.TenantId, id, (ChatChannel)v.Channel,
                ChatDirection.Ra, ChatSender.NhanVien, a.Username, ChatKind.Chu, body.Text.Trim(),
                null, null, ChatState.Cho, ct);
            if (msgId is null) return Results.Problem("Không ghi được tin");

            await repo.TouchConversationAsync(a.TenantId, id, ChatRules.TomTat(body.Text), false, ct);
            // Người thật vừa trả lời → bot câm một lúc, nếu không nó nói đè lên nhân viên.
            await repo.PauseBotAsync(a.TenantId, id, (int)ChatRules.BotCamMacDinh.TotalMinutes, ct);
            await repo.EnqueueOutboxAsync(a.TenantId, id, msgId.Value, ct);

            return Results.Json(new { ok = true, messageId = msgId }, Web);
        });

        g.MapPost("/conversations/{id:long}/assign", async (long id, AssignReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // Chuỗi rỗng = gỡ giao việc (trả về hàng chờ chung).
            var ai = string.IsNullOrWhiteSpace(body.Username) ? null : body.Username.Trim();
            await repo.AssignAsync(a.TenantId, id, ai, ct);
            return Results.Json(new { ok = true, assignedTo = ai }, Web);
        });

        g.MapPatch("/conversations/{id:long}/status", async (long id, StatusReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (!Enum.IsDefined(typeof(ChatStatus), body.Status))
                return Results.BadRequest(new { error = "Trạng thái không hợp lệ" });
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetStatusAsync(a.TenantId, id, (ChatStatus)body.Status, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapPost("/conversations/{id:long}/read", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            await repo.MarkAgentReadAsync(a.TenantId, id, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapPost("/conversations/{id:long}/bot", async (long id, BotReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // paused=false → bỏ câm ngay; true → câm theo số phút (mặc định 30).
            await repo.PauseBotAsync(a.TenantId, id, body.Paused ? Math.Clamp(body.Minutes ?? 30, 1, 1440) : 0, ct);
            return Results.Json(new { ok = true }, Web);
        });
    }

    private static IResult ChuaCauHinh()
        => Results.Json(new { error = "Chưa khai cơ sở dữ liệu chat (ConnectionStrings:Chat)" }, statusCode: 503);

    private static object Shape(ChatConversation v) => new
    {
        v.Id, v.Channel, v.ContactExternalId, v.Status, v.AssignedUsername,
        v.LastActivityAt, v.LastPreview, v.ContactRepliedAt,
        displayName = v.DisplayName,
        // Bot có đang bị câm không — giao diện hiện rõ, không thì nhân viên tưởng bot hỏng.
        botPaused = v.BotResumeAt is { } m && m > DateTime.UtcNow,
        // Chưa đọc = khách nhắn sau lần mình mở gần nhất.
        unread = v.ContactRepliedAt is { } cr && (v.AgentLastReadAt is null || cr > v.AgentLastReadAt),
    };

    public record SendReq(string Text);
    public record AssignReq(string? Username);
    public record StatusReq(short Status);
    public record BotReq(bool Paused, int? Minutes);
}
