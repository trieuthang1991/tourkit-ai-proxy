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
    /// Các tiền tố đường dẫn CHỈ thuộc hộp thư chat — <b>bản kiểm kê bề mặt API của cụm này</b>.
    ///
    /// <para>⚠️ <b>Từ 26/08/2026 danh sách này KHÔNG còn dùng để chặn.</b> Cờ <c>Features:Chat</c> nay
    /// chỉ ẩn mục menu, đường dẫn luôn được map (xem <c>EndpointRegistration.MapHopThuChat</c>).</para>
    ///
    /// <para><b>Vẫn giữ và vẫn có test canh cho đủ</b> vì hai lý do: nó là chỗ duy nhất liệt kê đủ
    /// bề mặt API của hộp thư chat, và nếu sau này làm cờ THEO TỪNG CÔNG TY thì cần đúng danh sách
    /// này. Thêm nhóm endpoint mới thì thêm một dòng ở đây.</para>
    ///
    /// <para><b>Vì sao không gộp thành tiền tố <c>/api/v1/chat</c> trần:</b> <c>POST /api/v1/chat</c>
    /// và <c>/api/v1/chat/stream</c> là <b>Trợ lý số liệu</b> — tính năng KHÁC. Gộp là kể nhầm nó
    /// vào cụm chat, và bất cứ ai dùng lại danh sách này để chặn sẽ giết một tính năng đang chạy.</para>
    /// </summary>
    public static readonly string[] OwnedPaths =
    {
        "/api/v1/chat/conversations",
        "/api/v1/chat/channels",
        "/api/v1/chat/messages",
        "/api/v1/chat/avatars",
        "/api/v1/chat/bot-settings",
        "/api/v1/chat/quick-replies",
        "/api/v1/chat/events",
        "/api/v1/chat/oauth",
        "/api/v1/chat/webhook",
        // Trang tra tiến độ xoá dữ liệu — CÔNG KHAI, khách hàng cuối mở, không đăng nhập.
        "/api/v1/chat/data-deletion",
        "/api/v1/chat/media",
    };

    public static IEndpointRouteBuilder MapChatInboxEndpoints(this IEndpointRouteBuilder routes)
    {
        MapWebhook(routes);
        MapInbox(routes);
        return routes;
    }

    // ── Webhook ─────────────────────────────────────────────────────────────

    /// Tên kênh trên đường dẫn → enum. Một nguồn cho cả ba kênh, thêm kênh chỉ thêm 1 dòng.
    private static readonly Dictionary<string, ChatChannel> ChannelByPath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zalo"] = ChatChannel.Zalo,
        ["messenger"] = ChatChannel.Messenger,
        ["telegram"] = ChatChannel.Telegram,
        ["instagram"] = ChatChannel.Instagram,
        ["whatsapp"] = ChatChannel.WhatsApp,
        ["tiktok"] = ChatChannel.TikTok,
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
            ChatInboundService svc, ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin,
            ILoggerFactory lf, CancellationToken ct)
        {
            var log = lf.CreateLogger("chat.webhook");
            if (!ChannelByPath.TryGetValue(kenh, out var loaiKenh)) return Results.NotFound();
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
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            if (id is null)
                log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, kênh={K} tenant={T} sk={S}",
                    kenh, tenantId, maSuKien);

            return Results.Ok();
        }

        // Đường DÙNG CHUNG cho Zalo: KHÔNG mang tên công ty. Khai một lần trong ứng dụng Zalo của
        // TourKit, mọi khách hàng dùng chung — khách không phải chạm vào cổng developer.
        //
        // Không mang tenant thì lấy đâu ra? Từ id OA trong thân tin, tra ngược ra công ty đã nối
        // OA đó. Vẫn kiểm chữ ký sau khi tra: id OA không phải bí mật, tra được không có nghĩa là
        // tin thật.
        //
        // ⚠️ LUÔN TRẢ 200, kể cả khi từ chối. Zalo nói thẳng: "Webhook của bạn chỉ được thiết lập
        // khi trả về http code 200 OK" — họ gọi thử URL lúc lưu bằng một gói tin RỖNG, không chữ
        // ký, không id OA. Trả 401 ở đó là không bao giờ lưu được URL, tức là cả tính năng chết.
        //
        // Trả 200 KHÔNG nới lỏng gì: từ chối vẫn là KHÔNG GHI GÌ vào hộp thư. Thực ra còn kín hơn
        // một chút — 401/200 khác nhau là một cái máy đoán, cho người ngoài dò xem id OA nào đã có
        // trong hệ thống.
        //
        // Cái giá: hỏng thì hỏng IM LẶNG. Chữ ký sai vì khai nhầm khoá cũng trả 200 y như tin rác,
        // và Zalo coi như đã giao xong. Nên mọi lượt từ chối đều ghi log mức WARNING kèm lý do —
        // đó là chỗ DUY NHẤT nhìn ra "tin có tới mà không vào hộp thư".
        routes.MapPost("/api/v1/chat/webhook/zalo", async (HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (svc.Adapter(ChatChannel.Zalo) is not Services.Chat.Channels.ZaloChatAdapter zalo)
                return Results.NotFound();

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var ai = await zalo.ResolveSharedWebhookAsync(raw, ctx.Request.Headers, ct);
            if (ai is null)
            {
                // Gói rỗng = Zalo đang gọi thử lúc lưu URL. Có id OA mà vẫn trượt = khai nhầm khoá
                // hoặc OA chưa ai nối — phân biệt hai ca này trong log, không thì đọc log không
                // biết đang gặp cái nào.
                var oa = Services.Chat.Channels.ZaloChatAdapter.OaIdOfEvent(raw);
                if (oa is null)
                    log.LogInformation("[chat/webhook] zalo: gói không có id OA (nhiều khả năng là "
                        + "lượt gọi thử lúc lưu URL) — trả 200, không ghi gì");
                else
                    log.LogWarning("[chat/webhook] zalo: TỪ CHỐI tin của OA {Oa} — chữ ký sai hoặc "
                        + "chưa công ty nào nối OA này. Tin KHÔNG vào hộp thư.", oa);
                return Results.Ok();
            }

            var sk = zalo.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            var id = await repo.EnqueueInboundAsync(ai.Value.TenantId, ChatChannel.Zalo,
                ai.Value.AccountId, sk[0].ExternalMsgId, raw, ct);
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            if (id is null)
                log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, zalo tenant={T} sk={S}",
                    ai.Value.TenantId, sk[0].ExternalMsgId);
            return Results.Ok();
        });

        // Zalo có thể gọi thử bằng GET thay vì POST tuỳ lúc. Không có đường GET thì máy chủ trả 405
        // và lượt kiểm cũng trượt — thêm một dòng còn hơn ngồi đoán vì sao không lưu được.
        routes.MapGet("/api/v1/chat/webhook/zalo", () => Results.Ok());

        // Đường DÙNG CHUNG cho Messenger — cùng lý do với Zalo: ứng dụng Facebook là của TourKit,
        // khai một lần, mọi khách hàng dùng chung nên URL không mang tên công ty được.
        //
        // Thực ra Meta BẮT BUỘC như vậy: webhook đăng ký theo ỨNG DỤNG, một địa chỉ duy nhất cho
        // mọi Trang. Tra ngược ra công ty bằng id Trang ở entry[].id, rồi vẫn kiểm chữ ký.
        //
        // ⚠️ Cũng LUÔN TRẢ 200 như Zalo, và vì lý do nặng hơn: Meta tự động NGỪNG gửi webhook cho
        // ứng dụng nào trả lỗi liên tục. Trả 401 cho tin rác là tự tay tắt kênh của mọi khách hàng.
        // ── Meta Data Deletion Callback ─────────────────────────────────────
        //
        // Meta gọi đường này khi một người GỠ ứng dụng khỏi tài khoản Facebook của họ và yêu cầu
        // xoá dữ liệu. Bắt buộc phải có trước khi ứng dụng lên Live diện Tech Provider — thiếu là
        // một trong những lý do bị trả hồ sơ hay gặp nhất.
        //
        // ⚠️ Đường này CÔNG KHAI, ai cũng POST vào được. Thứ DUY NHẤT chứng minh yêu cầu đến từ
        // Meta là chữ ký trong signed_request. Bỏ qua bước kiểm đó là dựng sẵn một cửa cho người
        // ngoài xoá dữ liệu khách hàng của mọi công ty.
        //
        // Meta đòi trả về đúng hình dạng { url, confirmation_code } — sai là họ báo lỗi cấu hình
        // mà không nói vì sao.
        routes.MapPost("/api/v1/chat/webhook/meta/data-deletion",
            async (HttpContext ctx, ChatRepository repo, IConfiguration cfg,
                   ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (!repo.Configured) return NotConfigured();

            var form = await ctx.Request.ReadFormAsync(ct);
            var goi = MetaSignedRequest.Parse(form["signed_request"], cfg["Chat:Messenger:AppSecret"]);
            if (goi is null)
            {
                log.LogWarning("[chat/xoa-du-lieu] signed_request không hợp lệ — từ chối");
                return Results.BadRequest(new { error = "signed_request không hợp lệ" });
            }

            // Xoá cho CẢ Messenger lẫn Instagram: hai kênh dùng chung một ứng dụng Meta, và mã
            // người dùng Meta gửi sang không nói rõ của kênh nào. Xoá thiếu một kênh là lời hứa
            // "đã xoá" thành lời nói dối.
            var tong = new ChatRepository.KetQuaXoa(0, 0);
            foreach (var kenh in new[] { ChatChannel.Messenger, ChatChannel.Instagram })
            {
                var kq = await repo.DeleteContactDataAsync(kenh, goi.UserId, ct);
                tong = new(tong.SoHoiThoai + kq.SoHoiThoai, tong.SoTin + kq.SoTin);
            }

            var ma = MetaSignedRequest.NewConfirmationCode();
            await repo.RecordDeletionAsync(ma, ChatChannel.Messenger, goi.UserId, tong, ct);
            log.LogInformation("[chat/xoa-du-lieu] xong {Ma}: {H} hội thoại, {T} tin",
                ma, tong.SoHoiThoai, tong.SoTin);

            var goc = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new
            {
                url = $"{goc}/api/v1/chat/data-deletion/{ma}",
                confirmation_code = ma,
            }, Web);
        });

        // Trang tra tiến độ — CÔNG KHAI, không đăng nhập: người tra là khách hàng cuối, họ không
        // có tài khoản trong hệ. Trả HTML chứ không JSON vì Meta mở nó trong trình duyệt.
        routes.MapGet("/api/v1/chat/data-deletion/{code}",
            async (string code, ChatRepository repo, CancellationToken ct) =>
        {
            if (!repo.Configured) return NotConfigured();
            var yc = await repo.GetDeletionAsync(code, ct);

            var than = yc is null
                ? "<h2>Không tìm thấy yêu cầu</h2><p>Mã tra cứu không đúng hoặc đã quá hạn lưu.</p>"
                : yc.DoneUtc is null
                    ? "<h2>Đang xử lý</h2><p>Yêu cầu đã nhận, đang chờ xoá.</p>"
                    : $"<h2 style=\"color:#16A34A\">Đã xoá xong</h2>"
                      + $"<p>Hoàn tất lúc {yc.DoneUtc:yyyy-MM-dd HH:mm} UTC.</p>"
                      + $"<p>Đã xoá {yc.SoHoiThoai} hội thoại và {yc.SoTin} tin nhắn, cùng tên, "
                      + "ảnh đại diện, nhãn và ghi chú liên quan.</p>";

            return Results.Content($"""
                <!doctype html><html lang="vi"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Tra cứu yêu cầu xoá dữ liệu</title></head>
                <body style="font-family:system-ui,sans-serif;max-width:640px;margin:0 auto;padding:40px 20px;line-height:1.7">
                {than}
                <p style="color:#64748B;font-size:14px">Mã tra cứu: <code>{System.Net.WebUtility.HtmlEncode(code)}</code></p>
                <p style="color:#64748B;font-size:14px">Cần hỗ trợ thêm: <a href="mailto:hotro@tourkit.vn">hotro@tourkit.vn</a></p>
                </body></html>
                """, "text/html; charset=utf-8");
        });

        routes.MapPost("/api/v1/chat/webhook/messenger", async (HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (svc.Adapter(ChatChannel.Messenger) is not Services.Chat.Channels.MessengerChatAdapter fb)
                return Results.NotFound();

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var ai = await fb.ResolveSharedWebhookAsync(raw, ctx.Request.Headers, ct);
            if (ai is null)
            {
                var trang = Services.Chat.Channels.MessengerChatAdapter.PageIdOfEvent(raw);
                if (trang is null)
                    log.LogInformation("[chat/webhook] messenger: gói không có id Trang — trả 200, không ghi gì");
                else
                    log.LogWarning("[chat/webhook] messenger: TỪ CHỐI tin của Trang {P} — chữ ký sai "
                        + "hoặc chưa công ty nào nối Trang này. Tin KHÔNG vào hộp thư.", trang);
                return Results.Ok();
            }

            var sk = fb.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            var id = await repo.EnqueueInboundAsync(ai.Value.TenantId, ChatChannel.Messenger,
                ai.Value.AccountId, sk[0].ExternalMsgId, raw, ct);
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            if (id is null)
                log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, messenger tenant={T} sk={S}",
                    ai.Value.TenantId, sk[0].ExternalMsgId);
            return Results.Ok();
        });

        // Meta xác minh địa chỉ bằng một lượt GET kèm hub.challenge. Ở đường dùng chung không có
        // tên công ty nên chỉ đối chiếu được với verify token CẤP NỀN TẢNG.
        routes.MapGet("/api/v1/chat/webhook/messenger", async (HttpContext ctx,
            Services.Chat.Channels.MessengerChatAdapter adapter, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var challenge = await adapter.VerifySubscriptionAsync("", q["hub.mode"], q["hub.verify_token"],
                q["hub.challenge"], ct);
            // Trả chuỗi THÔ, không bọc JSON — Meta so khớp nguyên văn.
            // KHÔNG dùng Results.Forbid(): ứng dụng không đăng ký dịch vụ xác thực nào nên nó ném
            // InvalidOperationException, và Meta nhận về 500 thay vì 403 — báo lỗi sai chỗ, mất công
            // đi tìm ở đầu Meta trong khi lỗi nằm ở đây.
            return challenge is null ? Results.StatusCode(403) : Results.Text(challenge);
        });

        // ── Instagram: đường DÙNG CHUNG ──────────────────────────────────────
        //
        // Instagram đi qua CHÍNH ứng dụng Meta của Messenger, nhưng Meta khai địa chỉ webhook
        // RIÊNG cho từng "đối tượng" (page · instagram) — nên phải có đường riêng ở đây, dù cùng
        // một ứng dụng. Gộp vào đường của Messenger là Instagram không có chỗ để gửi tới.
        //
        // ⚠️ LUÔN TRẢ 200, cùng lý do với Messenger và nặng hơn: ứng dụng dùng chung, trả lỗi
        // liên tục là Meta tự ngừng gửi cho MỌI khách hàng cùng lúc. Từ chối = không ghi gì, và
        // mỗi lượt từ chối ghi log WARNING — đó là chỗ duy nhất nhìn ra "tin có tới mà không vào
        // hộp thư".
        routes.MapPost("/api/v1/chat/webhook/instagram", async (HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (svc.Adapter(ChatChannel.Instagram) is not Services.Chat.Channels.InstagramChatAdapter ig)
                return Results.NotFound();

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var ai = await ig.ResolveSharedWebhookAsync(raw, ctx.Request.Headers, ct);
            if (ai is null)
            {
                log.LogWarning("[chat/webhook] instagram: TỪ CHỐI tin của tài khoản {Ig} — chữ ký sai "
                    + "hoặc chưa công ty nào nối. Tin KHÔNG vào hộp thư.",
                    Services.Chat.Channels.InstagramChatAdapter.AccountIdOfEvent(raw));
                return Results.Ok();
            }

            var sk = ig.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            var id = await repo.EnqueueInboundAsync(ai.Value.TenantId, ChatChannel.Instagram,
                ai.Value.AccountId, sk[0].ExternalMsgId, raw, ct);
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            else log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, instagram tenant={T} sk={S}",
                ai.Value.TenantId, sk[0].ExternalMsgId);
            return Results.Ok();
        });

        // Meta xác minh địa chỉ bằng một lượt GET kèm hub.challenge — dùng lại verify token cấp
        // nền tảng của Messenger, vì Instagram nằm trong CHÍNH ứng dụng đó.
        routes.MapGet("/api/v1/chat/webhook/instagram", async (HttpContext ctx,
            Services.Chat.Channels.MessengerChatAdapter adapter, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var challenge = await adapter.VerifySubscriptionAsync("", q["hub.mode"], q["hub.verify_token"],
                q["hub.challenge"], ct);
            return challenge is null ? Results.StatusCode(403) : Results.Text(challenge);
        });

        // ── WhatsApp: đường DÙNG CHUNG ───────────────────────────────────────
        //
        // Cùng lý do với Messenger/Instagram: webhook đăng ký theo ỨNG DỤNG. Định tuyến bằng
        // phone_number_id trong thân tin. LUÔN trả 200 kể cả khi từ chối.
        routes.MapPost("/api/v1/chat/webhook/whatsapp", async (HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (svc.Adapter(ChatChannel.WhatsApp) is not Services.Chat.Channels.WhatsAppChatAdapter wa)
                return Results.NotFound();

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var ai = await wa.ResolveSharedWebhookAsync(raw, ctx.Request.Headers, ct);
            if (ai is null)
            {
                var soId = Services.Chat.Channels.WhatsAppChatAdapter.PhoneNumberIdOfEvent(raw);
                log.LogWarning("[chat/webhook] whatsapp: TỪ CHỐI tin của số {So} — chữ ký sai hoặc "
                    + "chưa công ty nào nối. Tin KHÔNG vào hộp thư.", soId);
                return Results.Ok();
            }

            var sk = wa.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            var id = await repo.EnqueueInboundAsync(ai.Value.TenantId, ChatChannel.WhatsApp,
                ai.Value.AccountId, sk[0].ExternalMsgId ?? sk[0].Watermark?.ExternalMsgId, raw, ct);
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            else log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, whatsapp tenant={T}",
                ai.Value.TenantId);
            return Results.Ok();
        });

        // Meta xác minh địa chỉ bằng hub.challenge — dùng chung verify token cấp nền tảng.
        routes.MapGet("/api/v1/chat/webhook/whatsapp", async (HttpContext ctx,
            Services.Chat.Channels.MessengerChatAdapter adapter, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var challenge = await adapter.VerifySubscriptionAsync("", q["hub.mode"], q["hub.verify_token"],
                q["hub.challenge"], ct);
            return challenge is null ? Results.StatusCode(403) : Results.Text(challenge);
        });

        // ── TikTok: đường DÙNG CHUNG ─────────────────────────────────────────
        //
        // Định tuyến bằng user_openid trong thân tin. ⚠️ Chữ ký TikTok CÓ HẠN 5 GIÂY, nên đường
        // này phải nhẹ: đọc thân, tra công ty, kiểm chữ ký, ghi hàng đợi, trả 200.
        routes.MapPost("/api/v1/chat/webhook/tiktok", async (HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.webhook");
            if (svc.Adapter(ChatChannel.TikTok) is not Services.Chat.Channels.TikTokChatAdapter tt)
                return Results.NotFound();

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ct);
            ctx.Request.Body.Position = 0;

            var ai = await tt.ResolveSharedWebhookAsync(raw, ctx.Request.Headers, ct);
            if (ai is null)
            {
                var openId = Services.Chat.Channels.TikTokChatAdapter.OpenIdOfEvent(raw);
                log.LogWarning("[chat/webhook] tiktok: TỪ CHỐI tin của {Ig} — chữ ký sai, quá hạn 5 giây, "
                    + "hoặc chưa công ty nào nối. Tin KHÔNG vào hộp thư.", openId);
                return Results.Ok();
            }

            var sk = tt.Parse(raw);
            if (sk.Count == 0) return Results.Ok();

            var id = await repo.EnqueueInboundAsync(ai.Value.TenantId, ChatChannel.TikTok,
                ai.Value.AccountId, sk[0].ExternalMsgId, raw, ct);
            if (id is not null) tin.Signal(Services.Chat.Inbox.ChatLane.In);
            else log.LogInformation("[chat/webhook] bỏ qua bản gửi lại, tiktok tenant={T}",
                ai.Value.TenantId);
            return Results.Ok();
        });

        // Đường CŨ, mang tên công ty: giữ nguyên cho các OA đã khai theo ứng dụng riêng. Bỏ đi là
        // webhook đang chạy của họ chết ngay lúc deploy.
        routes.MapPost("/api/v1/chat/webhook/{kenh}/{tenantId}", (
            string kenh, string tenantId, HttpContext ctx, ChatInboundService svc, ChatRepository repo,
            Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf, CancellationToken ct)
            => XuLy(kenh, tenantId, null, ctx, svc, repo, tin, lf, ct));

        routes.MapPost("/api/v1/chat/webhook/{kenh}/{tenantId}/{accountId}", (
            string kenh, string tenantId, string accountId, HttpContext ctx, ChatInboundService svc,
            ChatRepository repo, Services.Chat.Inbox.ChatWorkSignal tin, ILoggerFactory lf, CancellationToken ct)
            => XuLy(kenh, tenantId, accountId, ctx, svc, repo, tin, lf, ct));

        // Meta xác minh địa chỉ webhook bằng một lượt GET riêng trước khi bắt đầu gửi tin. Thiếu
        // đường này thì không đăng ký được webhook Messenger, dù phần nhận tin đã đúng hết.
        routes.MapGet("/api/v1/chat/webhook/messenger/{tenantId}", async (
            string tenantId, HttpContext ctx, MessengerChatAdapter adapter, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var challenge = await adapter.VerifySubscriptionAsync(tenantId,
                q["hub.mode"], q["hub.verify_token"], q["hub.challenge"], ct);
            // Trả chuỗi THÔ, không bọc JSON — Meta so khớp nguyên văn.
            // KHÔNG dùng Results.Forbid(): ứng dụng không đăng ký dịch vụ xác thực nào nên nó ném
            // InvalidOperationException, và Meta nhận về 500 thay vì 403 — báo lỗi sai chỗ, mất công
            // đi tìm ở đầu Meta trong khi lỗi nằm ở đây.
            return challenge is null ? Results.StatusCode(403) : Results.Text(challenge);
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
            await using var nguon = bus.SubscribeAsync(a.TenantId, ct).GetAsyncEnumerator(ct);
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
            short? status, string? search, short? channel, bool? unread, bool? followed, bool? mine,
            string? cursor, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();

            // Không có quyền xem toàn công ty → chỉ thấy phần của mình + phần chưa ai nhận.
            // Kẹp ở SQL, không lọc phía client.
            var xemHet = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            var chiCuaToi = xemHet ? null : a.Username;

            // Mã hỏng → Decode() trả null → coi như trang đầu. Không ném: con trỏ nằm trên URL,
            // người dùng sửa tay được và mã cũ từ bản trước còn trong lịch sử trình duyệt.
            const int soDong = 60;
            var items = await repo.ListConversationsAsync(a.TenantId, status, chiCuaToi, search,
                kenh: channel, giaoCho: mine == true ? a.Username : null, chiChuaDoc: unread == true,
                chiTheoDoi: followed == true,
                sau: ChatCursor.Decode(cursor), limit: soDong, nguoiDung: a.Username, ct: ct);
            // Truyền kênh đang lọc: chip trạng thái phải nói về ĐÚNG danh sách đang hiện bên dưới.
            var dem = await repo.CountAsync(a.TenantId, chiCuaToi, a.Username, channel, ct);
            return Results.Json(new
            {
                items = items.Select(x => Shape(x, a.SessionId)),
                counts = new
                {
                    moi = dem.TheoTrangThai.GetValueOrDefault((short)0),
                    dangXuLy = dem.TheoTrangThai.GetValueOrDefault((short)1),
                    daDong = dem.TheoTrangThai.GetValueOrDefault((short)2),
                    chuaDoc = dem.Unread,
                    tong = dem.Tong,
                },
                // Dải kênh bên trái: kênh nào có bao nhiêu hội thoại. Khoá là số của ChatChannel.
                channelCounts = dem.TheoKenh.ToDictionary(k => k.Key.ToString(), k => k.Value),
                xemToanCongTy = xemHet,
                // Ít hơn số dòng xin = hết dữ liệu → null để giao diện biết dừng.
                // Luôn trả mã thì giao diện cuộn mãi không hết.
                nextCursor = items.Count < soDong ? null
                           : ChatCursor.Encode(new(items[^1].LastActivityAt, items[^1].Id)),
            }, Web);
        });

        g.MapGet("/conversations/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions,
            ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            var goc = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            var v = await repo.GetConversationAsync(a.TenantId, id, ct, nguoiDung: a.Username);
            if (v is null) return Results.NotFound();   // id của tenant khác cũng rơi vào đây

            var tin = await repo.ListMessagesAsync(a.TenantId, id, 120, ct);
            // Cảm xúc lấy MỘT lượt cho cả hội thoại rồi gộp trong bộ nhớ — hỏi theo từng tin là 120
            // lượt truy vấn cho một lần mở hội thoại.
            var camXuc = (await repo.ReactionsByConversationAsync(a.TenantId, id, ct))
                .GroupBy(x => x.ExternalMsgId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var lienHe = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            // ChatSender.Agent: hai đường này đều là NGƯỜI THẬT đang mở hộp thư và gõ. Messenger
            // với Instagram cho nhân viên nhắn tới 7 ngày (nhãn HUMAN_AGENT) trong khi bot chỉ có
            // 24 giờ — bỏ tham số này là ô soạn khoá sớm 6 ngày dù nền tảng vẫn cho gửi.
            var cuaSo = ChatRules.ComputeSendWindow((ChatChannel)v.Channel, v.ContactRepliedAt,
                DateTime.UtcNow, ChatSender.Agent);
            return Results.Json(new
            {
                conversation = Shape(v, a.SessionId),
                // Hồ sơ khách cho panel bên phải. Chỉ những gì kênh thật sự cho biết — chưa nối CRM
                // nên crmCustomerId còn trống, giao diện nói thẳng điều đó thay vì bịa một thẻ khách.
                contact = lienHe is null ? null : new
                {
                    lienHe.DisplayName, lienHe.Phone, lienHe.Email,
                    AvatarUrl = ContactAvatarUrl(lienHe.AvatarUrl, a.SessionId),
                    lienHe.CrmCustomerId, lienHe.CreatedUtc,
                },
                messages = tin.Select(m => new
                {
                    m.Id, m.Direction, m.SenderKind, m.SenderUsername, m.Kind,
                    m.Body, m.State, m.ErrorMessage, m.CreatedUtc,
                    // Tin đã xoá khỏi hộp thư. Vẫn gửi ra để giao diện hiện "đã bị xoá" thay
                    // cho nội dung — biến mất hẳn thì người trực tưởng mình nhớ nhầm.
                    deleted = m.DeletedUtc is not null,
                    // Mốc CÓ THẨM QUYỀN cho đồng hồ đếm ngược nút Thu hồi. Xem ChatMessage.SendAfterUtc.
                    sendAfterUtc = m.SendAfterUtc,
                    // Nút ĐÃ GỬI kèm tin. Đọc lại qua ChatRules chứ không đổ thẳng chuỗi JSON
                    // trong CSDL ra: dòng cũ có thể sai hình dạng, và đường dẫn phải lọc lại
                    // http(s) — nút do người dùng tự đặt nên là dữ liệu không tin được.
                    buttons = ChatRules.ReadButtons(m.Buttons)
                        .Select(b => new { chu = b.Label, url = b.Url }),
                    // Đính kèm đã CHUẨN HOÁ về cùng một hình dạng cho cả ba kênh — xem
                    // ChatAttachment. Giao diện không cần biết Zalo/Messenger/Telegram gói tệp
                    // khác nhau thế nào.
                    // Cảm xúc khách thả lên chính tin này. Gộp theo biểu tượng: ba người cùng thả
                    // tim thì giao diện hiện "❤️ 3", không phải ba cái tim rời.
                    reactions = m.ExternalMsgId is { Length: > 0 } maTin && camXuc.TryGetValue(maTin, out var ds)
                        ? ds.GroupBy(x => x.Emoji ?? x.ReactionName ?? "?")
                            .Select(g => new { emoji = g.Key, count = g.Count() })
                        : null,
                    files = ChatAttachment.Read((ChatChannel)v.Channel, (ChatKind)m.Kind, m.Attachment,
                        m.Direction).Select(f => new
                    {
                        f.Name, f.Size, f.Lat, f.Lon,
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
                    // Đang ở cửa "người thật trả lời muộn": vẫn gửi được, nhưng TRỢ LÝ thì không.
                    // Giao diện cần nói ra, không thì nhân viên tưởng bot vẫn đang trực hộ.
                    lateHumanReply = cuaSo.Tag == MetaSendTag.HumanAgent,
                },
            }, Web);
        });

        // ── Mẫu tin đã duyệt: đường DUY NHẤT nhắn khi cửa sổ trả lời tự do đã đóng ──
        //
        // Hết 24 giờ (Meta) hoặc 48 giờ (Zalo) là hộp thư câm hẳn: không gửi được xác nhận đặt
        // tour, không nhắc ngày khởi hành, không báo đổi giờ bay — đúng những việc cần nhất, và
        // đều rơi vào lúc khách đã im lâu.
        g.MapGet("/conversations/{id:long}/templates", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var kenh = (ChatChannel)v.Channel;
            var boNoi = adapters.FirstOrDefault(x => x.Channel == kenh);
            if (boNoi is not Services.Chat.Channels.IApprovedTemplateSender mauGui)
                return Results.Json(new
                {
                    supported = false,
                    // Nói rõ vì sao, đừng chỉ trả danh sách rỗng: Telegram/TikTok không cần mẫu
                    // (không có cửa sổ), còn Instagram thì Meta không cấp mẫu nào cả — ba lý do
                    // khác hẳn nhau mà cùng ra một danh sách rỗng.
                    reason = kenh is ChatChannel.Telegram or ChatChannel.TikTok
                                     or ChatChannel.Webchat
                        ? $"{ChatRules.ChannelName(kenh)} không giới hạn thời gian trả lời nên không cần tin mẫu."
                        : $"{ChatRules.ChannelName(kenh)} không có tin mẫu.",
                    items = Array.Empty<object>(),
                }, Web);

            var khach = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            if (mauGui.WhyBlocked(khach) is { } chan)
                return Results.Json(new
                {
                    supported = true, blocked = true, reason = chan.Reason,
                    items = Array.Empty<object>(),
                }, Web);

            var ds = await mauGui.ListTemplatesAsync(a.TenantId, v.AccountId, ct);
            return Results.Json(new
            {
                supported = true,
                blocked = false,
                items = ds.Select(m => new
                {
                    id = m.Id, name = m.Name, language = m.Language, category = m.Category,
                    status = m.Status, ready = m.SendReady, preview = m.Preview,
                    slots = m.Slots.Select(x => new { key = x.Key, label = x.Label, sample = x.Sample }),
                }),
            }, Web);
        });

        // Gửi một mẫu. KHÔNG đi qua hàng đợi gửi như tin thường: hàng đợi kiểm cửa sổ gửi trước
        // khi gọi API, mà cả điểm của tin mẫu là gửi được KHI cửa sổ đã đóng — qua hàng đợi thì
        // mọi tin mẫu đều bị chính chốt chặn đó loại bỏ.
        g.MapPost("/conversations/{id:long}/send-template", async (long id, SendTemplateReq body,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, ChatEventBus bus,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.TemplateId))
                return Results.BadRequest(new { error = "Chưa chọn mẫu tin" });

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var kenh = (ChatChannel)v.Channel;
            var boNoi = adapters.FirstOrDefault(x => x.Channel == kenh);
            if (boNoi is not Services.Chat.Channels.IApprovedTemplateSender mauGui)
                return Results.BadRequest(new { error = $"{ChatRules.ChannelName(kenh)} không có tin mẫu." });

            var khach = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            if (mauGui.WhyBlocked(khach) is { } chan)
                return Results.BadRequest(new { error = chan.Reason });

            // Đọc lại mẫu từ nền tảng thay vì tin danh sách client gửi lên: client có thể đang
            // cầm bản cũ (mẫu vừa bị gỡ duyệt), và ô điền phải khớp đúng mẫu THẬT thì tham số
            // mới không lệch chỗ.
            var mau = (await mauGui.ListTemplatesAsync(a.TenantId, v.AccountId, ct))
                .FirstOrDefault(x => x.Id == body.TemplateId);
            if (mau is null)
                return Results.BadRequest(new { error = "Mẫu này không còn trong danh sách của kênh." });
            if (!mau.SendReady)
                return Results.BadRequest(new
                    { error = $"Mẫu \"{mau.Name}\" đang ở trạng thái {mau.Status}, chưa gửi được." });

            var giaTri = body.Values ?? new Dictionary<string, string>();
            var thieu = mau.Slots.Where(x => string.IsNullOrWhiteSpace(giaTri.GetValueOrDefault(x.Key)))
                                 .Select(x => x.Label).ToList();
            if (thieu.Count > 0)
                return Results.BadRequest(new { error = "Chưa điền: " + string.Join(", ", thieu) });

            var kq = await mauGui.SendTemplateAsync(a.TenantId, v.AccountId, v.ContactExternalId,
                khach, mau, giaTri, ct);

            // Ghi vào hội thoại DÙ GỬI HỎNG, kèm lý do. Không ghi thì nhân viên bấm gửi, thấy một
            // câu lỗi thoáng qua rồi mất — và không còn dấu vết nào là đã từng thử.
            var noiDung = MoTaMauDaGui(mau, giaTri);
            var msgId = await repo.AppendMessageAsync(a.TenantId, id, kenh, ChatDirection.Out,
                ChatSender.Agent, a.Username, ChatKind.Text, noiDung, null, kq.ExternalMsgId,
                kq.Ok ? ChatState.Sent : ChatState.Failed, ct);

            if (msgId is not null)
            {
                if (!kq.Ok)
                    await repo.SetMessageStateAsync(a.TenantId, msgId.Value, ChatState.Failed, kq.Error, ct);
                await repo.TouchConversationAsync(a.TenantId, id, ChatRules.Summarize(noiDung), false, ct);
                bus.Publish(new(a.TenantId, id, "tin-moi", msgId.Value));
            }

            return kq.Ok
                ? Results.Json(new { ok = true, messageId = msgId }, Web)
                : Results.BadRequest(new { error = kq.Error });
        });

        g.MapPost("/conversations/{id:long}/send", async (long id, SendReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus,
            Services.Chat.Inbox.ChatWorkSignal tin, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();

            // Có đính kèm thì chữ là CHÚ THÍCH, được phép rỗng. Không đính kèm thì bắt buộc có chữ
            // — một tin trống trơn không đính kèm gì là gửi nhầm phím Enter.
            var coDinhKem = !string.IsNullOrWhiteSpace(body.AttachmentUrl);
            if (!coDinhKem && string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Chưa nhập nội dung" });

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // ChatSender.Agent: hai đường này đều là NGƯỜI THẬT đang mở hộp thư và gõ. Messenger
            // với Instagram cho nhân viên nhắn tới 7 ngày (nhãn HUMAN_AGENT) trong khi bot chỉ có
            // 24 giờ — bỏ tham số này là ô soạn khoá sớm 6 ngày dù nền tảng vẫn cho gửi.
            var cuaSo = ChatRules.ComputeSendWindow((ChatChannel)v.Channel, v.ContactRepliedAt,
                DateTime.UtcNow, ChatSender.Agent);
            if (!cuaSo.Open) return Results.BadRequest(new { error = cuaSo.Reason });

            var loai = coDinhKem ? (body.AttachmentKind == "anh" ? ChatKind.Image : ChatKind.File) : ChatKind.Text;
            // Đính kèm ghi theo hình dạng CHUẨN {ten,kich,url} — ChatAttachment.Doc đọc thẳng
            // không cần bóc theo kênh, vì đây là tin MÌNH GỬI (chieu=1), không phải tin kênh gửi tới.
            var attJson = coDinhKem
                ? new JsonObject { ["ten"] = body.AttachmentName, ["kich"] = body.AttachmentSize,
                                   ["url"] = body.AttachmentUrl }.ToJsonString()
                : null;
            var chu = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text.Trim();

            // Nút: cắt cho vừa kênh NGAY Ở ĐÂY để báo lại cho người gửi trong cùng lượt bấm.
            // Worker cũng cắt lần nữa (nó là chốt chặn cuối trước khi gọi API), nhưng lúc đó
            // nhân viên đã rời màn hình rồi — cảnh báo tới chỗ không ai đọc.
            string? nutJson = null;
            string? nutCanhBao = null;
            if (body.Buttons is { Count: > 0 } dsNut)
            {
                var (vua, canhBao) = ChatRules.FitButtons((ChatChannel)v.Channel, dsNut);
                nutCanhBao = canhBao;
                if (vua.Count > 0)
                    nutJson = new JsonArray(vua.Select(x => (JsonNode)new JsonObject
                    {
                        ["chu"] = x.Label,
                        ["url"] = x.Url,
                    }).ToArray()).ToJsonString();
            }

            var msgId = await repo.AppendMessageAsync(a.TenantId, id, (ChatChannel)v.Channel,
                ChatDirection.Out, ChatSender.Agent, a.Username, loai, chu,
                attJson, null, ChatState.Pending, ct, buttonsJson: nutJson);
            if (msgId is null) return Results.Problem("Không ghi được tin");

            var tomTat = coDinhKem ? (loai == ChatKind.Image ? "Đã gửi 1 ảnh" : "Đã gửi 1 tệp") : chu!;
            await repo.TouchConversationAsync(a.TenantId, id, ChatRules.Summarize(tomTat), false, ct);
            // Người thật vừa trả lời → bot câm một lúc, nếu không nó nói đè lên nhân viên.
            await repo.PauseBotAsync(a.TenantId, id, (int)ChatRules.DefaultBotMute.TotalMinutes, ct);
            // Người thật gõ thì giữ lại vài giây cho kịp bấm Thu hồi. Đây là toàn bộ cơ chế thu
            // hồi: Meta không cho doanh nghiệp thu hồi tin đã gửi, nên cách duy nhất để nút đó
            // nói thật là đừng gửi vội. Đặt 0 trong cấu hình là tắt hẳn, gửi ngay như trước.
            // Có hoãn thì worker tỉnh dậy, thấy chưa tới giờ, ngủ lại — nhịp 5 giây sẵn có mới là
            // thứ nhặt tin lên, nên tin thật sự đi trong khoảng hoãn…hoãn+5 giây. Vẫn đánh thức
            // để trường hợp hoãn = 0 (quản trị tắt tính năng) chạy nhanh y như trước.
            var hoan = ChatRules.HoanGuiGiay(ChatSender.Agent,
                cfg.GetValue("Chat:UndoSendSeconds", 5));
            await repo.EnqueueOutboxAsync(a.TenantId, id, msgId.Value, ct, hoan);
            // Đánh thức worker gửi NGAY — thiếu dòng này thì tin chờ hết nhịp 5 giây.
            tin.Signal(Services.Chat.Inbox.ChatLane.Out);
            bus.Publish(new(a.TenantId, id, "tin-moi", msgId.Value));

            // Trả kèm cảnh báo cắt nút: nhân viên soạn năm nút mà kênh chỉ nhận ba thì phải
            // biết ngay, chứ không phải phát hiện lúc khách hỏi lại.
            return Results.Json(new { ok = true, messageId = msgId, buttonWarning = nutCanhBao }, Web);
        });

        // ── Gửi ảnh/tệp: tải lên kho (R2/S3/local theo Storage:Provider) rồi trả URL để FE gọi
        // /send với AttachmentUrl. Tách hai bước (tải lên → gửi) để nhân viên xem trước ảnh trước
        // khi bấm gửi thật, giống mọi app chat khác.
        g.MapPost("/conversations/{id:long}/upload", async (long id, HttpRequest req, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, IChatFileStorage kho, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
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
            TkSessionStore sessions, ChatRepository repo, ChannelCredentialStore cred,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            IHttpClientFactory httpFac, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            // Tin phải thuộc hội thoại của CHÍNH tenant này — chặn ở đây thay vì tin vào id đoán được.
            var hoiThoai = await repo.GetConversationByMessageAsync(a.TenantId, msgId, ct);
            if (hoiThoai is null) return Results.NotFound();

            // ⚠️ Mỗi kênh giấu tệp một kiểu khác nhau, KHÔNG áp một luật:
            //   Telegram — cho mã tệp, đường tải mang bot token trong chính đường dẫn
            //   WhatsApp — cho mã tệp, đường tải đòi KHOÁ XÁC THỰC (gọi trần là 401)
            //   Zalo/Messenger/Instagram — cho URL công khai, không đi qua đây bao giờ
            if ((ChatChannel)hoiThoai.Channel == ChatChannel.WhatsApp)
            {
                var wa = adapters.OfType<Services.Chat.Channels.WhatsAppChatAdapter>().FirstOrDefault();
                if (wa is null || hoiThoai.AccountId is null) return Results.NotFound();
                var tep = await wa.DownloadFileAsync(a.TenantId, hoiThoai.AccountId, fid, ct);
                return tep is null ? Results.NotFound()
                    : Results.File(tep.Value.Bytes, tep.Value.Kieu ?? "application/octet-stream");
            }

            // ⚠️ file_id của Telegram gắn với TỪNG bot: đổi bằng token của bot khác thì họ trả lỗi.
            // Trước 27/08 chỗ này lấy Telegram:BotToken — bot DÙNG CHUNG của bản tin sáng, không
            // phải bot công ty vừa nối. Hậu quả: mọi tệp khách gửi qua Telegram đều hiện "chưa tải
            // được", mà không có lỗi nào để lần ra.
            var token = await TelegramTokenAsync(cred, cfg, a.TenantId, hoiThoai.AccountId, ct);
            if (token is null) return Results.NotFound();

            return await TelegramFileAsync(httpFac, token, fid, ct);
        });

        // Proxy ảnh đại diện Telegram. Cùng lý do với proxy tệp: đường tải thật chứa bot token,
        // nên trình duyệt gọi vào đây chứ không gọi thẳng Telegram.
        //
        // Mã tệp KHÔNG phải bí mật, nhưng vẫn kẹp theo phiên và theo tài khoản của chính công ty:
        // để trần thì ai có đường dẫn cũng biến máy chủ mình thành cửa tải tệp cho bot người khác.
        g.MapGet("/avatars/{accountId}/{fid}", async (string accountId, string fid, HttpContext ctx,
            TkSessionStore sessions, ChannelCredentialStore cred, IHttpClientFactory httpFac,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            var token = await TelegramTokenAsync(cred, cfg, a.TenantId, accountId, ct);
            if (token is null) return Results.NotFound();

            return await TelegramFileAsync(httpFac, token, fid, ct);
        });

        g.MapPost("/conversations/{id:long}/assign", async (long id, AssignReq? body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // KHÔNG có trường username = NHẬN VIỆC cho chính mình. Tên lấy từ PHIÊN, không lấy từ
            // thân yêu cầu: để client tự khai tên là ai cũng gán việc cho người khác được.
            //
            // ⚠️ Bản trước giao diện gửi một thuộc tính KHÔNG tồn tại nên thân yêu cầu luôn là
            // chuỗi rỗng — tức nút "Nhận việc" thật ra đang GỠ giao việc, mà nhìn thì như chạy.
            if (body?.Username is null)
            {
                var soDong = await repo.ClaimConversationAsync(a.TenantId, id, a.Username, ct);
                if (soDong == 0)
                {
                    // 200 im lặng là kiểu hỏng tệ nhất: giao diện người thua vẫn hiện "của tôi",
                    // rồi hai người cùng trả lời một khách.
                    var dangGiu = await repo.AssigneeOfAsync(a.TenantId, id, ct);
                    return Results.Json(new { error = $"{dangGiu} đang xử lý hội thoại này", assignedTo = dangGiu },
                        statusCode: StatusCodes.Status409Conflict);
                }
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "nhan-viec", null, ct);
                bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null));
                return Results.Json(new { ok = true, assignedTo = a.Username }, Web);
            }

            // Chuỗi rỗng = nhả việc (trả về hàng chờ chung); có tên = chuyển việc cho người đó.
            // Cả hai đều CỐ Ý đè lên người đang giữ, nên không đi qua đường nguyên tử ở trên.
            var ai = string.IsNullOrWhiteSpace(body!.Username) ? null : body.Username.Trim();
            await repo.AssignAsync(a.TenantId, id, ai, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, ai is null ? "nha-viec" : "chuyen-viec",
                ai is null ? null : new JsonObject { ["cho"] = ai }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true, assignedTo = ai }, Web);
        });

        g.MapPatch("/conversations/{id:long}/status", async (long id, StatusReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (!Enum.IsDefined(typeof(ChatStatus), body.Status))
                return Results.BadRequest(new { error = "Trạng thái không hợp lệ" });
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetStatusAsync(a.TenantId, id, (ChatStatus)body.Status, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "doi-trang-thai",
                new JsonObject { ["trangThai"] = body.Status }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null));
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
            if (!repo.Configured) return NotConfigured();
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
            if (!repo.Configured) return NotConfigured();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Chuẩn hoá DÙNG CHUNG với lệnh gọi mẫu trả lời nhanh — cùng vấn đề, cùng lời giải.
            // Ghi thô thì "Khách VIP" và "khach vip" thành hai nhãn khác nhau.
            var nhan = ChatRules.NormalizeSlug(body?.Tag);
            if (nhan.Length == 0) return Results.BadRequest(new { error = "Nhãn không hợp lệ" });

            await repo.AddTagAsync(a.TenantId, v.Channel, v.ContactExternalId, nhan, ct);
            return Results.Json(new { ok = true, tag = nhan }, Web);
        });

        g.MapDelete("/conversations/{id:long}/tags/{tag}", async (long id, string tag, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Chuẩn hoá cả lúc XOÁ: nhãn nằm trên đường dẫn nên trình duyệt/người dùng có thể gửi
            // bản có dấu, mà trong CSDL chỉ có bản đã chuẩn hoá — không chuẩn hoá là xoá trượt.
            var xoa = await repo.RemoveTagAsync(a.TenantId, v.Channel, v.ContactExternalId,
                ChatRules.NormalizeSlug(tag), ct);
            return Results.Json(new { ok = true, removed = xoa }, Web);
        });

        g.MapGet("/conversations/{id:long}/notes", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
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
            if (!repo.Configured) return NotConfigured();
            if (string.IsNullOrWhiteSpace(body?.Body))
                return Results.BadRequest(new { error = "Chưa nhập nội dung ghi chú" });
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            var maGhiChu = await repo.AddNoteAsync(a.TenantId, v.Channel, v.ContactExternalId,
                a.Username, body.Body.Trim(), ct);
            return Results.Json(new { ok = true, id = maGhiChu }, Web);
        });

        g.MapDelete("/conversations/{id:long}/notes/{noteId:long}", async (long id, long noteId,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
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
            if (!repo.Configured) return NotConfigured();
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
            if (!repo.Configured) return NotConfigured();

            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is null) return Results.NotFound();

            // Không có customerId = GỠ nối. Gỡ phải làm được: nối nhầm là bot đọc lịch sử mua của
            // người khác rồi nói với khách này, và không có đường lùi thì chỉ còn cách sửa tay CSDL.
            var ma = body?.CustomerId;
            var soDong = await repo.LinkCrmAsync(a.TenantId, v.Channel, v.ContactExternalId, ma, ct);
            if (soDong == 0) return Results.NotFound();

            await repo.AppendAuditAsync(a.TenantId, id, a.Username, ma is null ? "go-noi-crm" : "noi-crm",
                ma is null ? null : new JsonObject { ["khachCrm"] = ma }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true, crmCustomerId = ma }, Web);
        });

        // Nhật ký của một hội thoại. Nằm dưới tiền tố /conversations nên đã được OwnedPaths phủ.
        g.MapGet("/conversations/{id:long}/audit", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            // Hội thoại của tenant khác cũng rơi vào đây — không rò rỉ việc id đó có tồn tại hay không.
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            var ds = await repo.ListAuditAsync(a.TenantId, id, 50, ct);
            return Results.Json(new
            {
                items = ds.Select(x => new
                {
                    x.Id, x.Username, x.Action, x.CreatedUtc,
                    // Trả JSON thô: giao diện tự diễn giải theo hành động, backend không phải biết
                    // cách hiển thị.
                    chiTiet = x.Detail,
                }),
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/read", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatInboundService svc, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            // Theo TỪNG NGƯỜI: đánh dấu chung cho cả công ty thì A mở hội thoại là B mất dấu
            // chưa đọc, và tin của khách trôi qua mắt B mà không có lỗi nào hiện ra.
            await repo.MarkReadAsync(a.TenantId, id, a.Username, ct);

            // Báo sang kênh cho khách biết tin đã được mở. Chỉ ở ĐÂY, nơi có NGƯỜI THẬT bấm vào hội
            // thoại — bot đọc mà cũng báo đã xem là nói dối khách: họ tưởng có nhân viên đang nhìn.
            var v = await repo.GetConversationAsync(a.TenantId, id, ct);
            if (v is not null && svc.Adapter((ChatChannel)v.Channel) is { } boNoi)
                await boNoi.MarkSeenAsync(a.TenantId, v.AccountId, v.ContactExternalId, ct);

            return Results.Json(new { ok = true }, Web);
        });

        // Đánh dấu CHƯA đọc — trả lại hội thoại cho chính mình hoặc cho người khác nhặt.
        //
        // ⚠️ KHÔNG gọi MarkSeenAsync như đường /read: không nền tảng nào cho "bỏ đã xem". Báo
        // sang kênh một lần nữa còn tệ hơn — khách nhận thêm một tín hiệu đã xem cho tin cũ.
        g.MapPost("/conversations/{id:long}/unread", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            var duoc = await repo.MarkUnreadAsync(a.TenantId, id, a.Username, ct);
            if (duoc)
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "danh-dau-chua-doc", null, ct);

            // ok=false nghĩa là hội thoại chưa có tin nào của khách — giao diện nói rõ thay vì im.
            return Results.Json(new { ok = duoc }, Web);
        });

        // THU HỒI — rút tin khỏi hàng đợi trước khi nó rời máy chủ. Đây là thu hồi THẬT, vì
        // tin chưa hề được gửi. Hết cửa sổ hoãn thì KHÔNG có đường nào khác trên Meta: họ
        // không cấp API thu hồi cho phía doanh nghiệp. Trả 409 để giao diện nói thật thay vì
        // báo thành công rồi để nhân viên tưởng đã rút lại được và không đi xin lỗi khách.
        g.MapPost("/conversations/{id:long}/messages/{msgId:long}/recall", async (long id,
            long msgId, HttpContext ctx, TkSessionStore sessions, ChatRepository repo,
            ChatInboundService svc, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is not { } v) return Results.NotFound();

            if (await repo.CancelOutboxAsync(a.TenantId, id, msgId, ct))
            {
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "thu-hoi-tin",
                    new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
                bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
                return Results.Json(new { ok = true, recalledOnChannel = false }, Web);
            }

            // Tin đã rời máy chủ. Kênh nào thu hồi THẬT được thì thử — chỉ Telegram.
            var tinDaGui = (await repo.ListMessagesAsync(a.TenantId, id, 300, ct))
                .FirstOrDefault(m => m.Id == msgId);
            if (tinDaGui?.ExternalMsgId is { Length: > 0 } maNgoai
                && svc.Adapter((ChatChannel)v.Channel) is IMessageRecaller boThuHoi
                && DateTime.UtcNow - tinDaGui.CreatedUtc < boThuHoi.RecallWindow
                && await boThuHoi.RecallAsync(a.TenantId, v.AccountId, v.ContactExternalId,
                                              maNgoai, ct))
            {
                await repo.SoftDeleteMessageAsync(a.TenantId, id, msgId, ct);
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "thu-hoi-tin",
                    new JsonObject { ["tin"] = msgId, ["kenh"] = true }.ToJsonString(), ct);
                bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
                return Results.Json(new { ok = true, recalledOnChannel = true }, Web);
            }

            return Results.Json(new { error = "Tin đã gửi đi mất rồi — không thu hồi được nữa" },
                Web, statusCode: 409);
        });

        // Xoá tin — CHỈ trong hộp thư mình. Xoá MỀM: dòng vẫn nằm đó, chỉ đóng dấu, vì người
        // trực có thể đã đọc và đã hành động theo câu đó.
        g.MapDelete("/conversations/{id:long}/messages/{msgId:long}", async (long id, long msgId,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, ChatEventBus bus,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            if (!await repo.SoftDeleteMessageAsync(a.TenantId, id, msgId, ct)) return Results.NotFound();
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "xoa-tin",
                new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
            return Results.Json(new { ok = true }, Web);
        });

        // Sửa tin — CHỈ tin chưa ra khỏi máy. Xem ChatRules.CoTheSuaTin: sửa một tin đã gửi là
        // làm hộp thư nói dối về thứ khách thật sự nhận được.
        g.MapPatch("/conversations/{id:long}/messages/{msgId:long}", async (long id, long msgId,
            EditMsgReq body, HttpContext ctx, TkSessionStore sessions, ChatRepository repo,
            ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.Json(new { error = "Nội dung không được để trống" }, Web, statusCode: 400);
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            if (!await repo.EditPendingMessageAsync(a.TenantId, id, msgId, body.Body.Trim(), ct))
                return Results.Json(new { error = "Tin đã gửi đi rồi nên không sửa được nữa" },
                    Web, statusCode: 409);

            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "sua-tin",
                new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
            return Results.Json(new { ok = true }, Web);
        });

        // Chặn / bỏ chặn khách. CHỈ trong hộp thư của mình: không nền tảng nào cho phía doanh
        // nghiệp chặn một người qua API. Vì thế giao diện tuyệt đối không được gọi là "báo xấu".
        g.MapPost("/conversations/{id:long}/block", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is not { } v) return Results.NotFound();

            await repo.SetContactBlockedAsync(a.TenantId, (ChatChannel)v.Channel,
                v.ContactExternalId, true, a.Username, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "chan-khach", null, ct);
            return Results.Json(new { ok = true, blocked = true }, Web);
        });

        g.MapDelete("/conversations/{id:long}/block", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is not { } v) return Results.NotFound();

            await repo.SetContactBlockedAsync(a.TenantId, (ChatChannel)v.Channel,
                v.ContactExternalId, false, a.Username, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "bo-chan-khach", null, ct);
            return Results.Json(new { ok = true, blocked = false }, Web);
        });

        // Theo dõi / bỏ theo dõi. KHÔNG đụng assigned_username — theo dõi không phải nhận việc.
        g.MapPost("/conversations/{id:long}/follow", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetFollowAsync(a.TenantId, id, a.Username, true, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "theo-doi", null, ct);
            return Results.Json(new { ok = true, followed = true }, Web);
        });

        g.MapDelete("/conversations/{id:long}/follow", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetFollowAsync(a.TenantId, id, a.Username, false, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "bo-theo-doi", null, ct);
            return Results.Json(new { ok = true, followed = false }, Web);
        });

        // ── Soi lại tệp cũ về kho riêng — CHẠY TAY ──────────────────────────
        //
        // Việc này ĐÃ tự chạy nền cho mọi công ty (ChatMediaBackfillWorker), vì nó là cứu dữ
        // liệu có hạn chót: url ảnh của nhà cung cấp hết hạn sau vài ngày, chờ ai đó bấm thì
        // đến ngày đó không còn gì để cứu.
        //
        // Đường này giữ lại để GỌI NGAY cho một công ty, không phải đợi vòng quét kế tiếp —
        // dùng lúc gấp (sắp tới hạn) hoặc khi cần kiểm chứng ngay sau khi đổi cấu hình kho.
        // Không có nút nào trên giao diện gọi nó, và cố ý vậy.
        //
        // Chạy lại bao nhiêu lần cũng không sao: tin đã soi bị lọc ra ở tầng truy vấn.
        //
        // MỘT LƯỢT là một mẻ, không phải cả hộp thư — gọi lại liên tiếp cho tới khi `examined`
        // về 0. Không phải truyền mốc gì cả: mỗi dòng lấy ra đã được đánh dấu ngay trong CSDL
        // nên lượt sau tự ra mẻ kế. Cắt mẻ vì yêu cầu HTTP có hạn trả lời.
        g.MapPost("/media/backfill", async (int? limit, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatInboundService svc,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();

            // Chặn trên 500: một lượt bấm không được phép chạy hàng giờ rồi chết vì hết hạn
            // yêu cầu — gọi thêm lần nữa là chạy tiếp mẻ kế, vì tiến độ nằm trong CSDL.
            var soToiDa = Math.Clamp(limit ?? 200, 1, 500);
            var tep = await svc.BackfillMediaAsync(a.TenantId, soToiDa, ChatRepository.AnyTier, ct);

            // Ảnh đại diện đi cùng lượt: chúng cũng là url có hạn của nhà cung cấp, và ít hơn
            // hẳn (một dòng mỗi khách) nên một mẻ thường là hết. Bỏ ra ngoài thì lượt chạy tay
            // lúc gấp lại cứu thiếu đúng thứ lộ nhất khi vỡ — khuôn mặt khách ở mọi dòng.
            var anh = await svc.BackfillAvatarsAsync(a.TenantId, soToiDa, ChatRepository.AnyTier, ct);

            return Results.Json(new
            {
                ok = true,
                mirrored = tep.Mirrored, examined = tep.Examined,
                avatarsMirrored = anh.Mirrored, avatarsExamined = anh.Examined,
            }, Web);
        });

        g.MapPost("/conversations/{id:long}/bot", async (long id, BotReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            // paused=false → bỏ câm ngay; true → câm theo số phút (mặc định 30).
            var phut = body.Paused ? Math.Clamp(body.Minutes ?? 30, 1, 1440) : 0;
            await repo.PauseBotAsync(a.TenantId, id, phut, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "tam-dung-bot",
                new JsonObject { ["phut"] = phut }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null));
            return Results.Json(new { ok = true }, Web);
        });

        // ── Cấp quyền Zalo OA ───────────────────────────────────────────────
        //
        // Zalo KHÔNG cho copy Refresh Token từ giao diện — phải đi một vòng OAuth: mở đường cấp
        // quyền → quản trị viên OA bấm đồng ý → Zalo đá về callback kèm `code` sống rất ngắn →
        // đổi `code` lấy token. Làm tay thì phải dán URL, chép `code` trên thanh địa chỉ rồi gọi
        // curl; làm ở đây thì người dùng bấm MỘT nút.
        // Kết nối OA mà KHÔNG cần khai gì trước: dùng ứng dụng Zalo cấp nền tảng, nên chỉ cần biết
        // đây là công ty nào. Tài khoản được TẠO Ở BƯỚC CALLBACK, lấy chính id OA làm mã tài khoản
        // — nhờ vậy webhook dùng chung tra ngược ra công ty chỉ bằng một phép so.
        // ── Lấy lại hội thoại cũ (Messenger / Instagram) ─────────────────────
        //
        // Người dùng TỰ BẤM. Một Trang bán hàng lâu năm có thể có hàng chục nghìn tin, và gọi
        // Graph quá nhiều là Facebook chặn tạm cả ứng dụng — lúc đó tin trực tiếp cũng ngừng về.
        // Đó là quyết định của người dùng, không phải thứ nên âm thầm làm thay họ lúc nối.
        g.MapPost("/channels/{channel:int}/accounts/{accountId}/import-history",
            async (int channel, string accountId, HttpContext ctx, TkSessionStore sessions,
            Services.Chat.Channels.ChatHistoryImportQueue hang,
            Services.Chat.Channels.ChatHistoryJobs viec,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();

            if (!Services.Bootstrap.FeatureFlags.ChatHistoryImport(cfg))
                return Results.BadRequest(new
                {
                    error = "Tính năng lấy lại hội thoại cũ đang tắt (Features:ChatHistoryImport).",
                });

            var kenh = (ChatChannel)channel;
            if (!Services.Chat.Channels.MetaHistoryImporter.Supports(kenh))
                return Results.BadRequest(new
                {
                    error = $"{kenh} không có đường đọc lại hội thoại cũ. Chỉ Facebook và Instagram cho phép.",
                });

            if (!viec.BatDau(a.TenantId, kenh, accountId))
                return Results.Conflict(new { error = "Đang lấy dở cho tài khoản này rồi." });

            // Xếp vào hàng đợi rồi trả lời NGAY: đọc vài trăm hội thoại mất vài phút, quá dài
            // để giữ một kết nối HTTP mở — trình duyệt hoặc proxy sẽ cắt và người dùng thấy
            // "lỗi" trong khi việc vẫn đang chạy tốt.
            //
            // Worker chạy MỘT lượt tại một thời điểm cho cả máy chủ, xem ChatHistoryImportQueue.
            await hang.XepAsync(new(a.TenantId, kenh, accountId), ct);
            return Results.Accepted(value: new { started = true });
        });

        // Tra tiến độ lượt lấy. Giao diện hỏi lại vài giây một lần trong lúc chạy.
        g.MapGet("/channels/{channel:int}/accounts/{accountId}/import-history",
            async (int channel, string accountId, HttpContext ctx, TkSessionStore sessions,
            Services.Chat.Channels.ChatHistoryJobs viec, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();

            var t = viec.Xem(a.TenantId, (ChatChannel)channel, accountId);
            if (t is null) return Results.Json(new { running = false, ever = false }, Web);

            return Results.Json(new
            {
                running = !t.Xong,
                ever = true,
                conversations = t.SoHoiThoai,
                messages = t.SoTin,
                more = t.ConNua,
                error = t.Loi,
            }, Web);
        });

        g.MapPost("/channels/{channel:int}/connect-url", async (int channel, HttpContext ctx,
            TkSessionStore sessions,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            // accountId để TRỐNG ở cả hai nhánh: chưa biết OA/Trang nào cho tới lúc nhà cung cấp
            // trả hồ sơ về. Mã tài khoản sẽ là chính id OA / id Trang.
            if ((ChatChannel)channel == ChatChannel.Zalo)
            {
                var zalo = adapters.OfType<Services.Chat.Channels.ZaloChatAdapter>().FirstOrDefault();
                if (zalo?.HasPlatformApp != true)
                    return Results.BadRequest(new { error = "Máy chủ chưa khai ứng dụng Zalo dùng chung (Chat:Zalo)" });

                var quayVe = PublicOrigin(ctx, cfg) + ZaloCallbackPath;
                var state = moc.Create(a.TenantId, "", quayVe);
                return Results.Json(new
                {
                    url = Services.Chat.Channels.ZaloChatAdapter.PermissionUrlFor(zalo.PlatformAppId!, quayVe, state),
                    redirectUri = quayVe,
                }, Web);
            }

            if ((ChatChannel)channel == ChatChannel.Messenger)
            {
                var fb = adapters.OfType<Services.Chat.Channels.MessengerChatAdapter>().FirstOrDefault();
                if (fb?.HasPlatformApp != true)
                    return Results.BadRequest(new { error = "Máy chủ chưa khai ứng dụng Facebook dùng chung (Chat:Messenger)" });

                var quayVe = PublicOrigin(ctx, cfg) + MessengerCallbackPath;
                var state = moc.Create(a.TenantId, "", quayVe);
                return Results.Json(new { url = fb.PermissionUrlFor(quayVe, state), redirectUri = quayVe }, Web);
            }

            if ((ChatChannel)channel == ChatChannel.WhatsApp)
            {
                var wa = adapters.OfType<Services.Chat.Channels.WhatsAppChatAdapter>().FirstOrDefault();
                if (wa?.HasPlatformApp != true)
                    return Results.BadRequest(new
                    {
                        error = "Máy chủ chưa khai ứng dụng WhatsApp (Chat:WhatsApp — cần cả AppId, "
                              + "AppSecret và ConfigId của luồng Embedded Signup)",
                    });

                var quayVe = PublicOrigin(ctx, cfg) + WhatsAppCallbackPath;
                var state = moc.Create(a.TenantId, "", quayVe);
                return Results.Json(new { url = wa.PermissionUrlFor(quayVe, state), redirectUri = quayVe }, Web);
            }

            if ((ChatChannel)channel == ChatChannel.TikTok)
            {
                var tt = adapters.OfType<Services.Chat.Channels.TikTokChatAdapter>().FirstOrDefault();
                if (tt?.HasPlatformApp != true)
                    return Results.BadRequest(new
                    {
                        error = "Máy chủ chưa khai ứng dụng TikTok (Chat:TikTok — cần cả ClientId và ClientSecret)",
                    });

                var quayVe = PublicOrigin(ctx, cfg) + TikTokCallbackPath;
                var state = moc.Create(a.TenantId, "", quayVe);
                return Results.Json(new { url = tt.PermissionUrlFor(quayVe, state), redirectUri = quayVe }, Web);
            }

            return Results.BadRequest(new { error = "Kênh này không có bước kết nối một chạm" });
        });

        g.MapPost("/channels/{channel:int}/accounts/{accountId}/oauth-url", async (int channel,
            string accountId, HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, IConfiguration cfg, CancellationToken ct) =>
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
            var quayVe = PublicOrigin(ctx, cfg) + ZaloCallbackPath;
            var state = moc.Create(a.TenantId, accountId, quayVe);
            return Results.Json(new
            {
                url = Services.Chat.Channels.ZaloChatAdapter.PermissionUrlFor(appId!, quayVe, state),
                // Trả về để giao diện nhắc dán đúng chuỗi này vào ô Callback URL bên Zalo.
                redirectUri = quayVe,
            }, Web);
        });

        // CÔNG KHAI — Zalo đá trình duyệt về đây bằng chuyển hướng thường, không mang theo
        // X-Session-Id. Ghép lại công ty/tài khoản bằng `state` do máy chủ sinh, dùng một lần.
        g.MapGet("/oauth/zalo/callback", async (string? code, string? state, string? error,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return PermissionPage(false, $"Zalo báo: {error}");

            var cho = moc.Nhan(state);
            if (cho is null)
                return PermissionPage(false, "Lượt cấp quyền đã hết hạn hoặc đã dùng rồi. Bấm lại nút Cấp quyền OA.");
            if (string.IsNullOrWhiteSpace(code))
                return PermissionPage(false, "Zalo không trả về mã cấp quyền.");

            var zalo = adapters.OfType<Services.Chat.Channels.ZaloChatAdapter>().FirstOrDefault();
            if (zalo is null) return PermissionPage(false, "Kênh Zalo chưa được bật ở máy chủ.");

            var loi = await zalo.ExchangePermissionCodeAsync(cho.Value.TenantId, cho.Value.AccountId, code!,
                cho.Value.RedirectUri, ct);
            return loi is null
                ? PermissionPage(true, "Đã lưu Refresh Token cho tài khoản Zalo OA. Từ giờ hệ thống tự làm mới, bạn không phải làm lại.")
                : PermissionPage(false, loi);
        });

        // CÔNG KHAI — Meta đá trình duyệt về đây. Khác Zalo ở chỗ CHƯA nối được ngay: /me/accounts
        // trả về mọi Trang người này quản trị, phải để họ chọn Trang nào là của công ty.
        g.MapGet("/oauth/messenger/callback", async (string? code, string? state, string? error,
            string? error_description, HttpContext ctx, ChannelCredentialStore cred,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, Services.Chat.Channels.MessengerPageChoices chon,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return PermissionPage(false, $"Facebook báo: {error_description ?? error}");

            var cho = moc.Nhan(state);
            if (cho is null)
                return PermissionPage(false, "Lượt kết nối đã hết hạn hoặc đã dùng rồi. Bấm lại nút Kết nối Facebook.");
            if (string.IsNullOrWhiteSpace(code))
                return PermissionPage(false, "Facebook không trả về mã cấp quyền.");

            var fb = adapters.OfType<Services.Chat.Channels.MessengerChatAdapter>().FirstOrDefault();
            if (fb is null) return PermissionPage(false, "Kênh Messenger chưa được bật ở máy chủ.");

            var (trang, loi) = await fb.DoiMaLayTrangAsync(code!, cho.Value.RedirectUri, ct);
            if (loi is not null) return PermissionPage(false, loi);

            var daNoi = await ConnectedIdsAsync(cred, cho.Value.TenantId, ct);

            // ⚠️ Facebook trả về TOÀN Trang đã nối từ trước = người dùng vừa đi hết luồng mà
            // KHÔNG thêm được gì. Phải nói thẳng, đừng báo thành công.
            //
            // Vì sao xảy ra: luồng đăng nhập cổ điển NHỚ lựa chọn Trang của lần trước và bỏ qua
            // bước chọn Trang. auth_type=rerequest không chữa được — nó chỉ hỏi lại QUYỀN bị từ
            // chối, không mở lại phần chọn tài sản.
            //
            // Bản trước rơi thẳng vào nhánh "chỉ một Trang thì nối luôn": nó nối lại đúng Trang
            // cũ rồi hiện chữ xanh "Đã nối Trang X". Người dùng tưởng xong, quay lại hộp thư thấy
            // y nguyên, và không có gì trên màn hình gợi ý phải làm gì tiếp. Hỏng mà trông như chạy
            // là kiểu tệ nhất — đã mất thời gian thật vì nó.
            if (trang!.Count > 0 && trang.All(t => daNoi.Contains(t.PageId)))
                return PermissionPage(false,
                    $"Facebook chỉ trả về Trang đã nối từ trước ({trang[0].Name}), nên không có gì "
                    + "để thêm. Facebook nhớ lựa chọn cũ và bỏ qua bước chọn Trang. "
                    + "Cách thêm Trang khác: ngay trên màn hình đồng ý của Facebook, bấm "
                    + "\"Chỉnh sửa quyền truy cập\" rồi tích thêm Trang. Hoặc vào Facebook → "
                    + "Cài đặt → Ứng dụng và trang web → TourKit AI → Chỉnh sửa.");

            // CHỈ MỘT Trang thì nối luôn, đừng hỏi lại.
            //
            // Facebook đã bắt người dùng đi qua ba bước chọn rồi (tài khoản → doanh nghiệp →
            // Trang). Dựng thêm một màn hình "chọn Trang" nữa cho đúng một lựa chọn duy nhất là
            // bắt họ xác nhận lại thứ vừa xác nhận — người dùng đọc ra là "sao cứ lặp mãi".
            //
            // Từ hai Trang trở lên thì màn hình đó có việc thật: Facebook trả về mọi Trang được
            // cấp, mình vẫn phải để họ nói Trang nào là của công ty này.
            if (trang.Count == 1)
            {
                var loiNoi = await fb.ConnectPageAsync(cho.Value.TenantId, trang[0], ct);
                if (loiNoi is not null) return PermissionPage(false, loiNoi);
                var kemIg = await ConnectLinkedInstagramAsync(adapters, cho.Value.TenantId, trang[0], ct);
                return PermissionPage(true,
                    $"Đã nối Trang \"{trang[0].Name}\"{kemIg}. Tin nhắn mới sẽ vào hộp thư ngay.");
            }

            var ma = chon.Create(cho.Value.TenantId, trang);
            return PagePickerPage(ma, trang, daNoi, null);
        });

        // CÔNG KHAI — Meta gọi lại sau luồng Embedded Signup của WhatsApp.
        //
        // Khác Facebook ở chỗ KHÔNG có màn hình chọn: luồng Embedded Signup chỉ cấp quyền cho
        // ĐÚNG MỘT tài khoản WhatsApp, nên phía máy chủ tự dựng lại được cả tài khoản lẫn số điện
        // thoại từ mã cấp quyền. Người dùng không phải nhập gì.
        g.MapGet("/oauth/whatsapp/callback", async (string? code, string? state, string? error,
            string? error_description, HttpContext ctx,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return PermissionPage(false, $"WhatsApp báo: {error_description ?? error}");

            var cho = moc.Nhan(state);
            if (cho is null)
                return PermissionPage(false,
                    "Lượt kết nối đã hết hạn hoặc đã dùng rồi. Bấm lại nút Kết nối WhatsApp.");
            if (string.IsNullOrWhiteSpace(code))
                return PermissionPage(false, "WhatsApp không trả về mã cấp quyền.");

            var wa = adapters.OfType<Services.Chat.Channels.WhatsAppChatAdapter>().FirstOrDefault();
            if (wa is null) return PermissionPage(false, "Kênh WhatsApp chưa được bật ở máy chủ.");

            var kq = await wa.ConnectFromCodeAsync(cho.Value.TenantId, code!, cho.Value.RedirectUri, ct);
            if (kq.Loi is not null) return PermissionPage(false, kq.Loi);

            var ten = string.IsNullOrWhiteSpace(kq.SoHienThi) ? "WhatsApp" : kq.SoHienThi;
            return PermissionPage(true,
                $"Đã nối số {ten}. Tin nhắn mới sẽ vào hộp thư ngay.");
        });

        // CÔNG KHAI — TikTok gọi lại sau khi người dùng bấm Đồng ý.
        //
        // Cũng không có màn hình chọn: một lượt cấp quyền TikTok gắn với đúng MỘT tài khoản, và
        // mã tài khoản (open_id) chính là thứ dùng để gửi tin — không phải đi tìm thêm mã nào.
        g.MapGet("/oauth/tiktok/callback", async (string? code, string? state, string? error,
            string? error_description, HttpContext ctx,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.ChatOAuthStates moc, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return PermissionPage(false, $"TikTok báo: {error_description ?? error}");

            var cho = moc.Nhan(state);
            if (cho is null)
                return PermissionPage(false,
                    "Lượt kết nối đã hết hạn hoặc đã dùng rồi. Bấm lại nút Kết nối TikTok.");
            if (string.IsNullOrWhiteSpace(code))
                return PermissionPage(false, "TikTok không trả về mã cấp quyền.");

            var tt = adapters.OfType<Services.Chat.Channels.TikTokChatAdapter>().FirstOrDefault();
            if (tt is null) return PermissionPage(false, "Kênh TikTok chưa được bật ở máy chủ.");

            var kq = await tt.ConnectFromCodeAsync(cho.Value.TenantId, code!, cho.Value.RedirectUri, ct);
            if (kq.Loi is not null) return PermissionPage(false, kq.Loi);

            var ten = string.IsNullOrWhiteSpace(kq.Ten) ? "TikTok" : kq.Ten;
            return PermissionPage(true,
                $"Đã nối tài khoản \"{ten}\". Tin nhắn mới sẽ vào hộp thư ngay.");
        });

        // CÔNG KHAI — nửa sau của bước nối: người dùng vừa bấm chọn một Trang trên trang picker.
        //
        // Không có phiên nên chốt chặn nằm ở `ma`: máy chủ tự sinh 32 byte ngẫu nhiên, sống 10
        // phút, và CHỈ nối được Trang nằm trong danh sách đã lưu kèm mã đó. Thiếu vế sau thì ai
        // cầm mã cũng nối được Trang bất kỳ chỉ bằng cách đoán một id.
        g.MapPost("/oauth/messenger/chon", async (HttpContext ctx, ChannelCredentialStore cred,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters,
            Services.Chat.Channels.MessengerPageChoices chon, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var ma = form["ma"].ToString();
            var c = chon.Nhan(ma, form["pageId"].ToString());
            if (c is null)
                return PermissionPage(false, "Lượt chọn đã hết hạn. Bấm lại nút Kết nối Facebook.");

            var fb = adapters.OfType<Services.Chat.Channels.MessengerChatAdapter>().FirstOrDefault();
            if (fb is null) return PermissionPage(false, "Kênh Messenger chưa được bật ở máy chủ.");

            var loi = await fb.ConnectPageAsync(c.Value.TenantId, c.Value.Pages, ct);
            if (loi is not null) return PermissionPage(false, loi);
            var kemIg = await ConnectLinkedInstagramAsync(adapters, c.Value.TenantId, c.Value.Pages, ct);

            // Vẽ lại danh sách thay vì đóng cửa sổ: công ty nhiều chi nhánh nối vài Trang liền tay,
            // đóng phụt sau Trang đầu là bắt họ đăng nhập Facebook lại từ đầu cho Trang thứ hai.
            var con = chon.Xem(ma);
            return PagePickerPage(ma, con?.Pages ?? Array.Empty<Services.Chat.Channels.PageCandidate>(),
                await ConnectedIdsAsync(cred, c.Value.TenantId, ct),
                $"Đã nối Trang \"{c.Value.Pages.Name}\". Tin nhắn mới sẽ vào hộp thư ngay.");
        });

        // ── Khai kết nối kênh ───────────────────────────────────────────────
        // Cần quyền Cấu hình hệ thống: đây là khoá cấp CÔNG TY, ai cầm được là nhắn tin dưới danh
        // nghĩa công ty.
        //
        // MỘT công ty nối được NHIỀU tài khoản mỗi kênh (nhiều Trang Facebook cho các chi nhánh,
        // nhiều OA Zalo, nhiều bot Telegram cho từng đội sale).
        g.MapGet("/channels", async (HttpContext ctx, TkSessionStore sessions,
            ChannelCredentialStore cred, IConfiguration cfg,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();

            var goc = PublicOrigin(ctx, cfg);
            // Máy chủ đã khai ứng dụng cấp nền tảng cho kênh này chưa. Có thì giao diện giấu hết ô
            // nhập và chỉ hiện một nút — khách không phải chạm vào cổng developer của Zalo/Meta.
            var zaloNhanh = adapters.OfType<Services.Chat.Channels.ZaloChatAdapter>()
                                    .FirstOrDefault()?.HasPlatformApp == true;
            var fbNhanh = adapters.OfType<Services.Chat.Channels.MessengerChatAdapter>()
                                  .FirstOrDefault()?.HasPlatformApp == true;
            var waNhanh = adapters.OfType<Services.Chat.Channels.WhatsAppChatAdapter>()
                                  .FirstOrDefault()?.HasPlatformApp == true;
            var ttNhanh = adapters.OfType<Services.Chat.Channels.TikTokChatAdapter>()
                                  .FirstOrDefault()?.HasPlatformApp == true;
            var batLichSu = Services.Bootstrap.FeatureFlags.ChatHistoryImport(cfg);
            var ra = new List<object>();
            foreach (var (kenh, ten, tenNgan, oNhap, moiTaiKhoanMotUrl) in KhaiBao)
            {
                var dsach = await cred.ListAccountsAsync(a.TenantId, kenh, ct);
                var nhanh = kenh switch
                {
                    ChatChannel.Zalo => zaloNhanh,
                    ChatChannel.Messenger => fbNhanh,
                    ChatChannel.WhatsApp => waNhanh,
                    ChatChannel.TikTok => ttNhanh,
                    _ => false,
                };
                // Ứng dụng dùng chung thì URL webhook cũng dùng chung — khai MỘT lần trong ứng dụng
                // của TourKit, khách không phải dán gì. Vẫn trả về để quản trị đối chiếu.
                var duong = nhanh
                    ? $"{goc}/api/v1/chat/webhook/{kenh.ToString().ToLowerInvariant()}"
                    : $"{goc}/api/v1/chat/webhook/{kenh.ToString().ToLowerInvariant()}/{a.TenantId}";
                ra.Add(new
                {
                    channel = (short)kenh, name = ten, shortName = tenNgan, fields = oNhap,
                    // Máy chủ ĐÃ đủ khoá để nối bằng một nút chưa.
                    noiNhanh = nhanh,
                    // Kênh này CÓ đường một nút hay không — khác hẳn cờ trên.
                    //
                    // Thiếu vế này thì hai trạng thái rất khác nhau bị trộn làm một: "kênh vốn
                    // phải khai tay" (Telegram — bot token là đường DUY NHẤT) và "kênh nối một
                    // nút nhưng quản trị chưa khai khoá ứng dụng" (WhatsApp, TikTok). Trộn lại
                    // thì người dùng nhìn thấy bốn ô kỹ thuật và tưởng đó là việc của mình, đi
                    // tìm mã trong bảng điều khiển Meta — trong khi việc cần làm là báo quản trị
                    // điền một khoá vào máy chủ.
                    hoTroNoiNhanh = kenh is ChatChannel.Zalo or ChatChannel.Messenger
                                         or ChatChannel.WhatsApp or ChatChannel.TikTok,
                    // Chữ trên nút. Để máy chủ quyết định để giao diện không phải biết tên kênh —
                    // thêm kênh nối-một-chạm mới thì không phải sửa .jsx.
                    nutNoi = kenh switch
                    {
                        ChatChannel.Zalo => "Kết nối Zalo OA",
                        ChatChannel.Messenger => "Kết nối Facebook",
                        ChatChannel.WhatsApp => "Kết nối WhatsApp",
                        ChatChannel.TikTok => "Kết nối TikTok",
                        ChatChannel.Instagram => "Kết nối qua Facebook",
                        _ => "Kết nối",
                    },
                    // Kênh này nối KÈM kênh nào (mã kênh), hay tự nối lấy.
                    //
                    // Instagram không có bước cấp quyền riêng: nó đi theo chính Trang Facebook
                    // đã nối — nối Trang là hệ thống tự tìm tài khoản Instagram liên kết vào đó.
                    // Không nói ra thì tab này bày ba ô khai tay và người dùng tưởng phải đi tìm
                    // token ở đâu đó, trong khi việc cần làm nằm ở tab bên cạnh.
                    noiKemKenh = kenh == ChatChannel.Instagram && fbNhanh
                        ? (short?)ChatChannel.Messenger : null,
                    // Kênh này lấy lại được đoạn chat cũ không. Bốn kênh kia KHÔNG có đường nào:
                    // Telegram Bot API không cho đọc quá khứ, Zalo không có đầu đọc hội thoại,
                    // TikTok đòi tư cách Messaging Partner, WhatsApp thì Meta tự đẩy về lúc nối
                    // chứ không phải mình đi đọc. Để giao diện không phải biết danh sách đó.
                    layLichSuDuoc = batLichSu
                                    && Services.Chat.Channels.MetaHistoryImporter.Supports(kenh),
                    // Telegram: mỗi bot một URL riêng (thân tin không nói bot nào) → URL chung để
                    // trống, giao diện hiện URL riêng ở từng tài khoản. Zalo/Messenger dùng chung.
                    webhookUrl = moiTaiKhoanMotUrl ? null : duong,
                    accounts = dsach.Select(t => new
                    {
                        accountId = t.AccountId,
                        label = t.GiaTri.GetValueOrDefault("label", ""),
                        // Tên/id THẬT do nhà cung cấp trả về sau khi nối — khác "Tên gợi nhớ" người
                        // dùng tự đặt. Nối nhiều OA/Trang mà không có cái này thì không phân biệt
                        // nổi. Zalo cất ở oaName/oaId, Meta ở pageName/pageId; giao diện chỉ cần
                        // MỘT cặp khoá nên gộp ở đây.
                        oaName = t.GiaTri.GetValueOrDefault("oaName", "")
                                 is { Length: > 0 } tenThat ? tenThat
                                 : t.GiaTri.GetValueOrDefault("pageName", "")
                                   is { Length: > 0 } tenTrang ? tenTrang
                                   : t.GiaTri.GetValueOrDefault("botUsername", ""),
                        oaId = t.GiaTri.GetValueOrDefault("oaId", "")
                               is { Length: > 0 } idThat ? idThat
                               : t.GiaTri.GetValueOrDefault("pageId", "")
                                 is { Length: > 0 } idTrang ? idTrang
                                 : t.GiaTri.GetValueOrDefault("botId", ""),
                        configured = IsFullyConfigured(kenh, t.GiaTri, nhanh),
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
            HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred, IConfiguration cfg,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            var accountId = Guid.NewGuid().ToString("N")[..8];

            var loi = await ConnectTelegramBotAsync(adapters, ctx, cfg, a.TenantId, accountId,
                (ChatChannel)channel, body, ct);
            if (loi is not null) return Results.BadRequest(new { error = loi });

            await cred.SaveAsync(a.TenantId, (ChatChannel)channel, accountId, body, ct);
            return Results.Json(new { ok = true, accountId }, Web);
        });

        g.MapPut("/channels/{channel:int}/accounts/{accountId}", async (int channel, string accountId,
            Dictionary<string, string?> body, HttpContext ctx, TkSessionStore sessions,
            ChannelCredentialStore cred, IConfiguration cfg,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            // Dan bot token MOI thi phai dang ky lai dia chi nhan tin - chuoi bi mat cu gan voi
            // token cu, giu lai la webhook cam ma khong bao gi. De trong o token = giu nguyen.
            var loi = await ConnectTelegramBotAsync(adapters, ctx, cfg, a.TenantId, accountId,
                (ChatChannel)channel, body, ct);
            if (loi is not null) return Results.BadRequest(new { error = loi });

            await cred.SaveAsync(a.TenantId, (ChatChannel)channel, accountId, body, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapDelete("/channels/{channel:int}/accounts/{accountId}", async (int channel, string accountId,
            HttpContext ctx, TkSessionStore sessions, ChannelCredentialStore cred, ChatRepository repo,
            IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!Enum.IsDefined(typeof(ChatChannel), (short)channel))
                return Results.BadRequest(new { error = "Kênh không hợp lệ" });

            // ⚠️ Nói với nhà cung cấp TRƯỚC khi xoá khoá — xoá trước thì không còn khoá nào để
            // gọi lệnh huỷ, và họ nện vào đường đã chết mãi mãi. Mỗi kênh tự biết phải gọi gì
            // (hoặc không có gì để gọi); chỗ này không được biết tên kênh nào cả.
            var boNoi = adapters.FirstOrDefault(x => x.Channel == (ChatChannel)channel);
            var daGo = boNoi is null || await boNoi.DisconnectAsync(a.TenantId, accountId, ct);

            // CỐ Ý không xoá hội thoại cũ của tài khoản này: lịch sử chat với khách là dữ liệu
            // nghiệp vụ, gỡ kết nối chỉ nghĩa là "thôi không nhận/gửi qua tài khoản này nữa".
            var xoa = await cred.DeleteAsync(a.TenantId, (ChatChannel)channel, accountId, ct);
            // Không gắn với hội thoại nào — gỡ kết nối là việc ở mức tài khoản kênh.
            await repo.AppendAuditAsync(a.TenantId, null, a.Username, "go-ket-noi",
                new JsonObject
                {
                    ["kenh"] = channel,
                    ["taiKhoan"] = accountId,
                    // Ghi lại để về sau còn truy: gỡ mà nhà cung cấp không nhận thì webhook cũ vẫn
                    // sống, và triệu chứng lúc đó ("sao tự nhiên có tin lạ") không gợi được về đây.
                    ["daBaoNhaCungCap"] = daGo,
                }.ToJsonString(), ct);
            return Results.Json(new { ok = true, removed = xoa, providerNotified = daGo }, Web);
        });

        // ── Cấu hình trợ lý chat ────────────────────────────────────────────
        //
        // ĐỌC thì mọi nhân viên trực chat đều cần (giao diện hiện trạng thái bot bật/tắt);
        // SỬA thì cần quyền cấu hình hệ thống — đây là giọng nói của cả công ty với khách.
        g.MapGet("/bot-settings", async (HttpContext ctx, TkSessionStore sessions,
            ChatBotSettingsRepository repo, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();

            var v = await repo.GetAsync(a.TenantId, ct);
            return Results.Json(new
            {
                enabled = v.Enabled,
                // Số giây giữ tin lại trước khi gửi. Giao diện cần để biết có vẽ nút Thu hồi
                // hay không; 0 = quản trị đã tắt, đừng vẽ dải đếm ngược làm gì.
                undoSendSeconds = ChatRules.HoanGuiGiay(ChatSender.Agent,
                    cfg.GetValue("Chat:UndoSendSeconds", 5)),
                persona = v.Persona,
                greeting = v.Greeting,
                muteMinutes = v.MuteMinutes,
                historyTurns = v.HistoryTurns,
                // Giới hạn do máy chủ nói ra để giao diện không phải chép cứng — sửa mốc ở
                // Domain là màn hình đổi theo, không lệch.
                limits = new
                {
                    personaChars = ChatBotSettings.MaxPersonaChars,
                    minHistory = ChatBotSettings.MinHistoryTurns,
                    maxHistory = ChatBotSettings.MaxHistoryTurns,
                },
            }, Web);
        });

        g.MapPut("/bot-settings", async (BotSettingsReq body, HttpContext ctx,
            TkSessionStore sessions, ChatBotSettingsRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return NotConfigured();

            // Kẹp giá trị nằm ở Domain (Normalized) chứ không ở đây: worker đọc cùng bộ luật đó,
            // và kiểm hai nơi là hai nơi lệch nhau.
            await repo.SaveAsync(a.TenantId, new ChatBotSettings(
                body.Enabled, body.Persona, body.Greeting,
                body.MuteMinutes ?? 30, body.HistoryTurns ?? 12), ct);

            return Results.Json(new { ok = true }, Web);
        });

        // ── Mẫu trả lời nhanh ───────────────────────────────────────────────
        // ĐỌC thì mọi nhân viên trực chat đều cần; SỬA/XOÁ thì cần quyền cấu hình hệ thống —
        // đây là bộ câu dùng chung cả đội, một người sửa là cả đội đổi theo.
        g.MapGet("/quick-replies", async (HttpContext ctx, TkSessionStore sessions,
            ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            // ⚠️ Bóc nút ra MẢNG THẬT, đừng trả nguyên chuỗi JSON trong CSDL. Trả chuỗi thì
            // giao diện gọi .length lên nó và ra SỐ KÝ TỰ — màn hình hiện "68 nút" cho một mẫu
            // có hai nút, còn chỗ chèn mẫu vào ô soạn thì .map lên chuỗi và hỏng hẳn.
            var ds = await repo.ListAsync(a.TenantId, ct);
            return Results.Json(new
            {
                items = ds.Select(m => new
                {
                    m.Id, m.Trigger, m.Body,
                    buttons = ChatRules.ReadButtons(m.Buttons)
                        .Select(b => new { chu = b.Label, url = b.Url }),
                }),
            }, Web);
        });

        g.MapPut("/quick-replies", async (QuickReplyReq body, HttpContext ctx,
            TkSessionStore sessions, ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return NotConfigured();
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.BadRequest(new { error = "Chưa nhập nội dung mẫu" });
            try
            {
                // Nút KHÔNG cắt theo kênh ở đây: mẫu dùng chung cho cả sáu kênh, mà mỗi kênh
                // một giới hạn. Cắt lúc GỬI, khi đã biết hội thoại thuộc kênh nào.
                var nutMau = body.Buttons is { Count: > 0 } dsN
                    ? new JsonArray(dsN.Select(x => (JsonNode)new JsonObject
                      {
                          ["chu"] = x.Label,
                          ["url"] = x.Url,
                      }).ToArray()).ToJsonString()
                    : null;
                var id = await repo.UpsertAsync(a.TenantId, body.Trigger, body.Body.Trim(), ct,
                    buttonsJson: nutMau);
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
            if (!repo.Configured) return NotConfigured();
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
    /// <summary>
    /// Nối bot Telegram bằng MỘT nút: xác thực token → sinh chuỗi bí mật → đăng ký địa chỉ nhận
    /// tin. Kết quả bơm thẳng vào <paramref name="body"/> để chỗ gọi lưu MỘT lần.
    /// </summary>
    /// <returns><c>null</c> = xong (hoặc không phải việc của kênh này); khác <c>null</c> = câu lỗi
    /// hiện cho người dùng.</returns>
    private static async Task<string?> ConnectTelegramBotAsync(
        IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, HttpContext ctx,
        IConfiguration cfg, string tenantId, string accountId, ChatChannel kenh,
        Dictionary<string, string?> body, CancellationToken ct)
    {
        if (kenh != ChatChannel.Telegram) return null;

        // Ô token để trống = người dùng đang sửa tên gợi nhớ, hoặc mới tạo bản nháp. Không đăng ký
        // lại: chuỗi bí mật hiện có vẫn đúng với token hiện có.
        body.TryGetValue("botToken", out var token);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var tg = adapters.OfType<Services.Chat.Channels.TelegramChatAdapter>().FirstOrDefault();
        if (tg is null) return "Kênh Telegram chưa được bật trên máy chủ";

        // ⚠️ Mỗi bot MỘT đường riêng, có mã tài khoản trên URL — thân tin Telegram không nói bot
        // nào, định danh duy nhất nằm ở chính đường dẫn đã khai lúc đăng ký.
        var duong = $"{PublicOrigin(ctx, cfg)}/api/v1/chat/webhook/telegram/{tenantId}/{accountId}";
        var kq = await tg.ConnectBotAsync(token, duong, ct);
        if (!kq.Ok) return kq.Loi;

        body["webhookSecret"] = kq.ChuoiBiMat;
        if (!string.IsNullOrWhiteSpace(kq.BotId)) body["botId"] = kq.BotId;
        if (!string.IsNullOrWhiteSpace(kq.Username)) body["botUsername"] = kq.Username;

        // Chưa đặt tên gợi nhớ thì lấy @tên bot. Nối ba bot mà cả ba đều trống tên thì trong danh
        // sách không phân biệt nổi cái nào với cái nào.
        if (string.IsNullOrWhiteSpace(body.GetValueOrDefault("label"))
            && !string.IsNullOrWhiteSpace(kq.Username))
            body["label"] = "@" + kq.Username;

        return null;
    }

    /// <summary>
    /// Bot token của một tài khoản Telegram. Lùi về <c>Telegram:BotToken</c> dùng chung khi tài
    /// khoản chưa có khoá riêng — giữ tương thích với bản một-bot cũ, y như trong bộ nối.
    /// </summary>
    private static async Task<string?> TelegramTokenAsync(ChannelCredentialStore cred,
        IConfiguration cfg, string tenantId, string? accountId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var c = await cred.GetAsync(tenantId, ChatChannel.Telegram, accountId!, ct);
            if (c is not null && c.TryGetValue("botToken", out var rieng) && !string.IsNullOrWhiteSpace(rieng))
                return rieng;
        }
        var chung = cfg["Telegram:BotToken"];
        return string.IsNullOrWhiteSpace(chung) ? null : chung;
    }

    /// <summary>Đổi <c>file_id</c> thành byte thật. Hai lượt gọi: hỏi đường, rồi tải.</summary>
    /// <remarks>Token nằm TRONG đường dẫn nên tuyệt đối không ghi URL ra nhật ký.</remarks>
    private static async Task<IResult> TelegramFileAsync(IHttpClientFactory httpFac, string token,
        string fid, CancellationToken ct)
    {
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
    }

    /// <summary>
    /// Ảnh đại diện cho giao diện. Đường TƯƠNG ĐỐI nghĩa là ảnh phải đi qua máy chủ mình (Telegram
    /// — vì đường tải thật của họ chứa bot token), nên phải gắn thêm mã phiên: thẻ &lt;img&gt; không
    /// gửi được tiêu đề xác thực. Đường tuyệt đối (Zalo/Meta) thì để nguyên.
    /// </summary>
    private static string? ContactAvatarUrl(string? url, string sessionId)
        => string.IsNullOrWhiteSpace(url) || !url.StartsWith('/') ? url
           : $"{url}?sessionId={Uri.EscapeDataString(sessionId)}";

    /// <summary>
    /// Nối luôn tài khoản Instagram liên kết với Trang vừa nối, nếu có.
    ///
    /// <para>Instagram Direct đi qua CHÍNH Trang đó — cùng ứng dụng Meta, cùng token, cùng khoá ký.
    /// Nên bắt khách bấm thêm một nút nữa là bắt họ làm một việc máy tự làm được.</para>
    ///
    /// <para>⚠️ Không bao giờ chặn việc nối Trang: Trang không có Instagram là chuyện bình thường.</para>
    /// </summary>
    /// <returns>Đoạn chữ nối thêm vào câu báo thành công, hoặc rỗng.</returns>
    private static async Task<string> ConnectLinkedInstagramAsync(
        IEnumerable<Services.Chat.Channels.IChatChannelAdapter> adapters, string tenantId,
        Services.Chat.Channels.PageCandidate trang, CancellationToken ct)
    {
        var ig = adapters.OfType<Services.Chat.Channels.InstagramChatAdapter>().FirstOrDefault();
        if (ig is null) return "";
        var id = await ig.ConnectFromPageAsync(tenantId, trang.PageId, trang.Name, trang.AccessToken, ct);
        return id is null ? "" : " và tài khoản Instagram liên kết";
    }

    private static string PublicOrigin(HttpContext ctx, IConfiguration cfg)
    {
        var dat = cfg["Chat:PublicBaseUrl"];
        return string.IsNullOrWhiteSpace(dat)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : dat.TrimEnd('/');
    }

    /// <param name="Values">Giá trị từng ô, khoá theo <c>ChatTemplateSlot.Key</c>.</param>
    public record SendTemplateReq(string? TemplateId, Dictionary<string, string>? Values);

    /// <summary>
    /// Chữ ghi vào hội thoại cho một tin mẫu.
    ///
    /// <para>Nền tảng KHÔNG trả về nội dung cuối cùng sau khi ghép ô, và bên mình cũng không
    /// dựng lại được (bản xem trước có thể thiếu, mẫu có thể đổi). Nên ghi tên mẫu kèm giá trị
    /// đã điền — đủ để sau này ai đọc lại hội thoại cũng biết khách đã nhận gì.</para>
    /// </summary>
    private static string MoTaMauDaGui(Services.Chat.Channels.ChatTemplate mau,
        IReadOnlyDictionary<string, string> giaTri)
    {
        var oDien = mau.Slots.Count == 0
            ? ""
            : " — " + string.Join(" · ", mau.Slots.Select(x => giaTri.GetValueOrDefault(x.Key, "")));
        return $"[Tin mẫu] {mau.Name}{oDien}";
    }

    private const string ZaloCallbackPath = "/api/v1/chat/oauth/zalo/callback";

    /// <remarks>Chuỗi này phải nằm trong <b>Valid OAuth Redirect URIs</b> của ứng dụng bên Meta,
    /// khớp từng ký tự. Đổi ở đây là phải sửa cả bên đó.</remarks>
    private const string MessengerCallbackPath = "/api/v1/chat/oauth/messenger/callback";

    /// Đường Meta gọi lại sau khi người dùng đi hết luồng Embedded Signup của WhatsApp.
    private const string WhatsAppCallbackPath = "/api/v1/chat/oauth/whatsapp/callback";

    /// Đường TikTok gọi lại sau khi người dùng bấm Đồng ý.
    private const string TikTokCallbackPath = "/api/v1/chat/oauth/tiktok/callback";

    /// <summary>Id các Trang công ty này đã nối — để trang chọn Trang không mời nối lại cái đã có.</summary>
    private static async Task<HashSet<string>> ConnectedIdsAsync(ChannelCredentialStore cred, string tenantId,
        CancellationToken ct)
    {
        var ds = await cred.ListAccountsAsync(tenantId, ChatChannel.Messenger, ct);
        return ds.Select(t => t.GiaTri.GetValueOrDefault("pageId", t.AccountId))
                 .Where(x => !string.IsNullOrWhiteSpace(x))
                 .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Trang CHỌN TRANG — nửa sau của bước nối Facebook, thứ Zalo không cần.
    ///
    /// <para>Là HTML dựng tay chứ không phải một trang React vì nó chạy trong cửa sổ phụ Meta vừa
    /// đá về: <b>không có phiên đăng nhập</b>, nên không nạp được ứng dụng chính. Một form thuần,
    /// mỗi Trang một nút, mã <c>ma</c> là thứ duy nhất chứng minh lượt chọn này là thật.</para>
    /// </summary>
    private static IResult PagePickerPage(string ma, IReadOnlyList<Services.Chat.Channels.PageCandidate> trang,
        HashSet<string> daNoi, string? thongDiep)
    {
        var b = new System.Text.StringBuilder();
        b.Append("<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\">");
        b.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        b.Append("<title>Chọn Trang Facebook</title><style>");
        b.Append("body{font-family:system-ui,sans-serif;padding:28px 24px;line-height:1.6;color:#0F172A;max-width:520px;margin:0 auto}");
        b.Append("h2{margin:0 0 4px;font-size:20px}p{margin:0 0 20px;color:#64748B}");
        b.Append(".ok{background:#F0FDF4;border:1px solid #BBF7D0;color:#15803D;padding:10px 12px;border-radius:8px;margin:0 0 16px}");
        b.Append("ul{list-style:none;padding:0;margin:0;border:1px solid #E2E8F0;border-radius:10px;overflow:hidden}");
        b.Append("li{display:flex;align-items:center;gap:12px;padding:12px 14px;border-top:1px solid #E2E8F0}");
        b.Append("li:first-child{border-top:0}");
        b.Append(".ten{flex:1;min-width:0}.ten b{display:block;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}");
        b.Append(".ten small{color:#94A3B8;font-variant-numeric:tabular-nums}");
        b.Append("button{font:inherit;font-size:14px;padding:7px 14px;border-radius:8px;border:0;cursor:pointer}");
        b.Append("button.chinh{background:#0F172A;color:#fff}button.phu{background:#F1F5F9;color:#475569}");
        b.Append("button:active{transform:translateY(1px)}");
        b.Append(".da{color:#15803D;font-size:13px;font-weight:600}");
        b.Append("</style></head><body>");
        b.Append("<h2>Chọn Trang để nối</h2>");
        b.Append("<p>Chỉ Trang bạn chọn mới vào hộp thư. Nối xong một Trang có thể chọn tiếp Trang khác, "
            + "hoặc đóng cửa sổ này.</p>");
        if (thongDiep is not null) b.Append("<div class=\"ok\">").Append(System.Net.WebUtility.HtmlEncode(thongDiep)).Append("</div>");

        if (trang.Count == 0)
            b.Append("<p>Không có Trang nào để chọn.</p>");
        else
        {
            b.Append("<form method=\"post\" action=\"/api/v1/chat/oauth/messenger/chon\">");
            b.Append("<input type=\"hidden\" name=\"ma\" value=\"").Append(System.Net.WebUtility.HtmlEncode(ma)).Append("\"><ul>");
            foreach (var t in trang)
            {
                var xong = daNoi.Contains(t.PageId);
                b.Append("<li><span class=\"ten\"><b>").Append(System.Net.WebUtility.HtmlEncode(t.Name))
                 .Append("</b><small>").Append(System.Net.WebUtility.HtmlEncode(t.PageId)).Append("</small></span>");
                if (xong) b.Append("<span class=\"da\">Đã nối</span>");
                b.Append("<button class=\"").Append(xong ? "phu" : "chinh")
                 .Append("\" name=\"pageId\" value=\"").Append(System.Net.WebUtility.HtmlEncode(t.PageId)).Append("\">")
                 .Append(xong ? "Nối lại" : "Nối Trang này").Append("</button></li>");
            }
            b.Append("</ul></form>");
        }
        b.Append("</body></html>");
        return Results.Content(b.ToString(), "text/html; charset=utf-8");
    }

    /// <summary>
    /// Trang nhỏ trả về sau khi cấp quyền. Hỏng thì để nguyên cho người dùng đọc lý do — đóng
    /// phụt mất câu lỗi là kiểu tệ nhất, họ chỉ thấy "không có gì xảy ra".
    ///
    /// <para>⚠️ <b>Không được chỉ gọi <c>window.close()</c>.</b> Lệnh đó chỉ chạy được với cửa sổ
    /// do chính script mở ra. Trên ĐIỆN THOẠI, luồng cấp quyền thường đi ngay trong tab hiện tại
    /// (trình duyệt di động chặn cửa sổ bật lên), nên <c>close()</c> im lặng không làm gì và người
    /// dùng mắc kẹt ở trang trắng này — cấp quyền xong xuôi mà trông như hỏng.</para>
    ///
    /// <para>Nên: có cửa sổ mẹ thì đóng như cũ; không có thì quay về thẳng hộp thư.</para>
    /// </summary>
    private static IResult PermissionPage(bool xong, string thongDiep)
    {
        var mau = xong ? "#16A34A" : "#DC2626";
        var tuDong = xong
            ? "<script>setTimeout(function(){"
              + "if(window.opener&&!window.opener.closed){window.close();}"
              + "else{location.href='/chat-inbox';}},1500)</script>"
            : "";
        var html = $"""
            <!doctype html><html lang="vi"><head><meta charset="utf-8">
            <title>Cấp quyền kênh chat</title></head>
            <body style="font-family:system-ui,sans-serif;padding:32px;line-height:1.6">
            <h2 style="color:{mau};margin:0 0 8px">{(xong ? "Đã cấp quyền" : "Cấp quyền không xong")}</h2>
            <p>{System.Net.WebUtility.HtmlEncode(thongDiep)}</p>
            <p style="color:#64748B">{(xong ? "Đang quay lại hộp thư…" : "Quay lại hộp thư rồi thử lại.")}</p>
            {tuDong}</body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Đã khai đủ khoá để tài khoản này chạy được chưa.
    ///
    /// <para><paramref name="nenTang"/> = máy chủ có ứng dụng dùng chung cho kênh này không. Tài
    /// khoản nối bằng một nút KHÔNG lưu khoá ứng dụng (chúng nằm ở appsettings), nên nếu vẫn đòi
    /// đủ các ô đó thì mọi tài khoản nối theo luồng mới đều hiện "chưa khai đủ" — sai, và người
    /// dùng sẽ đi khai tay lại một thứ đang chạy tốt.</para>
    /// </summary>
    private static bool IsFullyConfigured(ChatChannel kenh, IReadOnlyDictionary<string, string> g, bool nenTang) => kenh switch
    {
        ChatChannel.Zalo => g.ContainsKey("refreshToken")
                            && (nenTang || (g.ContainsKey("appId") && g.ContainsKey("secretKey"))),
        ChatChannel.Messenger => g.ContainsKey("pageId") && g.ContainsKey("pageAccessToken")
                                 && (nenTang || (g.ContainsKey("appSecret") && g.ContainsKey("verifyToken"))),
        ChatChannel.Telegram => g.ContainsKey("botToken") && g.ContainsKey("webhookSecret"),
        // Instagram đi bằng chính Page Access Token của Trang đã nối, nên chỉ cần hai ô này.
        ChatChannel.Instagram => g.ContainsKey("igId") && g.ContainsKey("pageAccessToken"),
        ChatChannel.WhatsApp => g.ContainsKey("phoneNumberId") && g.ContainsKey("accessToken"),
        ChatChannel.TikTok => g.ContainsKey("openId") && g.ContainsKey("businessId")
                              && g.ContainsKey("accessToken"),
        _ => false,
    };

    private static IResult NotConfigured()
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
    private record FieldSpec(string Key, string Label, string Type = "text", string Hint = "");

    /// <param name="TenNgan">Tên cho dải tab. Từ khi có SÁU kênh, tên đầy đủ làm dải tab vỡ
    /// thành hai dòng cao thấp lệch nhau. Tên đầy đủ vẫn dùng cho tiêu đề mục bên dưới — chỗ đó
    /// có chỗ và cần nói rõ ("Zalo OA" khác Zalo cá nhân, "Instagram Direct" là tin nhắn riêng).</param>
    private static readonly (ChatChannel Kenh, string Ten, string TenNgan, FieldSpec[] O, bool MoiTaiKhoanMotUrl)[] KhaiBao =
    {
        (ChatChannel.Zalo, "Zalo OA", "Zalo", new[]
        {
            new FieldSpec("label",        "Tên gợi nhớ", "text",   "OA Hà Nội"),
            new FieldSpec("appId",        "App ID",      "text",   "1234567890123456789"),
            new FieldSpec("secretKey",    "App Secret Key", "secret", "Ứng dụng → Cài đặt. Dùng để GỬI tin"),
            new FieldSpec("oaSecretKey",  "OA Secret Key",  "secret", "Sản phẩm → Official Account → Cài đặt chung. Dùng để NHẬN tin"),
            new FieldSpec("refreshToken", "Refresh Token", "secret", "Để trống — bấm \"Cấp quyền OA\" là tự điền"),
            new FieldSpec("note",
                "Zalo cấp HAI khoá bí mật khác nhau: App Secret Key để gửi, OA Secret Key để nhận. "
                + "Bỏ trống OA Secret Key thì hệ thống dùng tạm App Secret Key — nếu tin khách không "
                + "vào hộp thư thì gần như chắc chắn là do thiếu ô này.", "note"),
            new FieldSpec("note2",
                "Đây là OA RIÊNG của chat, độc lập với OA khai cho bản tin sáng ở Tự động hoá.", "note"),
        }, false),
        (ChatChannel.Messenger, "Facebook Messenger", "Messenger", new[]
        {
            new FieldSpec("label",           "Tên gợi nhớ", "text", "Trang chi nhánh Q1"),
            new FieldSpec("pageId",          "ID Trang",    "text", "102938475610293"),
            new FieldSpec("pageAccessToken", "Page Access Token", "secret", "EAAG… (lấy ở Meta for Developers)"),
            new FieldSpec("appSecret",       "App Secret",  "secret", "Dùng để kiểm chữ ký webhook"),
            new FieldSpec("verifyToken",     "Verify Token", "secret", "Bạn tự đặt, dán y hệt vào Meta"),
            new FieldSpec("note",
                "Bốn ô này CHỈ dùng khi công ty tự tạo ứng dụng riêng trên Meta for Developers. "
                + "Bình thường bấm \"Kết nối Facebook\" là xong — không phải khai gì.", "note"),
        }, false),
        (ChatChannel.Instagram, "Instagram Direct", "Instagram", new[]
        {
            new FieldSpec("label",           "Tên gợi nhớ", "text", "IG chi nhánh Q1"),
            new FieldSpec("igId",            "ID tài khoản Instagram", "text", "17841400000000000"),
            new FieldSpec("pageAccessToken", "Page Access Token", "secret",
                "Token của Trang Facebook mà tài khoản Instagram này liên kết vào"),
            new FieldSpec("note",
                "Hai ô này CHỈ dùng khi vì lý do nào đó hệ thống không tự tìm ra tài khoản "
                + "Instagram lúc nối Trang. Bình thường không phải khai gì.", "note"),
            new FieldSpec("note2",
                "Tài khoản phải là Instagram Professional, đã liên kết với Trang Facebook đó, "
                + "và đã bật \"Cho phép truy cập tin nhắn\" trong cài đặt Instagram. Thiếu một "
                + "trong ba thì Facebook không trả tài khoản Instagram nào về cả.", "note"),
        }, false),
        (ChatChannel.WhatsApp, "WhatsApp", "WhatsApp", new[]
        {
            new FieldSpec("label",         "Tên gợi nhớ", "text", "Số hotline tour nước ngoài"),
            new FieldSpec("phoneNumberId", "Phone Number ID", "text", "1088888888888888"),
            new FieldSpec("wabaId",        "WhatsApp Business Account ID", "text", "1099999999999999"),
            new FieldSpec("accessToken",   "Access Token", "secret", "Token hệ thống của WABA"),
            new FieldSpec("appSecret",     "App Secret", "secret",
                "Để trống nếu dùng chung ứng dụng Meta với Facebook"),
            new FieldSpec("note",
                "Bốn ô này CHỈ dùng khi công ty tự tạo ứng dụng riêng trên Meta for Developers. "
                + "Bình thường bấm \"Kết nối WhatsApp\" là xong — không phải khai gì.", "note"),
            new FieldSpec("note2",
                "WhatsApp cần tài khoản doanh nghiệp đã xác minh và một số điện thoại RIÊNG "
                + "(số đã dùng cho ứng dụng WhatsApp thường thì không khai được). Ngoài 24 giờ kể "
                + "từ tin của khách chỉ gửi được mẫu đã duyệt, không gửi chữ tự do.", "note"),
        }, false),
        (ChatChannel.TikTok, "TikTok", "TikTok", new[]
        {
            new FieldSpec("label",        "Tên gợi nhớ", "text", "TikTok bán tour"),
            new FieldSpec("openId",       "Open ID tài khoản", "text", "_000AbCdEf…"),
            new FieldSpec("businessId",   "Business ID", "text", "7000000000000000000"),
            new FieldSpec("accessToken",  "Access Token", "secret", "Token của ứng dụng TikTok for Business"),
            new FieldSpec("clientSecret", "Client Secret", "secret", "Dùng để kiểm chữ ký webhook"),
            new FieldSpec("note",
                "Bốn ô này CHỈ dùng khi công ty tự tạo ứng dụng riêng trên TikTok for Business. "
                + "Bình thường bấm \"Kết nối TikTok\" là xong — không phải khai gì.", "note"),
            new FieldSpec("note2",
                "TikTok cần ứng dụng TikTok for Business đã được duyệt quyền nhắn tin. Kênh này "
                + "chỉ gửi được CHỮ và ẢNH; tệp, âm thanh, video thì gửi đường dẫn bằng tin chữ.", "note"),
        }, false),
        (ChatChannel.Telegram, "Telegram", "Telegram", new[]
        {
            // Các bước ĐỨNG TRƯỚC ô nhập: người khai đọc cách lấy mã rồi mới có cái để dán. Ô
            // "Bot token" đứng một mình giả định họ đã biết lấy ở đâu — mà đó đúng là chỗ tắc.
            new FieldSpec("buoc",
                "Mở [@BotFather](https://t.me/BotFather) trong Telegram."
                + "|Gửi **/newbot**, đặt tên hiển thị rồi đặt tên đăng nhập kết thúc bằng **bot**."
                + "|BotFather trả về một dòng mã dạng **8012345678:AAH…** — chép rồi dán xuống dưới.",
                "steps"),
            new FieldSpec("label",         "Tên gợi nhớ", "text", "Bot đội sale lẻ"),
            new FieldSpec("botToken",      "Bot token",   "secret", "123456:ABC-DEF… (dán mã BotFather đưa)"),
            new FieldSpec("note",
                "Bấm Lưu là xong — hệ thống tự kiểm tra mã, tự đặt chuỗi bí mật và tự đăng ký địa "
                + "chỉ nhận tin với Telegram. Bạn không phải gõ lệnh nào bên ngoài.", "note"),
        }, true),
    };

    private static object Shape(ChatConversation v, string sessionId)
    {
        // Mốc đọc RIÊNG của người đang xem. Chưa mở lần nào thì lùi về mốc chung cũ — không thì
        // mọi hội thoại cũ bật lại thành "chưa đọc" cho tất cả mọi người ngay sau khi nâng cấp.
        var docToi = v.MyLastReadAt ?? v.AgentLastReadAt;
        return new
        {
            v.Id, v.Channel, v.ContactExternalId, v.AccountId, v.Status, v.AssignedUsername, v.Followed,
            // Khách bị chặn — giao diện phải hiện rõ, không thì người trực tưởng kênh hỏng khi
            // tin gửi đi cứ bị bỏ qua.
            blocked = v.BlockedUtc is not null,
            v.LastActivityAt, v.LastPreview, v.ContactRepliedAt,
            displayName = v.DisplayName,
            avatarUrl = ContactAvatarUrl(v.AvatarUrl, sessionId),
            // Khách đến từ đâu. Nhà cung cấp chỉ nói MỘT LẦN lúc mở cuộc trò chuyện, nên đây là
            // bản ghi duy nhất — không có API nào tra ngược được.
            referral = v.ReferralSource is null && v.ReferralRef is null && v.ReferralAdId is null
                ? null : new { source = v.ReferralSource, gtRef = v.ReferralRef, adId = v.ReferralAdId },
            // Bot có đang bị câm không — giao diện hiện rõ, không thì nhân viên tưởng bot hỏng.
            botPaused = v.BotResumeAt is { } m && m > DateTime.UtcNow,
            // Chưa đọc = khách nhắn sau lần CHÍNH MÌNH mở gần nhất.
            unread = v.ContactRepliedAt is { } cr && (docToi is null || cr > docToi),
        };
    }

    /// <param name="AttachmentKind">"anh" | "tep" — bỏ trống khi không đính kèm.</param>
/// <param name="Buttons">Nút gắn dưới tin. Bỏ trống = tin chữ thường.</param>
public record SendReq(string? Text, string? AttachmentUrl = null, string? AttachmentKind = null,
    string? AttachmentName = null, long? AttachmentSize = null,
    List<ChatButton>? Buttons = null);
    public record AssignReq(string? Username);
    /// <param name="CustomerId">Bỏ trống = GỠ nối khách CRM khỏi hội thoại này.</param>
    public record LinkCrmReq(int? CustomerId);
    /// <param name="Tag">Nhãn thô — server tự chuẩn hoá (bỏ dấu, hạ chữ thường, gạch nối).</param>
    public record TagReq(string? Tag);
    public record NoteReq(string? Body);
    public record StatusReq(short Status);
    public record BotReq(bool Paused, int? Minutes);
    public record EditMsgReq(string? Body);

    /// <param name="Trigger">Lệnh gọi thô — server tự chuẩn hoá (bỏ dấu, hạ chữ thường).</param>
    /// <param name="Buttons">Nút kèm mẫu. Bỏ trống hoặc rỗng = XOÁ nút đang có — màn hình sửa
    /// mẫu luôn gửi lên trạng thái đầy đủ, và "bỏ hết nút" phải làm được.</param>
    /// <param name="Persona">Lời dặn RIÊNG của công ty. NỐI THÊM vào khung an toàn, không thay
    /// thế — xem <see cref="ChatBotSettings.BuildSystemPrompt"/>.</param>
    public record BotSettingsReq(bool Enabled, string? Persona, string? Greeting,
        int? MuteMinutes, int? HistoryTurns);

    public record QuickReplyReq(string Trigger, string Body, List<ChatButton>? Buttons = null);
}
