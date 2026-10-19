using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Services.Storage;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Chat nội bộ giữa nhân viên — đường dẫn cho widget nhúng vào CRM.
///
/// <para><b>Nhóm RIÊNG, không nằm dưới nhóm <c>/api/v1/chat</c> của hộp thư khách:</b> nhóm đó gắn
/// bộ lọc quyền chat (CHAT_XEM/CHAT_XEM_ALL), còn chat nội bộ dành cho MỌI nhân viên có phiên hợp lệ.</para>
///
/// <para>Thân request đọc TAY bằng <c>JsonSerializer</c> chứ không khai tham số thân: tham số thân
/// của minimal API gắn <c>AcceptsMetadata</c>, request thiếu <c>Content-Type</c> bị loại ở tầng định
/// tuyến và rơi xuống SPA fallback → 404 HTML (đã dính thật 08/09/2026).</para>
/// </summary>
public static class InternalChatEndpoints
{
    public const string Prefix = "/api/v1/chat/internal";

    private const int MaxBody = 4000;
    private const int MaxMembers = 200;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapInternalChatEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup(Prefix);

        g.MapGet("/rooms", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            int? limit, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var rooms = await repo.ListRoomsAsync(a!.TenantId, me, Math.Clamp(limit ?? 50, 1, 100), ct);
            return Results.Json(rooms, Web);
        });

        g.MapPost("/rooms", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            TourKitApiClient api, RedisStore redis, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<CreateRoomRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");

            var type = req.EntityType?.Trim().ToLowerInvariant();
            if (!InternalRoomKey.IsValidType(type)) return Bad("Loại phòng không hợp lệ (direct | order | company | group)");
            var others = (req.MemberUserIds ?? new()).Where(x => x > 0 && x != me).Distinct().ToList();
            if (others.Count > MaxMembers) return Bad($"Tối đa {MaxMembers} người một phòng");

            string key;
            try
            {
                key = type switch
                {
                    "direct" when others.Count != 1 => throw new ArgumentException("Phòng 1-1 cần đúng một người khác"),
                    "direct" => InternalRoomKey.Direct(me, others[0]),
                    "group" when others.Count == 0 => throw new ArgumentException("Nhóm cần ít nhất một người khác"),
                    "group" => InternalRoomKey.Group(),
                    _ => InternalRoomKey.ForEntity(type!, req.EntityId ?? ""),
                };
            }
            catch (ArgumentException ex) { return Bad(ex.Message); }

            if (await CheckStaffAsync(others, sessions, api, redis, a!, lf, ct) is { } sai) return sai;

            var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim()[..Math.Min(req.Title.Trim().Length, 120)];
            var entityId = type is "order" or "company" ? req.EntityId!.Trim() : null;
            var id = await repo.CreateOrGetRoomAsync(a!.TenantId, me, key, type!, entityId, title, others, ct);
            return Results.Json(new { id, roomKey = key }, Web);
        });

        g.MapGet("/rooms/{id:long}/messages", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, string? beforeUtc, long? beforeId, long? afterId, int? limit,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var n = Math.Clamp(limit ?? 50, 1, 100);
            IReadOnlyList<InternalMessage>? msgs;
            if (afterId is { } sau)
                msgs = await repo.ListMessagesAfterAsync(a!.TenantId, id, me, sau, n, ct);
            else
            {
                DateTime? truoc = null;
                if (!string.IsNullOrWhiteSpace(beforeUtc))
                {
                    if (!DateTime.TryParse(beforeUtc, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                        return Bad("beforeUtc không đúng định dạng ISO-8601");
                    truoc = t;
                }
                msgs = await repo.ListMessagesAsync(a!.TenantId, id, me, truoc, beforeId, n, ct);
            }
            return msgs is null ? NotMember() : Results.Json(msgs, Web);
        });

        g.MapPost("/rooms/{id:long}/messages", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, IChatFileStorage kho, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<SendInternalMessageRequest>(ctx, ct);
            var body = req?.Body?.Trim();
            if (string.IsNullOrEmpty(body)) body = null;
            if (body?.Length > MaxBody) return Bad($"Tin nhắn dài quá {MaxBody} ký tự");

            // Đính kèm: chỉ nhận URL do CHÍNH kho này cấp cho đúng công ty + phòng (InternalAttachmentRules.IsOwnUrl).
            // Tên/loại dựng lại phía máy chủ, không tin chữ client gửi.
            InternalAttachment? att = null;
            if (req?.Attachment is { } ra)
            {
                if (!InternalAttachmentRules.IsOwnUrl(ra.Url, a!.TenantId, id, StorageBases(kho, ctx)))
                    return Bad("Tệp đính kèm không hợp lệ — tải lên lại");
                var ten = string.IsNullOrWhiteSpace(ra.Name) ? "tệp" : ra.Name.Trim()[..Math.Min(ra.Name.Trim().Length, 200)];
                att = new InternalAttachment(ra.Url, ten, Math.Max(0, ra.Size), InternalAttachmentRules.KindOf(ra.Url));
            }
            if (body is null && att is null) return Bad("Tin nhắn trống");

            var r = await repo.AppendMessageAsync(a!.TenantId, id, me, a.Username, body, att, ct);
            if (r.Id is null) return NotMember();
            // Người gửi cũng nằm trong Recipients: họ có thể đang mở phòng ở tab thứ hai.
            bus.Publish(new ChatEvent(a.TenantId, id, "noi-bo", r.Id) { Recipients = r.Members });
            return Results.Json(new InternalMessage(r.Id.Value, id, me, a.Username, body,
                r.CreatedUtc ?? DateTime.UtcNow, null, att), Web);
        });

        // Tải tệp lên kho (Storage:Provider — R2 ở môi trường thật) rồi trả gói đính kèm để widget gửi
        // kèm tin. Hai bước như hộp thư khách: người gửi xem trước, bỏ được, rồi mới gửi.
        g.MapPost("/rooms/{id:long}/upload", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, IChatFileStorage kho, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            if (!kho.Configured)
                return Results.Json(new { error = $"Chưa cấu hình kho tệp Storage:{kho.Provider}" }, statusCode: 503);
            // Hỏi thành viên TRƯỚC khi đẩy byte lên kho — người ngoài phòng không được đổ tệp vào kho.
            if (!await repo.IsMemberForUploadAsync(a!.TenantId, id, me, ct)) return NotMember();
            if (!ctx.Request.HasFormContentType) return Bad("Thiếu tệp");
            var form = await ctx.Request.ReadFormAsync(ct);
            var tep = form.Files.GetFile("file");
            if (tep is null || tep.Length == 0) return Bad("Thiếu tệp");
            if (tep.Length > InternalAttachmentRules.MaxBytes) return Bad("Tệp quá 15MB");
            if (!InternalAttachmentRules.IsAllowed(tep.FileName))
                return Bad("Loại tệp này không gửi được (nhận ảnh, PDF, Word, Excel, PowerPoint, TXT, CSV, ZIP, RAR)");

            var duoi = Path.GetExtension(tep.FileName).ToLowerInvariant();
            var key = InternalAttachmentRules.KeyPrefix(a.TenantId, id) + Guid.NewGuid().ToString("N") + duoi;
            await using var s = tep.OpenReadStream();
            var url = await kho.UploadAsync(key, s, ContentTypeOf(duoi), ct);
            if (url.StartsWith('/')) url = $"{ctx.Request.Scheme}://{ctx.Request.Host}{url}";
            return Results.Json(new InternalAttachment(url, Path.GetFileName(tep.FileName), tep.Length,
                InternalAttachmentRules.KindOf(tep.FileName)), Web);
        });

        g.MapPost("/rooms/{id:long}/read", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            return await repo.MarkReadAsync(a!.TenantId, id, me, ct) ? Results.NoContent() : NotMember();
        });

        // ── Cấu hình phòng: đổi tên, thêm/bỏ người. Phòng 1-1 khoá (repo trả false/0 → 403). ──
        g.MapPatch("/rooms/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<RenameRoomRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");
            var t = req.Title?.Trim();
            var title = string.IsNullOrEmpty(t) ? null : t[..Math.Min(t.Length, 120)];
            if (!await repo.RenameRoomAsync(a!.TenantId, id, me, title, ct)) return CannotConfigure();
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        g.MapPost("/rooms/{id:long}/members", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<AddMembersRequest>(ctx, ct);
            var them = (req?.MemberUserIds ?? new()).Where(x => x > 0 && x != me).Distinct().ToList();
            if (them.Count == 0) return Bad("Chưa chọn người để thêm");
            if (them.Count > MaxMembers) return Bad($"Tối đa {MaxMembers} người một lần");
            if (await CheckStaffAsync(them, sessions, api, redis, a!, lf, ct) is { } sai) return sai;
            if (await repo.AddMembersAsync(a!.TenantId, id, me, them, ct) == 0) return CannotConfigure();
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        g.MapDelete("/rooms/{id:long}/members/{userId:int}", async (long id, int userId, HttpContext ctx,
            TkSessionStore sessions, InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            if (!await repo.RemoveMemberAsync(a!.TenantId, id, me, userId, ct))
                return Results.Json(new { error = "Không bỏ được người này (không ở trong phòng, hoặc là phòng 1-1)" }, statusCode: 403);
            // Báo cả người VỪA bị bỏ: tab của họ phải biết mà đóng phòng.
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, userId, ct);
            return Results.NoContent();
        });

        g.MapDelete("/messages/{messageId:long}", async (long messageId, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            return await repo.DeleteOwnMessageAsync(a!.TenantId, messageId, me, ct)
                ? Results.NoContent()
                : Results.Json(new { error = "Không thấy tin của bạn để thu hồi" }, statusCode: 404);
        });

        g.MapGet("/users", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            TourKitApiClient api, RedisStore redis, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a!.TenantId,
                a.SessionId, lf.CreateLogger("chat.internal"), ct);
            return Results.Json(new { meId = me, users = staffs }, Web);
        });

        // SSE. EventSource KHÔNG gửi được header nên phiên đi ?sessionId= — SessionAuth.Read đã đọc
        // sẵn. Khung chép từ /api/v1/chat/events: một luồng ghi, nhịp giữ sống 25 giây. Dữ liệu chỉ
        // nói "phòng X có tin Y"; widget tự gọi ?afterId= để lấy nội dung qua đường đã kẹp thành viên.
        g.MapGet("/events", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.StartAsync(ct);

            async Task GhiAsync(string dong)
            {
                await ctx.Response.WriteAsync(dong, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            await using var nguon = bus.SubscribeInternalAsync(a!.TenantId, me, ct).GetAsyncEnumerator(ct);
            using var dongHo = new PeriodicTimer(TimeSpan.FromSeconds(25));
            var toi = nguon.MoveNextAsync().AsTask();
            var nhip = dongHo.WaitForNextTickAsync(ct).AsTask();
            try
            {
                while (true)
                {
                    if (await Task.WhenAny(toi, nhip) == nhip)
                    {
                        if (!await nhip) break;
                        await GhiAsync(": nhip\n\n");
                        nhip = dongHo.WaitForNextTickAsync(ct).AsTask();
                    }
                    else
                    {
                        if (!await toi) break;
                        var e = nguon.Current;
                        // Không gửi Recipients ra ngoài: danh sách thành viên là việc của API phòng.
                        await GhiAsync($"data: {JsonSerializer.Serialize(new { roomId = e.ConversationId, messageId = e.MessageId }, Web)}\n\n");
                        toi = nguon.MoveNextAsync().AsTask();
                    }
                }
            }
            catch (OperationCanceledException) { }   // tab đóng — chuyện thường
            return Results.Empty;
        });

        return routes;
    }

    /// Phiên hợp lệ + có mã người + CSDL chat đã cấu hình. Lỗi thì trả kết quả lỗi, không ném.
    private static async Task<(IResult? Loi, SessionAuth.Ctx? Phien, int Me)> GuardAsync(HttpContext ctx,
        TkSessionStore sessions, InternalChatRepository repo, CancellationToken ct)
    {
        var a = SessionAuth.Read(ctx, sessions);
        if (a is null) return (SessionAuth.Unauthorized(), null, 0);
        var me = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
        if (me is null)
            return (Results.Json(new { error = "Phiên chưa có mã người dùng — đăng nhập lại" }, statusCode: 401), null, 0);
        if (!repo.Configured)
            return (Results.Json(new { error = "Chat nội bộ chưa được cấu hình (ConnectionStrings:Chat)" }, statusCode: 503), null, 0);
        return (null, a, me.Value);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
    {
        try { return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Web, ct); }
        catch (JsonException) { return null; }
    }

    private static IResult Bad(string msg) => Results.Json(new { error = msg }, statusCode: 400);

    private static IResult CannotConfigure()
        => Results.Json(new { error = "Không đổi được phòng này (bạn không ở trong phòng, hoặc là phòng 1-1)" }, statusCode: 403);

    /// <summary>
    /// Chỉ thêm được người CỦA CÔNG TY NÀY — mã người là số đoán được, không kiểm thì kéo được bất kỳ
    /// mã nào vào phòng. null = hợp lệ.
    /// </summary>
    private static async Task<IResult?> CheckStaffAsync(IReadOnlyCollection<int> ids, TkSessionStore sessions,
        TourKitApiClient api, RedisStore redis, SessionAuth.Ctx a, ILoggerFactory lf, CancellationToken ct)
    {
        if (ids.Count == 0) return null;
        var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId,
            lf.CreateLogger("chat.internal"), ct);
        if (staffs.Count == 0)
            return Results.Json(new { error = "Chưa tải được danh sách nhân viên — thử lại sau" }, statusCode: 503);
        var hopLe = staffs.Select(ChatStaffDirectory.StaffId).OfType<int>().ToHashSet();
        var la = ids.Where(x => !hopLe.Contains(x)).ToList();
        return la.Count > 0 ? Bad("Có người không thuộc công ty: " + string.Join(", ", la)) : null;
    }

    /// Báo "phòng đổi cấu hình" (MessageId null) cho thành viên hiện tại + người vừa bị bỏ (nếu có).
    private static async Task NotifyRoomChangedAsync(InternalChatRepository repo, ChatEventBus bus, string tenant,
        long roomId, int? removed, CancellationToken ct)
    {
        var ds = (await repo.MembersAsync(tenant, roomId, ct)).ToList();
        if (removed is int r && !ds.Contains(r)) ds.Add(r);
        bus.Publish(new ChatEvent(tenant, roomId, "noi-bo-phong", null) { Recipients = ds.ToArray() });
    }

    /// Đoạn đầu của mọi URL kho cấp: PublicBase (r2/s3) hoặc /chat-files/ của chính máy chủ (local).
    private static IEnumerable<string> StorageBases(IChatFileStorage kho, HttpContext ctx)
    {
        var b = kho.PublicBase;
        if (string.IsNullOrWhiteSpace(b)) yield break;
        if (b.StartsWith('/')) b = $"{ctx.Request.Scheme}://{ctx.Request.Host}{b}";
        yield return b.EndsWith('/') ? b : b + "/";
    }

    /// Content-Type đặt theo ĐUÔI (đã qua danh sách trắng), không tin chữ trình duyệt gửi lên.
    private static string ContentTypeOf(string duoi) => duoi switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".pdf" => "application/pdf", ".txt" => "text/plain; charset=utf-8", ".csv" => "text/csv; charset=utf-8",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip", ".rar" => "application/vnd.rar",
        _ => "application/octet-stream",
    };

    /// 403 chứ không 404: người hỏi biết mã phòng (từ chính danh sách của họ hoặc đoán); nói "không
    /// phải thành viên" không lộ thêm gì ngoài điều họ đã biết.
    private static IResult NotMember() => Results.Json(new { error = "Bạn không ở trong phòng này" }, statusCode: 403);
}
