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

        // Danh sách phòng của mình, mới hoạt động trước. Phân trang con trỏ: trang sau gửi beforeAt +
        // beforeId của phòng CUỐI trang trước. q = tìm trong TOÀN BỘ phòng (không chỉ trang đã tải):
        // tên phòng / mã bản ghi, hoặc tên người trong phòng (tra danh bạ, không phân biệt dấu).
        g.MapGet("/rooms", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            int? limit, string? beforeAt, long? beforeId, string? q, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            DateTime? truoc = null;
            if (!string.IsNullOrWhiteSpace(beforeAt))
            {
                if (!DateTime.TryParse(beforeAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) || beforeId is null)
                    return Bad("beforeAt (ISO-8601) phải đi kèm beforeId");
                truoc = t;
            }
            var tim = q?.Trim();
            if (tim?.Length > 100) tim = tim[..100];
            int[]? nameIds = null;
            if (!string.IsNullOrEmpty(tim))
            {
                var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a!.TenantId, a.SessionId,
                    lf.CreateLogger("chat.internal"), ct);
                nameIds = staffs.Where(s => InternalRoomSearch.NameMatches(ChatStaffDirectory.StaffName(s), tim))
                    .Select(ChatStaffDirectory.StaffId).OfType<int>().ToArray();
            }
            // CHAT_XEM_ALL: mọi phòng nội bộ của công ty (kể cả phòng mình không ở) — không phụ thuộc nhóm / chi nhánh / quyền đơn.
            var viewAll = await SessionAuth.IsQuanTriChatAsync(a!.SessionId, sessions, ct);
            var rooms = await repo.ListRoomsAsync(a.TenantId, me, Math.Clamp(limit ?? 30, 1, 100),
                truoc, beforeId, InternalRoomSearch.LikePattern(tim), nameIds, viewAll, ct);
            return Results.Json(rooms, Web);
        });

        g.MapPost("/rooms", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            TourkitAiProxy.Services.Deals.DealOpportunityClient deals, IConfiguration cfg, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<CreateRoomRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");

            var type = req.EntityType?.Trim().ToLowerInvariant();
            if (!InternalRoomKey.IsValidType(type)) return Bad("Loại phòng không hợp lệ (direct | group | deal | order | company | key)");
            var others = (req.MemberUserIds ?? new()).Where(x => x > 0 && x != me).Distinct().ToList();
            if (others.Count > MaxMembers) return Bad($"Tối đa {MaxMembers} người một phòng");
            // Mã cơ hội chuẩn hoá ("012" = "12") — không thì cùng một cơ hội ra nhiều phòng.
            var rawEntity = req.EntityId?.Trim();
            if (type == "deal" && (rawEntity = InternalRoomKey.CanonicalDealId(rawEntity)) is null)
                return Bad("Mã cơ hội không hợp lệ");
            // Tham số tự do của trang nhúng (vd url xem lại bản ghi) — chặn ở cửa, lưu nguyên.
            var (dataJson, saiData) = InternalRoomData.Normalize(req.Data);
            if (saiData is not null) return Bad(saiData);

            // Khoá phòng: 1-1 và nhóm do MÁY CHỦ tự sinh (không cần truyền gì); đơn/cơ hội/công ty theo
            // mã bản ghi; "key" = khoá tự do do trang gọi tự sinh ("donhang_123") — lấy-hoặc-tạo.
            string key;
            try
            {
                key = type switch
                {
                    "direct" when others.Count != 1 => throw new ArgumentException("Phòng 1-1 cần đúng một người khác"),
                    "direct" => InternalRoomKey.Direct(me, others[0]),
                    "group" when others.Count == 0 => throw new ArgumentException("Nhóm cần ít nhất một người khác"),
                    "group" => InternalRoomKey.Group(),
                    "key" => InternalRoomKey.ForCustomKey(rawEntity)
                             ?? throw new ArgumentException("Khoá phòng không hợp lệ (chữ, số, _ - . :, tối đa 100 ký tự)"),
                    _ => InternalRoomKey.ForEntity(type!, rawEntity ?? ""),
                };
            }
            catch (ArgumentException ex) { return Bad(ex.Message); }

            // Trưởng nhóm phòng đơn/công ty = người tạo bản ghi ở CRM, CHỈ nhận khi CRM ký. Chữ ký sai/thiếu
            // thì bỏ qua (không chặn mở phòng) — người tạo phòng làm trưởng nhóm như nhóm thường.
            var owner = SignedOwner(cfg, a!.TenantId, type, key, req.OwnerUserId, req.OwnerSig);
            if (owner is int o && o != me && !others.Contains(o)) others.Add(o);

            if (InternalRoomKey.IsEntityType(type))
            {
                // Phòng theo bản ghi: danh sách người do TRANG NHÚNG gom (vd mọi sale của tour FIT) — một tài khoản lạ (đại lý /
                // CTV không có trong danh bạ nhân viên) chỉ bị BỎ RA, không làm hỏng cả lần tạo phòng (lỗi FIT 27/10/2026).
                var (giu, saiNv) = await KeepStaffAsync(others, sessions, api, redis, a!, lf, ct);
                if (saiNv is not null) return saiNv;
                others = giu;
                if (owner is int o2 && o2 != me && !others.Contains(o2)) owner = null;
            }
            else if (await CheckStaffAsync(others, sessions, api, redis, a!, lf, ct) is { } sai) return sai;

            // Phòng theo CƠ HỘI: ai xem được cơ hội đó theo luật của web thì vào được (CH_XEM_ALL: mọi
            // cơ hội, KHÔNG gọi CRM; chỉ CH_XEM: cơ hội mình tạo/phụ trách). CHỈ kiểm lần đầu (chưa là
            // thành viên). Chi tiết cơ hội đọc MỘT lần, dùng lại để thêm người phụ trách khi phòng vừa tạo.
            // Đơn hàng / công ty / khoá tự do: KHÔNG kiểm — xem docs.
            TourkitAiProxy.Services.Deals.DealOpportunityClient.DealDetail? chiTiet = null;
            if (type == "deal" && !await repo.IsMemberByKeyAsync(a!.TenantId, key, me, ct))
            {
                var (cam, ct2) = await CheckDealViewAsync(sessions, deals, a, me, rawEntity!, ct);
                if (cam is not null) return cam;
                chiTiet = ct2;
            }

            var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim()[..Math.Min(req.Title.Trim().Length, 120)];
            var entityId = InternalRoomKey.IsEntityType(type) || type == "key" ? rawEntity : null;
            var (id, created, ownerSync) = await repo.CreateOrGetRoomAsync(a!.TenantId, me, key, type!, entityId, title, others, dataJson, owner, ct);
            if (owner is int so) await AfterOwnerSyncAsync(repo, bus, sessions, api, redis, lf, a, me, id, so, ownerSync, ct);
            // Phòng ĐÃ CÓ mà lượt này mang tham số → thay (chỉ ghi khi khác giá trị đang có).
            if (!created && dataJson is not null) await repo.SetRoomDataAsync(a.TenantId, id, me, dataJson, ct);

            // Phòng cơ hội VỪA tạo → thêm người phụ trách (mã lấy thẳng từ CRM, không dò tên). CHỈ lần
            // tạo: thêm ở mọi lần bấm là kéo lại người đã bị bỏ ra khỏi phòng. Best-effort: CRM hỏng thì
            // phòng vẫn mở, người phụ trách tự bấm Chat là vào (họ nhìn thấy cơ hội).
            if (created && type == "deal")
            {
                chiTiet ??= await deals.GetDetailAsync(a.SessionId, entityId!, ct);
                var owners = (chiTiet?.Owners ?? Array.Empty<int>()).Where(x => x != me).ToArray();
                if (owners.Length > 0) await repo.AddMembersAsync(a.TenantId, id, me, owners, ct);
            }
            return Results.Json(new { id, roomKey = key, created }, Web);
        });

        // Bấm Chat trên một bản ghi: CHỈ HỎI phòng đã có chưa + mình có trong đó không, KHÔNG tạo gì.
        // Phòng sinh ra lúc gửi tin đầu tiên (POST /rooms) — bấm nhầm rồi đóng ô là không để lại gì.
        // Phòng cơ hội: kiểm quyền xem NGAY ở đây (chưa là thành viên) để người không có quyền biết luôn,
        // không phải gõ xong tin mới bị từ chối.
        g.MapGet("/rooms/lookup", async (HttpContext ctx, TkSessionStore sessions, InternalChatRepository repo,
            TourkitAiProxy.Services.Deals.DealOpportunityClient deals, ChatEventBus bus, IConfiguration cfg,
            TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            string? entityType, string? entityId, int? ownerUserId, string? ownerSig, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var key = InternalRoomKey.ForLookup(entityType, entityId);
            if (key is null) return Bad("Cần entityType (order | company | deal | key) và mã hợp lệ");
            var (id, member, data) = await repo.FindRoomByKeyAsync(a!.TenantId, key, me, ct);
            // Đã ở phòng → mở thẳng, không qua POST /rooms; đồng bộ trưởng nhóm theo người tạo bản ghi ngay đây.
            if (member && id is long rid
                && SignedOwner(cfg, a.TenantId, entityType?.Trim().ToLowerInvariant(), key, ownerUserId, ownerSig) is int so)
                await AfterOwnerSyncAsync(repo, bus, sessions, api, redis, lf, a, me, rid, so,
                    await repo.SyncOwnerAsync(a.TenantId, rid, so, ct), ct);
            if (!member && entityType!.Trim().ToLowerInvariant() == "deal")
            {
                var (cam, _) = await CheckDealViewAsync(sessions, deals, a, me, key["deal:".Length..], ct);
                if (cam is not null) return cam;
            }
            // canView: phòng có rồi, mình chưa ở nhưng có CHAT_XEM_ALL → widget mở thẳng ở chế độ chỉ xem thay vì ô nháp.
            var canView = id is not null && !member && await SessionAuth.IsQuanTriChatAsync(a.SessionId, sessions, ct);
            return Results.Json(new { id, member, roomKey = key, data, canView }, Web);
        });

        g.MapGet("/rooms/{id:long}/messages", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, string? beforeUtc, long? beforeId, long? afterId, int? limit,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var n = Math.Clamp(limit ?? 50, 1, 100);
            var viewAll = await SessionAuth.IsQuanTriChatAsync(a!.SessionId, sessions, ct);   // CHAT_XEM_ALL: đọc cả phòng mình không ở
            IReadOnlyList<InternalMessage>? msgs;
            if (afterId is { } sau)
                msgs = await repo.ListMessagesAfterAsync(a.TenantId, id, me, sau, n, viewAll, ct);
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
                msgs = await repo.ListMessagesAsync(a.TenantId, id, me, truoc, beforeId, n, viewAll, ct);
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
            // Người gửi cũng nằm trong Recipients: họ có thể đang mở phòng ở tab thứ hai. Tóm tắt kèm theo để
            // máy người nhận tự cập nhật danh sách, không gọi lại GET /rooms ở mỗi tin.
            bus.Publish(new ChatEvent(a.TenantId, id, "noi-bo", r.Id)
            {
                Recipients = r.Members,
                Summary = new InternalEventSummary(r.CreatedUtc ?? DateTime.UtcNow, InternalMessagePreview.Of(body, att), me, false),
            });
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

        // "Xoá phía tôi": xoá mềm, chỉ với người bấm (phòng + tin cũ ẩn; tin mới làm phòng hiện lại).
        g.MapPost("/rooms/{id:long}/hide", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            return await repo.HideRoomAsync(a!.TenantId, id, me, ct) ? Results.NoContent() : NotMember();
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
            InternalChatRepository repo, ChatEventBus bus, TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<RenameRoomRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");
            var t = req.Title?.Trim();
            var title = string.IsNullOrEmpty(t) ? null : t[..Math.Min(t.Length, 120)];
            if (!await repo.RenameRoomAsync(a!.TenantId, id, me, title, ct)) return CannotConfigure();
            await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id, "đổi tên", Array.Empty<int>(), title, ct);
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        // Thả / đổi / gỡ cảm xúc lên một tin ({ emoji: "👍" | null }). Bộ cố định (InternalReactions); mỗi người
        // một biểu tượng trên một tin. Báo "tin đã đổi" để máy mọi người nạp lại đúng tin đó.
        g.MapPut("/messages/{id:long}/reaction", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<ReactionRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");
            string? emoji = null;
            if (!string.IsNullOrWhiteSpace(req.Emoji) && (emoji = InternalReactions.Normalize(req.Emoji)) is null)
                return Bad("Biểu tượng không hợp lệ (" + string.Join(" ", InternalReactions.Allowed) + ")");
            var roomId = await repo.SetReactionAsync(a!.TenantId, id, me, emoji, ct);
            if (roomId is null) return Results.Json(new { error = "Không thả được cảm xúc cho tin này" }, statusCode: 403);
            await NotifyMessageChangedAsync(repo, bus, a.TenantId, roomId.Value, id, ct);
            return Results.NoContent();
        });

        // Thay tham số tự do của phòng ({ data: {...} | null }). Widget gọi khi trang nhúng mở lại bản ghi với
        // tham số KHÁC cái đang lưu (vd đơn đổi đường dẫn) — cùng tham số thì không gọi, không ghi gì.
        g.MapPut("/rooms/{id:long}/data", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<RoomDataRequest>(ctx, ct);
            if (req is null) return Bad("Thân request không đọc được");
            var (dataJson, sai) = InternalRoomData.Normalize(req.Data);
            if (sai is not null) return Bad(sai);
            var (ok, changed) = await repo.SetRoomDataAsync(a!.TenantId, id, me, dataJson, ct);
            if (!ok) return NotMember();
            if (changed) await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);   // người khác thấy nút mới
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
            await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id, "thêm", them, null, ct);
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        g.MapDelete("/rooms/{id:long}/members/{userId:int}", async (long id, int userId, HttpContext ctx,
            TkSessionStore sessions, InternalChatRepository repo, ChatEventBus bus, TourKitApiClient api,
            RedisStore redis, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var (ok, newOwner) = await repo.RemoveMemberAsync(a!.TenantId, id, me, userId, ct);
            if (!ok)
                return Results.Json(new { error = userId == me
                    ? "Không rời được phòng này (bạn không ở trong phòng, hoặc là phòng 1-1)"
                    : "Chỉ trưởng nhóm mới bỏ được người khác khỏi phòng" }, statusCode: 403);
            if (userId == me && newOwner is int no)
                await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id, "rời, chuyển quyền", new[] { no }, null, ct);
            else
                await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id,
                    userId == me ? "rời" : "bỏ", new[] { userId }, null, ct);
            // Báo cả người VỪA bị bỏ: tab của họ phải biết mà đóng phòng.
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, userId, ct);
            return Results.NoContent();
        });

        // Tự vào một phòng đang xem nhờ quyền "Xem tất cả đoạn chat" (CHAT_XEM_ALL) — để nhắn được. Không áp phòng 1-1.
        g.MapPost("/rooms/{id:long}/join", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            if (!await SessionAuth.IsQuanTriChatAsync(a!.SessionId, sessions, ct))
                return Results.Json(new { error = "Cần quyền Xem tất cả đoạn chat để tự vào phòng" }, statusCode: 403);
            if (!await repo.JoinRoomAsync(a.TenantId, id, me, ct))
                return Results.Json(new { error = "Không vào được phòng này (phòng 1-1, đã xoá, hoặc bạn đã ở trong phòng)" }, statusCode: 403);
            await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id, "vào", Array.Empty<int>(), null, ct);
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        // Chuyển quyền trưởng nhóm ({ userId }) — chỉ trưởng nhóm; phòng đơn/công ty không chuyển tay.
        g.MapPost("/rooms/{id:long}/owner", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, TourKitApiClient api, RedisStore redis, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var req = await ReadBodyAsync<TransferOwnerRequest>(ctx, ct);
            if (req is null || req.UserId <= 0) return Bad("Chưa chọn người nhận quyền");
            if (!await repo.TransferOwnerAsync(a!.TenantId, id, me, req.UserId, ct))
                return Results.Json(new { error = "Không chuyển được quyền (bạn không phải trưởng nhóm, người nhận không ở trong phòng, hoặc phòng đơn hàng/công ty — trưởng nhóm luôn là người tạo)" }, statusCode: 403);
            await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, id, "chuyển quyền", new[] { req.UserId }, null, ct);
            await NotifyRoomChangedAsync(repo, bus, a.TenantId, id, null, ct);
            return Results.NoContent();
        });

        // Xoá phòng với MỌI người — chỉ trưởng nhóm. Khác "Xoá phía tôi" (/hide: chỉ ẩn với người bấm).
        g.MapDelete("/rooms/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var ra = await repo.DeleteRoomAsync(a!.TenantId, id, me, ct);
            if (ra.Length == 0)
                return Results.Json(new { error = "Chỉ trưởng nhóm mới xoá được phòng" }, statusCode: 403);
            // Phòng không còn thành viên → báo thẳng những người vừa bị đưa ra; widget hỏi lại phòng, nhận 403, tự đóng.
            bus.Publish(new ChatEvent(a.TenantId, id, "noi-bo-phong", null) { Recipients = ra });
            return Results.NoContent();
        });

        // Thu hồi (xoá với mọi người) và sửa — CHỈ tin của chính mình. Báo cả phòng để màn hình người
        // khác đổi ngay, không phải tải lại (sự kiện mang mã tin đã có → widget nạp lại tin đó).
        g.MapDelete("/messages/{messageId:long}", async (long messageId, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            if (await repo.DeleteOwnMessageAsync(a!.TenantId, messageId, me, ct) is not long roomId)
                return Results.Json(new { error = "Không thấy tin của bạn để thu hồi" }, statusCode: 404);
            await NotifyMessageChangedAsync(repo, bus, a.TenantId, roomId, messageId, ct);
            return Results.NoContent();
        });

        g.MapPatch("/messages/{messageId:long}", async (long messageId, HttpContext ctx, TkSessionStore sessions,
            InternalChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var (loi, a, me) = await GuardAsync(ctx, sessions, repo, ct);
            if (loi is not null) return loi;
            var body = (await ReadBodyAsync<EditMessageRequest>(ctx, ct))?.Body?.Trim();
            if (string.IsNullOrEmpty(body)) return Bad("Tin nhắn trống — muốn bỏ hẳn thì dùng Thu hồi");
            if (body.Length > MaxBody) return Bad($"Tin nhắn dài quá {MaxBody} ký tự");
            if (await repo.EditOwnMessageAsync(a!.TenantId, messageId, me, body, ct) is not long roomId)
                return Results.Json(new { error = "Không sửa được tin này (không phải tin của bạn, hoặc đã thu hồi)" }, statusCode: 404);
            await NotifyMessageChangedAsync(repo, bus, a.TenantId, roomId, messageId, ct);
            return Results.NoContent();
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

            var viewAll = await SessionAuth.IsQuanTriChatAsync(a!.SessionId, sessions, ct);
            await using var nguon = bus.SubscribeInternalAsync(a.TenantId, me, ct, viewAll).GetAsyncEnumerator(ct);
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
                        // changed = tin ĐÃ CÓ vừa bị sửa / thu hồi (khác tin mới) — widget nạp lại tin đó.
                        // at/preview/senderId/system: tóm tắt tin mới (chỉ chat nội bộ) — widget tự cập nhật danh sách.
                        var t = e.Summary;
                        await GhiAsync($"data: {JsonSerializer.Serialize(new
                        {
                            roomId = e.ConversationId, messageId = e.MessageId, changed = e.Loai == "noi-bo-sua",
                            at = t?.At, preview = t?.Preview, senderId = t?.SenderUserId, system = t?.System,
                        }, Web)}\n\n");
                        toi = nguon.MoveNextAsync().AsTask();
                    }
                }
            }
            catch (OperationCanceledException) { }   // tab đóng — chuyện thường
            return Results.Empty;
        });

        // Đường không khớp (vd mã phòng không phải số) → 404 JSON. Không có dòng này thì rơi xuống SPA
        // fallback và widget nhận một trang HTML kèm mã 404.
        g.Map("/{**rest}", () => Results.Json(new { error = "Không có đường này" }, statusCode: 404));

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

    /// <summary>
    /// Có được xem cơ hội này không, đúng luật web: <c>CH_XEM_ALL</c> → mọi cơ hội (không gọi CRM); chỉ
    /// <c>CH_XEM</c> → cơ hội mình tạo / phụ trách (đọc chi tiết MỘT lần, trả lại để dùng tiếp).
    /// Err null = được xem.
    /// </summary>
    private static async Task<(IResult? Err, TourkitAiProxy.Services.Deals.DealOpportunityClient.DealDetail? Detail)>
        CheckDealViewAsync(TkSessionStore sessions, TourkitAiProxy.Services.Deals.DealOpportunityClient deals,
            SessionAuth.Ctx a, int me, string dealId, CancellationToken ct)
    {
        await sessions.EnsurePermissionsAsync(a.SessionId, ct);
        if (sessions.HasPermission(a.SessionId, TkPermissionCodes.CoHoiXemTatCa)) return (null, null);
        if (!sessions.HasPermission(a.SessionId, TkPermissionCodes.CoHoiXem)) return (NoDealAccess(), null);
        var d = await deals.GetDetailAsync(a.SessionId, dealId, ct);
        if (d is null)
            return (Results.Json(new { error = "Chưa kiểm được quyền xem cơ hội với CRM — thử lại sau" }, statusCode: 503), null);
        if (!d.Exists || !TourkitAiProxy.Domain.Deals.DealAccess.IsOwner(me, d.InsUid, string.Join(',', d.Owners)))
            return (NoDealAccess(), null);
        return (null, d);
    }

    /// <summary>
    /// Trưởng nhóm phòng đơn/công ty do CRM ký (<see cref="InternalRoomOwnerSignature"/>, chung <c>Sso:Secret</c>).
    /// null = không truyền / loại phòng không nhận / chữ ký sai — chỗ gọi coi như không có.
    /// </summary>
    private static int? SignedOwner(IConfiguration cfg, string tenant, string? entityType, string roomKey,
        int? ownerUserId, string? ownerSig)
        => InternalRoomOwner.IsCrmOwned(entityType) && ownerUserId is int o && !string.IsNullOrWhiteSpace(ownerSig)
           && InternalRoomOwnerSignature.Verify(SsoSecret(cfg), tenant, roomKey, o, ownerSig) ? o : null;

    /// Sso:Secret đã giải "ENC:" (Crypton — chậm, dẫn khoá mỗi lần gọi) → giải một lần, giữ theo chuỗi gốc.
    private sealed record SsoCache(string? Raw, string? Plain);   // lớp chứ không tuple: đọc/ghi tham chiếu là nguyên tử
    private static SsoCache? _sso;
    private static string? SsoSecret(IConfiguration cfg)
    {
        var raw = cfg["Sso:Secret"];
        if (_sso is { } c && c.Raw == raw) return c.Plain;
        var plain = !string.IsNullOrEmpty(raw) && raw.StartsWith("ENC:", StringComparison.Ordinal)
            ? TourkitAiProxy.Infrastructure.Security.Crypton.Decrypt(raw[4..]) : raw;
        _sso = new SsoCache(raw, plain);
        return plain;
    }

    private static IResult NoDealAccess()
        => Results.Json(new { error = "Bạn không có quyền xem cơ hội này" }, statusCode: 403);

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
        var log = lf.CreateLogger("chat.internal");
        var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId, log, ct);
        if (staffs.Count == 0)
            return Results.Json(new { error = "Chưa tải được danh sách nhân viên — thử lại sau" }, statusCode: 503);
        var hopLe = staffs.Select(ChatStaffDirectory.StaffId).OfType<int>().ToHashSet();
        var la = ids.Where(x => !hopLe.Contains(x)).ToList();
        if (la.Count > 0)
        {
            // Có mã lạ: có thể là nhân viên MỚI chưa vào đệm 2 tiếng → hỏi lại ERP một lần, bỏ qua đệm.
            (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId, log, ct, skipCache: true);
            hopLe = staffs.Select(ChatStaffDirectory.StaffId).OfType<int>().ToHashSet();
            la = ids.Where(x => !hopLe.Contains(x)).ToList();
        }
        return la.Count > 0 ? Bad("Có người không thuộc công ty: " + string.Join(", ", la)) : null;
    }

    /// <summary>
    /// Như <see cref="CheckStaffAsync"/> nhưng LỌC thay vì từ chối: giữ người của công ty này, bỏ mã lạ (sau một lần hỏi lại
    /// ERP bỏ qua đệm — có thể là nhân viên mới). Lỗi chỉ khi không tải được danh bạ.
    /// </summary>
    private static async Task<(List<int> Keep, IResult? Err)> KeepStaffAsync(List<int> ids, TkSessionStore sessions,
        TourKitApiClient api, RedisStore redis, SessionAuth.Ctx a, ILoggerFactory lf, CancellationToken ct)
    {
        if (ids.Count == 0) return (ids, null);
        var log = lf.CreateLogger("chat.internal");
        var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId, log, ct);
        if (staffs.Count == 0)
            return (ids, Results.Json(new { error = "Chưa tải được danh sách nhân viên — thử lại sau" }, statusCode: 503));
        var hopLe = staffs.Select(ChatStaffDirectory.StaffId).OfType<int>().ToHashSet();
        if (ids.Any(x => !hopLe.Contains(x)))
        {
            (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId, log, ct, skipCache: true);
            hopLe = staffs.Select(ChatStaffDirectory.StaffId).OfType<int>().ToHashSet();
        }
        var keep = ids.Where(hopLe.Contains).ToList();
        if (keep.Count < ids.Count)
            log.LogInformation("Phòng theo bản ghi: bỏ {N} người không có trong danh bạ nhân viên ({Ids})",
                ids.Count - keep.Count, string.Join(",", ids.Where(x => !hopLe.Contains(x))));
        return (keep, null);
    }

    /// <summary>
    /// Ghi dòng "A đã thêm B…" vào phòng rồi báo như một tin mới. Tên lấy từ danh bạ nhân viên (đệm
    /// 2 tiếng), không có thì lùi về tên đăng nhập / "#mã". BEST-EFFORT: hỏng thì ghi log và bỏ qua —
    /// việc đổi phòng đã xong, không được trả lỗi vì một dòng thông báo.
    /// </summary>
    private static async Task PostNoticeAsync(InternalChatRepository repo, ChatEventBus bus, TkSessionStore sessions,
        TourKitApiClient api, RedisStore redis, ILoggerFactory lf, SessionAuth.Ctx a, int me, long roomId,
        string viec, IReadOnlyCollection<int> ids, string? gi, CancellationToken ct)
    {
        var log = lf.CreateLogger("chat.internal");
        try
        {
            var (staffs, _) = await ChatStaffDirectory.ListAsync(sessions, api, redis, a.TenantId, a.SessionId, log, ct);
            var ten = new Dictionary<int, string>();
            foreach (var s in staffs)
                if (ChatStaffDirectory.StaffId(s) is int sid && ChatStaffDirectory.StaffName(s) is { } n) ten[sid] = n;
            string Ten(int u) => ten.TryGetValue(u, out var n) ? n : (u == me ? a.Username : $"#{u}");
            if (ids.Count > 0) gi = string.Join(", ", ids.Select(Ten));
            var cau = InternalRoomNotice.Text(Ten(me), viec, gi);
            var r = await repo.AppendSystemMessageAsync(a.TenantId, roomId, cau, ct);
            if (r.Id is not null)
                bus.Publish(new ChatEvent(a.TenantId, roomId, "noi-bo", r.Id)
                {
                    Recipients = r.Members,
                    Summary = new InternalEventSummary(r.CreatedUtc ?? DateTime.UtcNow, InternalMessagePreview.Of(cau, null), null, true),
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Không ghi được thông báo hệ thống phòng {Room}", roomId);
        }
    }

    /// Sau đồng bộ trưởng nhóm phòng đơn/công ty: có đổi thì ghi một dòng hệ thống (thêm người tạo vào phòng / người tạo là
    /// trưởng nhóm) rồi báo cả phòng. Không đổi gì (trường hợp thường — mở lại phòng) thì không ghi, không báo.
    private static async Task AfterOwnerSyncAsync(InternalChatRepository repo, ChatEventBus bus, TkSessionStore sessions,
        TourKitApiClient api, RedisStore redis, ILoggerFactory lf, SessionAuth.Ctx a, int me, long roomId, int owner,
        InternalChatRepository.OwnerSync s, CancellationToken ct)
    {
        if (!s.Added && !s.Changed) return;
        await PostNoticeAsync(repo, bus, sessions, api, redis, lf, a, me, roomId,
            s.Added ? "thêm trưởng nhóm" : "đổi trưởng nhóm", new[] { owner }, null, ct);
        await NotifyRoomChangedAsync(repo, bus, a.TenantId, roomId, null, ct);
    }

    /// Báo "một tin đã đổi" (sửa / thu hồi) cho thành viên phòng. Loai riêng để widget nạp lại đúng tin.
    private static async Task NotifyMessageChangedAsync(InternalChatRepository repo, ChatEventBus bus, string tenant,
        long roomId, long messageId, CancellationToken ct)
        => bus.Publish(new ChatEvent(tenant, roomId, "noi-bo-sua", messageId)
            { Recipients = await repo.MembersAsync(tenant, roomId, ct) });

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
