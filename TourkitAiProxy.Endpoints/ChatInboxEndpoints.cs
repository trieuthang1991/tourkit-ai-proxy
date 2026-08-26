// Endpoints/ChatInboxEndpoints.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Infrastructure.Chat.Channels;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Services.Storage;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Domain.Chat;
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

    /// <summary>
    /// Các tiền tố đường dẫn CHỈ thuộc hộp thư chat. <c>Program.cs</c> dùng chính danh sách này cho
    /// nhánh <b>tắt cờ</b> <c>Features:Chat</c> để trả 404 — nhờ vậy thêm nhóm endpoint mới không
    /// thể quên cập nhật nơi thứ hai.
    ///
    /// <para><b>Vì sao không chặn thẳng tiền tố <c>/api/v1/chat</c>:</b> <c>POST /api/v1/chat</c> và
    /// <c>/api/v1/chat/stream</c> là <b>Trợ lý số liệu</b> — tính năng KHÁC, không nằm sau cờ này.
    /// Chặn cả cụm là giết nhầm một tính năng đang chạy thật.</para>
    ///
    /// <para><b>Vì sao phải liệt kê thay vì để rơi:</b> không map ≠ 404. <c>app.MapFallback</c> (deep
    /// link SPA) nuốt mọi đường không khớp kể cả <c>/api/**</c> và trả <c>index.html</c> kèm status
    /// <b>200</b> — client gọi API nhận HTML thay vì lỗi, lần ra nguyên nhân rất mất công.</para>
    /// </summary>
    public static readonly string[] DuongRieng =
    {
        "/api/v1/chat/conversations",
        "/api/v1/chat/channels",
        "/api/v1/chat/messages",
        "/api/v1/chat/quick-replies",
        "/api/v1/chat/events",
        "/api/v1/chat/oauth",
        "/api/v1/chat/webhook",
    };

    public static IEndpointRouteBuilder MapChatInboxEndpoints(this IEndpointRouteBuilder routes)
    {
        MapWebhook(routes);
        MapInbox(routes);
        return routes;
    }

    // ── Webhook ─────────────────────────────────────────────────────────────

    /// Tên kênh trên đường dẫn → enum. Một nguồn cho cả ba kênh, thêm kênh chỉ thêm 1 dòng.
    private static readonly Dictionary<string, ChatChannel> TenKenh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zalo"] = ChatChannel.Zalo,
        ["messenger"] = ChatChannel.Messenger,
        ["telegram"] = ChatChannel.Telegram,
    };

    private static void MapWebhook(IEndpointRouteBuilder routes)
    {
        // Tenant nằm trên ĐƯỜNG DẪN vì webhook không có phiên đăng nhập: mỗi công ty khai một URL
        // riêng ở trang quản trị của kênh. Không nhận tenant từ thân request — thân do người ngoài
        // gửi, tin vào đó là ai cũng ghi được tin vào hộp thư công ty khác.
        //
        // MỘT đường dẫn cho MỌI kênh. Viết riêng từng kênh thì phần chung (đọc thân thô, kiểm chữ
        // ký, trả 200 ngay, xử lý nền) bị chép ba lần và sớm muộn lệch nhau.
        // MỘT hàm xử lý cho mọi kênh, có hay không có mã tài khoản trên URL.
        //
        // Telegram BẮT BUỘC dạng .../{tenantId}/{accountId} vì thân tin không nói bot nào (xem
        // TelegramChatAdapter). Zalo/Messenger dùng dạng .../{tenantId} — nhiều OA/Trang chung một
        // đường, adapter tự soát ra tài khoản từ app_id/pageId trong thân tin.
        async Task<IResult> XuLy(string kenh, string tenantId, string? accountId, HttpContext ctx,
            ChatInboundService svc, ChatRepository repo, ILoggerFactory lf, CancellationToken ct)
        {
            var log = lf.CreateLogger("chat.webhook");
            if (!TenKenh.TryGetValue(kenh, out var loaiKenh)) return Results.NotFound();
            var adapter = svc.Adapter(loaiKenh);
            if (adapter is null) return Results.NotFound();

            // Đọc THÂN THÔ: chữ ký ký trên đúng chuỗi này, parse rồi dựng lại là chữ ký hỏng.
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var taiKhoan = await adapter.VerifyAsync(tenantId, accountId, raw, ctx.Request.Headers, ct);
            if (taiKhoan is null)
            {
                log.LogWarning("[chat/webhook] chữ ký sai hoặc chưa khai tài khoản, kênh={K} tenant={T}",
                    kenh, tenantId);
                return Results.Unauthorized();
            }

            // Vẫn bóc MỘT lần ở đây, nhưng CHỈ để lấy id sự kiện làm khoá chống trùng — chống trùng
            // phải xảy ra lúc GHI, không thì kênh gửi lại sẽ tạo hai dòng và bot trả lời hai lần.
            var sk = adapter.Parse(raw);
            if (sk.Count == 0) return Results.Ok();
            var maSuKien = sk[0].ExternalMsgId;

            // Chỉ GHI thân thô rồi trả 200. XỬ LÝ là việc của ChatInboundWorker: đã trả 200 thì kênh
            // không gửi lại nữa, nên việc còn dở KHÔNG được nằm trong bộ nhớ — recycle/deploy/crash
            // lúc đó là mất hẳn tin của khách mà không dấu vết.
            var id = await repo.EnqueueInboundAsync(tenantId, loaiKenh, taiKhoan, maSuKien, raw, ct);
            if (id is null)
                log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, kênh={K} tenant={T} sk={S}",
                    kenh, tenantId, maSuKien);

            return Results.Ok();
        }

        routes.MapPost("/api/v1/chat/webhook/{kenh}/{tenantId}", (
            string kenh, string tenantId, HttpContext ctx, ChatInboundService svc, ChatRepository repo,
            ILoggerFactory lf, CancellationToken ct) => XuLy(kenh, tenantId, null, ctx, svc, repo, lf, ct));

        routes.MapPost("/api/v1/chat/webhook/{kenh}/{tenantId}/{accountId}", (
            string kenh, string tenantId, string accountId, HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, ILoggerFactory lf, CancellationToken ct)
            => XuLy(kenh, tenantId, accountId, ctx, svc, repo, lf, ct));

        // Meta xác minh địa chỉ webhook bằng một lượt GET riêng trước khi bắt đầu gửi tin. Thiếu
        // đường này thì không đăng ký được webhook Messenger, dù phần nhận tin đã đúng hết.
        routes.MapGet("/api/v1/chat/webhook/messenger/{tenantId}", async (
            string tenantId, HttpContext ctx, MessengerChatAdapter adapter, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var challenge = await adapter.XacMinhDangKyAsync(tenantId,
                q["hub.mode"], q["hub.verify_token"], q["hub.challenge"], ct);
            // Trả chuỗi THÔ, không bọc JSON — Meta so khớp nguyên văn.
            return challenge is null ? Results.Forbid() : Results.Text(challenge);
        });
    }

    // ── Hộp thư ─────────────────────────────────────────────────────────────

    private static void MapInbox(IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/chat");

        // ── Đẩy sự kiện: thay cho hỏi-lại-4-giây ────────────────────────────
        //
        // Dùng SSE chứ không SignalR — dự án đã có sẵn SSE ở CẢ HAI đầu (AiEndpoints, DealEndpoints;
        // window.tourkitUtil.readSSE ở frontend) và frontend KHÔNG có bundler, nên thêm SignalR là
        // thêm một thẻ script CDN VÀ một import bundle-entry — hai danh sách đó đã lệch nhau một
        // lần rồi. Nhu cầu thật cũng chỉ một chiều: server báo "có tin mới", nhân viên gõ thì POST.
        //
        // ⚠️ EventSource KHÔNG gửi được header tuỳ ý, nên phiên đi qua ?sessionId=. SessionAuth.Read
        // đọc X-Session-Id rồi mới tới Query["sessionId"] nên chỗ này không cần sửa lớp xác thực.
        g.MapGet("/events", async (HttpContext ctx, TkSessionStore sessions, ChatEventBus bus,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";   // nginx: đừng gom lại rồi mới đẩy
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
                ?.DisableBuffering();
            await ctx.Response.StartAsync(ct);

            async Task GhiAsync(string dong)
            {
                await ctx.Response.WriteAsync(dong, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            // Đọc sự kiện và nhịp giữ sống trên CÙNG MỘT luồng, đua nhau bằng WhenAny.
            //
            // Vì sao không tách nhịp ra một tác vụ nền chạy song song cho gọn: hai bên cùng ghi
            // là hỏng khung SSE giữa chừng, mà chống lại thì phải thêm khoá. Một luồng ghi thì
            // không có gì để chống. (Tác vụ nền trong file endpoint này còn bị một chốt chặn từ
            // chối — xem ChatInboundEventTests.Webhook_khong_con_fire_and_forget.)
            //
            // Nhịp 25 giây: hộp thư im hàng giờ là bình thường, mà proxy thường cắt kết nối rảnh
            // sau 60 giây — không có nhịp thì cứ mỗi phút EventSource lại nối lại một lần. Dòng
            // bắt đầu bằng dấu hai chấm là chú thích của giao thức SSE, trình duyệt bỏ qua.
            await using var nguon = bus.NgheAsync(a.TenantId, ct).GetAsyncEnumerator(ct);
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
                        await GhiAsync($"data: {JsonSerializer.Serialize(nguon.Current, Web)}\n\n");
                        toi = nguon.MoveNextAsync().AsTask();
                    }
                }
            }
            catch (OperationCanceledException) { }   // tab đóng — chuyện thường, không phải lỗi
            return Results.Empty;
        });

        g.MapGet("/conversations", async (HttpContext ctx, TkSessionStore sessions, ChatRepository repo,
            short? status, string? search, short? channel, bool? unread, bool? mine,
            string? cursor, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();

            // Không có quyền xem toàn công ty → chỉ thấy phần của mình + phần chưa ai nhận.
            // Kẹp ở SQL, không lọc phía client.
            var xemHet = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            var chiCuaToi = xemHet ? null : a.Username;

            // Mã hỏng → Giai() trả null → coi như trang đầu. Không ném: con trỏ nằm trên URL,
            // người dùng sửa tay được và mã cũ từ bản trước còn trong lịch sử trình duyệt.
            const int soDong = 60;
            var items = await repo.ListConversationsAsync(a.TenantId, status, chiCuaToi, search,
                kenh: channel, giaoCho: mine == true ? a.Username : null, chiChuaDoc: unread == true,
                sau: ChatCursor.Giai(cursor), limit: soDong, nguoiDung: a.Username, ct: ct);
            var dem = await repo.CountAsync(a.TenantId, chiCuaToi, a.Username, ct);
            return Results.Json(new
            {
                items = items.Select(Shape),
                counts = new
                {
                    moi = dem.TheoTrangThai.GetValueOrDefault((short)0),
                    dangXuLy = dem.TheoTrangThai.GetValueOrDefault((short)1),
                    daDong = dem.TheoTrangThai.GetValueOrDefault((short)2),
                    chuaDoc = dem.ChuaDoc,
                    tong = dem.Tong,
                },
                // Dải kênh bên trái: kênh nào có bao nhiêu hội thoại. Khoá là số của ChatChannel.
                channelCounts = dem.TheoKenh.ToDictionary(k => k.Key.ToString(), k => k.Value),
                xemToanCongTy = xemHet,
                // Ít hơn số dòng xin = hết dữ liệu → null để giao diện biết dừng.
                // Luôn trả mã thì giao diện cuộn mãi không hết.
                nextCursor = items.Count < soDong ? null
                           : ChatCursor.Ma(new(items[^1].LastActivityAt, items[^1].Id)),
            }, Web);
        });

        g.MapGet("/conversations/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions,
            ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            var goc = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();   // id của tenant khác cũng rơi vào đây

            var tin = await repo.ListMessagesAsync(a.TenantId, id, 120, ct);
            var lienHe = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            var cuaSo = ChatRules.TinhCuaSo((ChatChannel)v.Channel, v.ContactRepliedAt, DateTime.UtcNow);
            return Results.Json(new
            {
                conversation = Shape(v),
                // Hồ sơ khách cho panel bên phải. Chỉ những gì kênh thật sự cho biết — chưa nối CRM
                // nên crmCustomerId còn trống, giao diện nói thẳng điều đó thay vì bịa một thẻ khách.
                contact = lienHe is null ? null : new
                {
                    lienHe.DisplayName, lienHe.AvatarUrl, lienHe.Phone, lienHe.Email,
                    lienHe.CrmCustomerId, lienHe.CreatedUtc,
                },
                messages = tin.Select(m => new
                {
                    m.Id, m.Direction, m.SenderKind, m.SenderUsername, m.Kind,
                    m.Body, m.State, m.ErrorMessage, m.CreatedUtc,
                    // Đính kèm đã CHUẨN HOÁ về cùng một hình dạng cho cả ba kênh — xem
                    // ChatAttachment. Giao diện không cần biết Zalo/Messenger/Telegram gói tệp
                    // khác nhau thế nào.
                    files = ChatAttachment.Doc((ChatChannel)v.Channel, (ChatKind)m.Kind, m.Attachment,
                        m.Direction).Select(f => new
                    {
                        f.Ten, f.Kich, f.Lat, f.Lon,
                        // Telegram chỉ cho file_id (khoá bot token) — phải đi qua máy chủ để giấu
                        // token; Zalo/Messenger/ảnh mình tự gửi thì đã có URL công khai thẳng.
                        url = f.Url ?? (f.FileId is null ? null
                            : $"{goc}/api/v1/chat/messages/{m.Id}/file?fid={Uri.EscapeDataString(f.FileId)}"
                              + $"&sessionId={Uri.EscapeDataString(a.SessionId)}"),
                    }),
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
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();

            // Có đính kèm thì chữ là CHÚ THÍCH, được phép rỗng. Không đính kèm thì bắt buộc có chữ
            // — một tin trống trơn không đính kèm gì là gửi nhầm phím Enter.
            var coDinhKem = !string.IsNullOrWhiteSpace(body.AttachmentUrl);
            if (!coDinhKem && string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Chưa nhập nội dung" });

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var cuaSo = ChatRules.TinhCuaSo((ChatChannel)v.Channel, v.ContactRepliedAt, DateTime.UtcNow);
            if (!cuaSo.Open) return Results.BadRequest(new { error = cuaSo.Reason });

            var loai = coDinhKem ? (body.AttachmentKind == "anh" ? ChatKind.Anh : ChatKind.Tep) : ChatKind.Chu;
            // Đính kèm ghi theo hình dạng CHUẨN {ten,kich,url} — ChatAttachment.Doc đọc thẳng
            // không cần bóc theo kênh, vì đây là tin MÌNH GỬI (chieu=1), không phải tin kênh gửi tới.
            var attJson = coDinhKem
                ? new JsonObject { ["ten"] = body.AttachmentName, ["kich"] = body.AttachmentSize,
                                   ["url"] = body.AttachmentUrl }.ToJsonString()
                : null;
            var chu = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text.Trim();

            var msgId = await repo.AppendMessageAsync(a.TenantId, id, (ChatChannel)v.Channel,
                ChatDirection.Ra, ChatSender.NhanVien, a.Username, loai, chu,
                attJson, null, ChatState.Cho, ct);
            if (msgId is null) return Results.Problem("Không ghi được tin");

            var tomTat = coDinhKem ? (loai == ChatKind.Anh ? "Đã gửi 1 ảnh" : "Đã gửi 1 tệp") : chu!;
            await repo.TouchConversationAsync(a.TenantId, id, ChatRules.TomTat(tomTat), false, ct);
            // Người thật vừa trả lời → bot câm một lúc, nếu không nó nói đè lên nhân viên.
            await repo.PauseBotAsync(a.TenantId, id, (int)ChatRules.BotCamMacDinh.TotalMinutes, ct);
            await repo.EnqueueOutboxAsync(a.TenantId, id, msgId.Value, ct);
            bus.Bao(new(a.TenantId, id, "tin-moi", msgId.Value));

            return Results.Json(new { ok = true, messageId = msgId }, Web);
        });

        // ── Gửi ảnh/tệp: tải lên kho (R2/S3/local theo Storage:Provider) rồi trả URL để FE gọi
        // /send với AttachmentUrl. Tách hai bước (tải lên → gửi) để nhân viên xem trước ảnh trước
        // khi bấm gửi thật, giống mọi app chat khác.
        g.MapPost("/conversations/{id:long}/upload", async (long id, HttpRequest req, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, IChatFileStorage kho, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (!kho.Configured)
                return Results.Json(new { error = $"Chưa cấu hình Storage:{kho.Provider} — xem appsettings.example.json" },
                    statusCode: 503);
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Thiếu tệp" });

            var form = await req.ReadFormAsync(ct);
            var tep = form.Files.GetFile("file");
            if (tep is null || tep.Length == 0) return Results.BadRequest(new { error = "Thiếu tệp" });
            // 15MB — đủ cho ảnh chụp điện thoại + PDF vài trang. Chặn ở đây, không để tràn tới
            // R2/S3 rồi mới báo lỗi vừa tốn băng thông vừa chậm.
            if (tep.Length > 15 * 1024 * 1024)
                return Results.BadRequest(new { error = "Tệp quá 15MB" });

            var laAnh = (tep.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var duoi = Path.GetExtension(tep.FileName);
            var key = $"chat/{a.TenantId}/{id}/{Guid.NewGuid():N}{duoi}";

            await using var s = tep.OpenReadStream();
            var url = await kho.UploadAsync(key, s, tep.ContentType ?? "application/octet-stream", ct);
            if (url.StartsWith('/')) url = $"{ctx.Request.Scheme}://{ctx.Request.Host}{url}";

            return Results.Json(new
            {
                url, name = tep.FileName, size = tep.Length, kind = laAnh ? "anh" : "tep",
            }, Web);
        });

        // Proxy tệp Telegram: bot token KHÔNG được lọt ra trình duyệt, nên trình duyệt gọi vào
        // đây, máy chủ tự đổi file_id → đường tải thật rồi chuyển tiếp byte.
        g.MapGet("/messages/{msgId:long}/file", async (long msgId, string fid, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, IHttpClientFactory httpFac,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            // Tin phải thuộc hội thoại của CHÍNH tenant này — chặn ở đây thay vì tin vào id đoán được.
            if (!await repo.MessageBelongsToTenantAsync(a.TenantId, msgId, ct))
                return Results.NotFound();

            var token = cfg["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token)) return Results.NotFound();

            var http = httpFac.CreateClient();
            using var meta = await http.GetAsync(
                $"https://api.telegram.org/bot{token}/getFile?file_id={Uri.EscapeDataString(fid)}", ct);
            var raw = await meta.Content.ReadAsStringAsync(ct);
            var duong = JsonNode.Parse(raw)?["result"]?["file_path"]?.ToString();
            if (string.IsNullOrWhiteSpace(duong)) return Results.NotFound();

            var res = await http.GetAsync($"https://api.telegram.org/file/bot{token}/{duong}", ct);
            if (!res.IsSuccessStatusCode) return Results.NotFound();
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return Results.File(bytes, res.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
        });

        g.MapPost("/conversations/{id:long}/assign", async (long id, AssignReq? body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // KHÔNG có trường username = NHẬN VIỆC cho chính mình. Tên lấy từ PHIÊN, không lấy từ
            // thân yêu cầu: để client tự khai tên là ai cũng gán việc cho người khác được.
            //
            // ⚠️ Bản trước giao diện gửi một thuộc tính KHÔNG tồn tại nên thân yêu cầu luôn là
            // chuỗi rỗng — tức nút "Nhận việc" thật ra đang GỠ giao việc, mà nhìn thì như chạy.
            if (body?.Username is null)
            {
                var soDong = await repo.NhanViecAsync(a.TenantId, id, a.Username, ct);
                if (soDong == 0)
                {
                    // 200 im lặng là kiểu hỏng tệ nhất: giao diện người thua vẫn hiện "của tôi",
                    // rồi hai người cùng trả lời một khách.
                    var dangGiu = await repo.AiDangGiuAsync(a.TenantId, id, ct);
                    return Results.Json(new { error = $"{dangGiu} đang xử lý hội thoại này", assignedTo = dangGiu },
                        statusCode: StatusCodes.Status409Conflict);
                }
                await repo.GhiNhatKyAsync(a.TenantId, id, a.Username, "nhan-viec", null, ct);
                bus.Bao(new(a.TenantId, id, "doi-hoi-thoai", null));
                return Results.Json(new { ok = true, assignedTo = a.Username }, Web);
            }

            // Chuỗi rỗng = nhả việc (trả về hàng chờ chung); có tên = chuyển việc cho người đó.
            // Cả hai đều CỐ Ý đè lên người đang giữ, nên không đi qua đường nguyên tử ở trên.
            var ai = string.IsNullOrWhiteSpace(body!.Username) ? null : body.Username.Trim();
            await repo.AssignAsync(a.TenantId, id, ai, ct);
            await repo.GhiNhatKyAsync(a.TenantId, id, a.Username, ai is null ? "nha-viec" : "chuyen-viec",
                ai is null ? null : new JsonObject { ["cho"] = ai }.ToJsonString(), ct);
            bus.Bao(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true, assignedTo = ai }, Web);
        });

        g.MapPatch("/conversations/{id:long}/status", async (long id, StatusReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (!Enum.IsDefined(typeof(ChatStatus), body.Status))
                return Results.BadRequest(new { error = "Trạng thái không hợp lệ" });
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetStatusAsync(a.TenantId, id, (ChatStatus)body.Status, ct);
            await repo.GhiNhatKyAsync(a.TenantId, id, a.Username, "doi-trang-thai",
                new JsonObject { ["trangThai"] = body.Status }.ToJsonString(), ct);
            bus.Bao(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true }, Web);
        });

        // ── Nhãn và ghi chú của khách ───────────────────────────────────────
        //
        // Gắn theo KHÁCH chứ không theo hội thoại: khách nhắn lại sau ba tháng vẫn còn nhãn cũ,
        // còn gắn theo hội thoại thì mỗi lần mở hội thoại mới là mất hết — đúng lúc cần nhất.
        g.MapGet("/conversations/{id:long}/tags", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();
            return Results.Json(new
            {
                items = await repo.ListTagsAsync(a.TenantId, v.Channel, v.ContactExternalId, ct),
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/tags", async (long id, TagReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Chuẩn hoá DÙNG CHUNG với lệnh gọi mẫu trả lời nhanh — cùng vấn đề, cùng lời giải.
            // Ghi thô thì "Khách VIP" và "khach vip" thành hai nhãn khác nhau.
            var nhan = ChatRules.ChuanHoaSlug(body?.Tag);
            if (nhan.Length == 0) return Results.BadRequest(new { error = "Nhãn không hợp lệ" });

            await repo.AddTagAsync(a.TenantId, v.Channel, v.ContactExternalId, nhan, ct);
            return Results.Json(new { ok = true, tag = nhan }, Web);
        });

        g.MapDelete("/conversations/{id:long}/tags/{tag}", async (long id, string tag, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Chuẩn hoá cả lúc XOÁ: nhãn nằm trên đường dẫn nên trình duyệt/người dùng có thể gửi
            // bản có dấu, mà trong CSDL chỉ có bản đã chuẩn hoá — không chuẩn hoá là xoá trượt.
            var xoa = await repo.RemoveTagAsync(a.TenantId, v.Channel, v.ContactExternalId,
                ChatRules.ChuanHoaSlug(tag), ct);
            return Results.Json(new { ok = true, removed = xoa }, Web);
        });

        g.MapGet("/conversations/{id:long}/notes", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();
            return Results.Json(new
            {
                items = await repo.ListNotesAsync(a.TenantId, v.Channel, v.ContactExternalId, 50, ct),
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/notes", async (long id, NoteReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (string.IsNullOrWhiteSpace(body?.NoiDung))
                return Results.BadRequest(new { error = "Chưa nhập nội dung ghi chú" });
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var maGhiChu = await repo.AddNoteAsync(a.TenantId, v.Channel, v.ContactExternalId,
                a.Username, body.NoiDung.Trim(), ct);
            return Results.Json(new { ok = true, id = maGhiChu }, Web);
        });

        g.MapDelete("/conversations/{id:long}/notes/{noteId:long}", async (long id, long noteId,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();
            return Results.Json(new { ok = true, removed = await repo.RemoveNoteAsync(a.TenantId, noteId, ct) }, Web);
        });

        // ── Nối khách chat với khách CRM ────────────────────────────────────
        //
        // Nối TAY, KHÔNG đoán tự động: ghép theo tên sai thường xuyên (trùng tên là chuyện bình
        // thường ở khách du lịch), còn ghép theo số điện thoại thì Zalo/Messenger không cho biết
        // số trừ khi khách tự nhắn. Nối tay đúng 100% và làm được ngay; tự động để sau khi đã có
        // dữ liệu thật xem tỉ lệ trùng thế nào.
        g.MapGet("/conversations/{id:long}/crm-search", async (long id, string? q, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, TourKitCustomerSource khach,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(q)) return Results.Json(new { items = Array.Empty<object>() }, Web);

            // Tìm bằng PHIÊN CỦA CHÍNH NHÂN VIÊN, không phải tài khoản dịch vụ — để CRM tự chặn
            // theo quyền của họ. Dùng tài khoản dịch vụ là nhân viên chỉ được xem khách của mình
            // vẫn tra ra cả kho khách của công ty.
            var kq = await khach.ListAsync(a.SessionId, new(Search: q.Trim()), 1, 10, ct);
            return Results.Json(new
            {
                items = kq.Items.Select(k => new { id = k.Id, name = k.Name, phone = k.Phone, code = k.Code }),
                total = kq.Total,
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/link-crm", async (long id, LinkCrmReq? body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Không có customerId = GỠ nối. Gỡ phải làm được: nối nhầm là bot đọc lịch sử mua của
            // người khác rồi nói với khách này, và không có đường lùi thì chỉ còn cách sửa tay CSDL.
            var ma = body?.CustomerId;
            var soDong = await repo.NoiCrmAsync(a.TenantId, v.Channel, v.ContactExternalId, ma, ct);
            if (soDong == 0) return Results.NotFound();

            await repo.GhiNhatKyAsync(a.TenantId, id, a.Username, ma is null ? "go-noi-crm" : "noi-crm",
                ma is null ? null : new JsonObject { ["khachCrm"] = ma }.ToJsonString(), ct);
            bus.Bao(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true, crmCustomerId = ma }, Web);
        });

        // Nhật ký của một hội thoại. Nằm dưới tiền tố /conversations nên đã được DuongRieng phủ.
        g.MapGet("/conversations/{id:long}/audit", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            // Hội thoại của tenant khác cũng rơi vào đây — không rò rỉ việc id đó có tồn tại hay không.
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            var ds = await repo.ListAuditAsync(a.TenantId, id, 50, ct);
            return Results.Json(new
            {
                items = ds.Select(x => new
                {
                    x.Id, x.Username, x.HanhDong, x.CreatedUtc,
                    // Trả JSON thô: giao diện tự diễn giải theo hành động, backend không phải biết
                    // cách hiển thị.
                    chiTiet = x.ChiTiet,
                }),
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/read", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            // Theo TỪNG NGƯỜI: đánh dấu chung cho cả công ty thì A mở hội thoại là B mất dấu
            // chưa đọc, và tin của khách trôi qua mắt B mà không có lỗi nào hiện ra.
            await repo.MarkReadAsync(a.TenantId, id, a.Username, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapPost("/conversations/{id:long}/bot", async (long id, BotReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // paused=false → bỏ câm ngay; true → câm theo số phút (mặc định 30).
            var phut = body.Paused ? Math.Clamp(body.Minutes ?? 30, 1, 1440) : 0;
            await repo.PauseBotAsync(a.TenantId, id, phut, ct);
            await repo.GhiNhatKyAsync(a.TenantId, id, a.Username, "tam-dung-bot",
                new JsonObject { ["phut"] = phut }.ToJsonString(), ct);
            bus.Bao(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true }, Web);
        });

        // ── Cấp quyền Zalo OA ───────────────────────────────────────────────
        //
        // Zalo KHÔNG cho copy Refresh Token từ giao diện — phải đi một vòng OAuth: mở đường cấp
        // quyền → quản trị viên OA bấm đồng ý → Zalo đá về callback kèm `code` sống rất ngắn →
        // đổi `code` lấy token. Làm tay thì phải dán URL, chép `code` trên thanh địa chỉ rồi gọi
        // curl; làm ở đây thì người dùng bấm MỘT nút.
        g.MapPost("/channels/{channel:int}/accounts/{accountId}/oauth-url", async (int channel,
            string accountId, HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ZaloOAuthStates moc, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if ((ChatChannel)channel != ChatChannel.Zalo)
                return Results.BadRequest(new { error = "Chỉ Zalo OA mới có bước cấp quyền này" });

            var khoa = await cred.GetAsync(a.TenantId, ChatChannel.Zalo, accountId, ct);
            var appId = khoa?.GetValueOrDefault("appId");
            if (string.IsNullOrWhiteSpace(appId))
                return Results.BadRequest(new { error = "Khai App ID và App Secret Key rồi bấm Lưu trước đã" });

            // redirect_uri phải khớp Y HỆT chuỗi khai bên Zalo — nên sinh ở đây một lần rồi giữ
            // luôn trong state, để lượt đổi mã dùng đúng chuỗi đó, không dựng lại và lệch.
            var quayVe = GocCongKhai(ctx, cfg) + DuongCallbackZalo;
            var state = moc.Tao(a.TenantId, accountId, quayVe);
            return Results.Json(new
            {
                url = Services.Chat.Channels.ZaloChatAdapter.DuongCapQuyen(appId!, quayVe, state),
                // Trả về để giao diện nhắc dán đúng chuỗi này vào ô Callback URL bên Zalo.
                redirectUri = quayVe,
            }, Web);
        });

        // CÔNG KHAI — Zalo đá trình duyệt về đây bằng chuyển hướng thường, không mang theo
        // X-Session-Id. Ghép lại công ty/tài khoản bằng `state` do máy chủ sinh, dùng một lần.
        g.MapGet("/oauth/zalo/callback", async (string? code, string? state, string? error,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ZaloOAuthStates moc, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return TrangCapQuyen(false, $"Zalo báo: {error}");

            var cho = moc.Nhan(state);
            if (cho is null)
                return TrangCapQuyen(false, "Lượt cấp quyền đã hết hạn hoặc đã dùng rồi. Bấm lại nút Cấp quyền OA.");
            if (string.IsNullOrWhiteSpace(code))
                return TrangCapQuyen(false, "Zalo không trả về mã cấp quyền.");

            var zalo = adapters.OfType<Services.Chat.Channels.ZaloChatAdapter>().FirstOrDefault();
            if (zalo is null) return TrangCapQuyen(false, "Kênh Zalo chưa được bật ở máy chủ.");

            var loi = await zalo.DoiMaCapQuyenAsync(cho.Value.TenantId, cho.Value.AccountId, code!,
                cho.Value.RedirectUri, ct);
            return loi is null
                ? TrangCapQuyen(true, "Đã lưu Refresh Token cho tài khoản Zalo OA. Từ giờ hệ thống tự làm mới, bạn không phải làm lại.")
                : TrangCapQuyen(false, loi);
        });

        // ── Khai kết nối kênh ───────────────────────────────────────────────
        // Cần quyền Cấu hình hệ thống: đây là khoá cấp CÔNG TY, ai cầm được là nhắn tin dưới danh
        // nghĩa công ty.
        //
        // MỘT công ty nối được NHIỀU tài khoản mỗi kênh (nhiều Trang Facebook cho các chi nhánh,
        // nhiều OA Zalo, nhiều bot Telegram cho từng đội sale).
        g.MapGet("/channels", async (HttpContext ctx, TkSessionStore sessions,
            ChannelCredentialStore cred, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();

            var goc = GocCongKhai(ctx, cfg);
            var ra = new List<object>();
            foreach (var (kenh, ten, oNhap, moiTaiKhoanMotUrl) in KhaiBao)
            {
                var dsach = await cred.ListAccountsAsync(a.TenantId, kenh, ct);
                var duong = $"{goc}/api/v1/chat/webhook/{kenh.ToString().ToLowerInvariant()}/{a.TenantId}";
                ra.Add(new
                {
                    channel = (short)kenh, name = ten, fields = oNhap,
                    // Telegram: mỗi bot một URL riêng (thân tin không nói bot nào) → URL chung để
                    // trống, giao diện hiện URL riêng ở từng tài khoản. Zalo/Messenger dùng chung.
                    webhookUrl = moiTaiKhoanMotUrl ? null : duong,
                    accounts = dsach.Select(t => new
                    {
                        accountId = t.AccountId,
                        label = t.GiaTri.GetValueOrDefault("label", ""),
                        // Tên OA THẬT do Zalo trả về sau khi cấp quyền — khác "Tên gợi nhớ" người
                        // dùng tự đặt. Khai nhiều OA mà không có cái này thì không phân biệt nổi.
                        oaName = t.GiaTri.GetValueOrDefault("oaName", ""),
                        oaId = t.GiaTri.GetValueOrDefault("oaId", ""),
                        configured = DaKhaiDu(kenh, t.GiaTri),
                        webhookUrl = moiTaiKhoanMotUrl ? $"{duong}/{t.AccountId}" : duong,
                        // Giá trị điền sẵn khi sửa — CHỈ trường không phải bí mật. Bí mật thì
                        // tuyệt đối không trả ra: giao diện để trống, gửi rỗng = giữ nguyên.
                        values = oNhap.Where(f => f.Type == "text")
                                      .ToDictionary(f => f.Key, f => t.GiaTri.GetValueOrDefault(f.Key, "")),
                    }),
                });
            }
            return Results.Json(new { items = ra }, Web);
        });

        // Tạo tài khoản mới cho một kênh. Mã tài khoản do MÁY CHỦ sinh, không nhận từ client —
        // nó nằm trên URL webhook công khai nên phải chắc chắn không trùng và không có ký tự lạ.
        g.MapPost("/channels/{channel:int}/accounts", async (int channel, Dictionary<string, string?> body,
            HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            var accountId = Guid.NewGuid().ToString("N")[..8];
            await cred.SaveAsync(a.TenantId, (ChatChannel)channel, accountId, body, ct);
            return Results.Json(new { ok = true, accountId }, Web);
        });

        g.MapPut("/channels/{channel:int}/accounts/{accountId}", async (int channel, string accountId,
            Dictionary<string, string?> body, HttpContext ctx, TkSessionStore sessions,
            ChannelCredentialStore cred, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            await cred.SaveAsync(a.TenantId, (ChatChannel)channel, accountId, body, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapDelete("/channels/{channel:int}/accounts/{accountId}", async (int channel, string accountId,
            HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred, ChatRepository repo,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            // CỐ Ý không xoá hội thoại cũ của tài khoản này: lịch sử chat với khách là dữ liệu
            // nghiệp vụ, gỡ kết nối chỉ nghĩa là "thôi không nhận/gửi qua tài khoản này nữa".
            var xoa = await cred.DeleteAsync(a.TenantId, (ChatChannel)channel, accountId, ct);
            // Không gắn với hội thoại nào — gỡ kết nối là việc ở mức tài khoản kênh.
            await repo.GhiNhatKyAsync(a.TenantId, null, a.Username, "go-ket-noi",
                new JsonObject { ["kenh"] = channel, ["taiKhoan"] = accountId }.ToJsonString(), ct);
            return Results.Json(new { ok = true, removed = xoa }, Web);
        });

        // ── Mẫu trả lời nhanh ───────────────────────────────────────────────
        // ĐỌC thì mọi nhân viên trực chat đều cần; SỬA/XOÁ thì cần quyền cấu hình hệ thống —
        // đây là bộ câu dùng chung cả đội, một người sửa là cả đội đổi theo.
        g.MapGet("/quick-replies", async (HttpContext ctx, TkSessionStore sessions,
            ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            return Results.Json(new { items = await repo.ListAsync(a.TenantId, ct) }, Web);
        });

        g.MapPut("/quick-replies", async (QuickReplyReq body, HttpContext ctx,
            TkSessionStore sessions, ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return ChuaCauHinh();
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.BadRequest(new { error = "Chưa nhập nội dung mẫu" });
            try
            {
                var id = await repo.UpsertAsync(a.TenantId, body.Trigger, body.Body.Trim(), ct);
                return Results.Json(new { ok = true, id }, Web);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapDelete("/quick-replies/{id:long}", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return ChuaCauHinh();
            return Results.Json(new { ok = true, removed = await repo.DeleteAsync(a.TenantId, id, ct) }, Web);
        });
    }

    /// <summary>
    /// Địa chỉ CÔNG KHAI của bản chạy này — dùng cho cả URL webhook lẫn callback cấp quyền.
    ///
    /// <para>Mặc định lấy theo địa chỉ của chính yêu cầu đang tới. Nhưng lúc dev thì đó là
    /// <c>localhost</c>, mà Zalo/Meta <b>không gọi vào localhost được</b> — nên cho phép đè bằng
    /// <c>Chat:PublicBaseUrl</c> (dán URL đường hầm ngrok vào đó).</para>
    /// </summary>
    private static string GocCongKhai(HttpContext ctx, IConfiguration cfg)
    {
        var dat = cfg["Chat:PublicBaseUrl"];
        return string.IsNullOrWhiteSpace(dat)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : dat.TrimEnd('/');
    }

    private const string DuongCallbackZalo = "/api/v1/chat/oauth/zalo/callback";

    /// <summary>
    /// Trang nhỏ trả về cho cửa sổ cấp quyền. Tự đóng khi xong; hỏng thì để nguyên cho người dùng
    /// đọc lý do — đóng phụt mất câu lỗi là kiểu tệ nhất, họ chỉ thấy "không có gì xảy ra".
    /// </summary>
    private static IResult TrangCapQuyen(bool xong, string thongDiep)
    {
        var mau = xong ? "#16A34A" : "#DC2626";
        var tuDong = xong
            ? "<script>setTimeout(function(){window.close()},1500)</script>"
            : "";
        var html = $"""
            <!doctype html><html lang="vi"><head><meta charset="utf-8">
            <title>Cấp quyền Zalo OA</title></head>
            <body style="font-family:system-ui,sans-serif;padding:32px;line-height:1.6">
            <h2 style="color:{mau};margin:0 0 8px">{(xong ? "Đã cấp quyền" : "Cấp quyền không xong")}</h2>
            <p>{System.Net.WebUtility.HtmlEncode(thongDiep)}</p>
            <p style="color:#64748B">{(xong ? "Cửa sổ này tự đóng." : "Đóng cửa sổ này rồi thử lại.")}</p>
            {tuDong}</body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// Đã khai đủ khoá để tài khoản này chạy được chưa.
    private static bool DaKhaiDu(ChatChannel kenh, IReadOnlyDictionary<string, string> g) => kenh switch
    {
        ChatChannel.Zalo => g.ContainsKey("appId") && g.ContainsKey("secretKey") && g.ContainsKey("refreshToken"),
        ChatChannel.Messenger => g.ContainsKey("pageId") && g.ContainsKey("pageAccessToken")
                                 && g.ContainsKey("appSecret") && g.ContainsKey("verifyToken"),
        ChatChannel.Telegram => g.ContainsKey("botToken") && g.ContainsKey("webhookSecret"),
        _ => false,
    };

    private static IResult ChuaCauHinh()
        => Results.Json(new { error = "Chưa khai cơ sở dữ liệu chat (ConnectionStrings:Chat)" }, statusCode: 503);

    /// <summary>
    /// Ô cần nhập cho từng kênh. MỘT nguồn — giao diện đọc để tự vẽ form, thêm kênh không phải
    /// sửa giao diện.
    /// </summary>
    /// <remarks><c>MoiTaiKhoanMotUrl</c>: Telegram cần URL riêng cho từng bot (thân tin không cho
    /// biết bot nào); Zalo/Messenger nhiều tài khoản chung một URL.</remarks>
    /// <param name="Type">"text" (điền sẵn lại được) · "secret" (KHÔNG bao giờ trả ra client) ·
    /// "note" (chỉ là dòng hướng dẫn, không phải ô nhập).</param>
    /// <param name="Hint">Chữ mờ trong ô — cho VÍ DỤ về định dạng, không lặp lại nhãn. Ô token
    /// dài mà không có ví dụ thì người khai không biết mình dán đúng thứ chưa.</param>
    private record ONhap(string Key, string Label, string Type = "text", string Hint = "");

    private static readonly (ChatChannel Kenh, string Ten, ONhap[] O, bool MoiTaiKhoanMotUrl)[] KhaiBao =
    {
        (ChatChannel.Zalo, "Zalo OA", new[]
        {
            new ONhap("label",        "Tên gợi nhớ", "text",   "OA Hà Nội"),
            new ONhap("appId",        "App ID",      "text",   "1234567890123456789"),
            new ONhap("secretKey",    "App Secret Key", "secret", "Ứng dụng → Cài đặt. Dùng để GỬI tin"),
            new ONhap("oaSecretKey",  "OA Secret Key",  "secret", "Sản phẩm → Official Account → Cài đặt chung. Dùng để NHẬN tin"),
            new ONhap("refreshToken", "Refresh Token", "secret", "Để trống — bấm \"Cấp quyền OA\" là tự điền"),
            new ONhap("note",
                "Zalo cấp HAI khoá bí mật khác nhau: App Secret Key để gửi, OA Secret Key để nhận. "
                + "Bỏ trống OA Secret Key thì hệ thống dùng tạm App Secret Key — nếu tin khách không "
                + "vào hộp thư thì gần như chắc chắn là do thiếu ô này.", "note"),
            new ONhap("note2",
                "Đây là OA RIÊNG của chat, độc lập với OA khai cho bản tin sáng ở Tự động hoá.", "note"),
        }, false),
        (ChatChannel.Messenger, "Facebook Messenger", new[]
        {
            new ONhap("label",           "Tên gợi nhớ", "text", "Trang chi nhánh Q1"),
            new ONhap("pageId",          "ID Trang",    "text", "102938475610293"),
            new ONhap("pageAccessToken", "Page Access Token", "secret", "EAAG… (lấy ở Meta for Developers)"),
            new ONhap("appSecret",       "App Secret",  "secret", "Dùng để kiểm chữ ký webhook"),
            new ONhap("verifyToken",     "Verify Token", "secret", "Bạn tự đặt, dán y hệt vào Meta"),
        }, false),
        (ChatChannel.Telegram, "Telegram", new[]
        {
            new ONhap("label",         "Tên gợi nhớ", "text", "Bot đội sale lẻ"),
            new ONhap("botToken",      "Bot token",   "secret", "123456:ABC-DEF… (lấy từ @BotFather)"),
            new ONhap("webhookSecret", "Chuỗi bí mật webhook", "secret", "Bạn tự đặt, khai khi gọi setWebhook"),
        }, true),
    };

    private static object Shape(ChatConversation v)
    {
        // Mốc đọc RIÊNG của người đang xem. Chưa mở lần nào thì lùi về mốc chung cũ — không thì
        // mọi hội thoại cũ bật lại thành "chưa đọc" cho tất cả mọi người ngay sau khi nâng cấp.
        var docToi = v.MyLastReadAt ?? v.AgentLastReadAt;
        return new
        {
            v.Id, v.Channel, v.ContactExternalId, v.AccountId, v.Status, v.AssignedUsername,
            v.LastActivityAt, v.LastPreview, v.ContactRepliedAt,
            displayName = v.DisplayName,
            // Bot có đang bị câm không — giao diện hiện rõ, không thì nhân viên tưởng bot hỏng.
            botPaused = v.BotResumeAt is { } m && m > DateTime.UtcNow,
            // Chưa đọc = khách nhắn sau lần CHÍNH MÌNH mở gần nhất.
            unread = v.ContactRepliedAt is { } cr && (docToi is null || cr > docToi),
        };
    }

    /// <param name="AttachmentKind">"anh" | "tep" — bỏ trống khi không đính kèm.</param>
public record SendReq(string? Text, string? AttachmentUrl = null, string? AttachmentKind = null,
    string? AttachmentName = null, long? AttachmentSize = null);
    public record AssignReq(string? Username);
    /// <param name="CustomerId">Bỏ trống = GỠ nối khách CRM khỏi hội thoại này.</param>
    public record LinkCrmReq(int? CustomerId);
    /// <param name="Tag">Nhãn thô — server tự chuẩn hoá (bỏ dấu, hạ chữ thường, gạch nối).</param>
    public record TagReq(string? Tag);
    public record NoteReq(string? NoiDung);
    public record StatusReq(short Status);
    public record BotReq(bool Paused, int? Minutes);

    /// <param name="Trigger">Lệnh gọi thô — server tự chuẩn hoá (bỏ dấu, hạ chữ thường).</param>
    public record QuickReplyReq(string Trigger, string Body);
}
