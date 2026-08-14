using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Đăng ký nhận bản tin: ai nhận loại nào, mấy giờ, qua kênh nào — cộng gửi thử và cấu hình Zalo OA.
///
/// <para><b>KHÔNG kiểm quyền để quyết ai được đăng ký bản tin điều hành.</b> Bản kế hoạch định gác
/// bằng <c>CH_XEM_ALL</c>, nhưng TourKit.Api đã tự lo: <c>DashboardService.ResolveSpUserIdAsync</c>
/// chỉ truyền "xem tất cả" cho tài khoản có <c>BC_NV_XEM</c>, còn lại truyền chính user id nên thủ
/// tục lưu trữ tự lọc về số của riêng người đó. Proxy chỉ cần gửi kèm tài khoản. Tự gác thêm bằng
/// <c>CH_XEM_ALL</c> còn CHẶN OAN người có <c>BC_NV_XEM</c> mà không có <c>CH_XEM_ALL</c> — hỏng đúng
/// việc nó định bảo vệ, lại thêm một chỗ phải đồng bộ tay với mã quyền upstream.</para>
///
/// <para>Vẫn gác quyền ở <b>cấu hình Zalo OA</b> (<c>CH_HT_XEM</c>): đó là token cấp công ty do proxy
/// tự giữ, TourKit không biết gì để lọc giúp.</para>
///
/// <para>Mọi endpoint yêu cầu <c>X-Session-Id</c>; tenant + user lấy từ phiên, KHÔNG nhận từ client.</para>
/// </summary>
public static class DigestEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// Body PUT đăng ký (camelCase từ frontend).
    public record SubBody(bool Enabled, int SendHourLocal, bool ChannelInApp,
        bool ChannelEmail, string? Email, bool ChannelTelegram, string? TelegramChatId,
        bool ChannelZalo, string? ZaloPhone);

    public static IEndpointRouteBuilder MapDigestEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/digest");

        // ─── Danh sách đăng ký của chính mình ────────────────────────────────────
        g.MapGet("/subscriptions", async (HttpContext ctx, TkSessionStore sessions,
            DigestSubscriptionRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            var items = await repo.ListForUserAsync(a.TenantId, a.Username, ct);
            return Results.Json(new
            {
                items,
                briefTypes = new[]
                {
                    new { type = BriefTypes.Sale, label = "Bản tin sáng cho nhân viên bán hàng" },
                    new { type = BriefTypes.Ceo,  label = "Bản tin điều hành (giám đốc)" },
                },
                // Nhắc UI nói rõ với người dùng: số trong bản tin điều hành co theo quyền của họ
                // (TourKit lọc), chứ không phải "đăng ký được là thấy hết công ty".
                scopeNote = "Số liệu trong bản tin theo đúng phạm vi quyền của tài khoản bạn.",
            }, Web);
        });

        // ─── Lưu / cập nhật đăng ký ──────────────────────────────────────────────
        g.MapPut("/subscriptions/{briefType}", async (string briefType, SubBody body,
            HttpContext ctx, TkSessionStore sessions, DigestSubscriptionRepository repo,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Loại lạ phải chặn: lưu được sẽ tạo bản ghi mà KHÔNG workflow nào đọc → người dùng
            // thấy "đã lưu" rồi ngồi đợi bản tin không bao giờ tới, không dấu hiệu nào để lần ra.
            if (!BriefTypes.IsValid(briefType))
                return Results.BadRequest(new { error = "Loại bản tin không hợp lệ." });

            // KHÔNG còn chặn "bật mà 0 kênh": kênh trong app giờ LUÔN BẬT — bản tin luôn được lưu ở
            // Bảng tin để xem/nghe lại, kể cả khi người dùng không muốn nhận qua email/Telegram/Zalo.
            // Nên "0 kênh" là trạng thái hợp lệ (chỉ nhận trong app), không phải lỗi cấu hình nữa.

            // Bật kênh mà bỏ trống nơi nhận thì kênh đó im lặng không gửi được — nói ngay lúc lưu
            // còn hơn để người dùng chờ tới sáng mới biết. Kiểm ở đây, không phải lúc gửi.
            var missing = new List<string>();
            if (body.ChannelEmail && string.IsNullOrWhiteSpace(body.Email)) missing.Add("email nhận");
            if (body.ChannelTelegram && string.IsNullOrWhiteSpace(body.TelegramChatId)) missing.Add("chat id Telegram");
            if (body.ChannelZalo && string.IsNullOrWhiteSpace(body.ZaloPhone)) missing.Add("số điện thoại Zalo");
            if (body.Enabled && missing.Count > 0)
                return Results.BadRequest(new { error = $"Còn thiếu {string.Join(", ", missing)}." });

            // Số sai định dạng thì ZNS từ chối, mà lỗi đó chỉ hiện ở trang theo dõi của admin — người
            // đăng ký không thấy gì và cứ ngồi đợi. Chặn ngay lúc lưu, lúc họ còn đang nhìn màn hình.
            if (body.Enabled && body.ChannelZalo && !DigestPhone.IsValid(body.ZaloPhone))
                return Results.BadRequest(new
                {
                    error = "Số điện thoại Zalo không hợp lệ — nhập số Việt Nam 10 chữ số bắt đầu bằng 0 (vd 0912345678).",
                });

            await repo.UpsertAsync(new DigestSubscription(
                a.TenantId, a.Username, briefType,
                body.Enabled, DigestSubscription.ClampHour(body.SendHourLocal),
                // In-app là KHO LƯU luôn-bật (xem/nghe lại), không phải kênh tắt được → server ép
                // true, bỏ qua body.ChannelInApp. Client cũ gửi false cũng không tắt được.
                ChannelInApp: true, body.ChannelEmail, body.Email?.Trim(),
                body.ChannelTelegram, body.TelegramChatId?.Trim(),
                body.ChannelZalo, DigestPhone.Normalize(body.ZaloPhone),
                // Upsert CỐ Ý không đụng 2 mốc này (xem repo): sửa cấu hình giữa ngày KHÔNG được
                // làm bản tin gửi lại lần nữa.
                LastSentUtc: null, LastSentLocalDate: null), ct);

            // 1 dòng/người (PK TenantId+Username) — cấu trúc tự bảo đảm mỗi người 1 loại; đổi loại =
            // UPDATE cột BriefType trên chính dòng đó (giờ + kênh giữ nguyên).

            return Results.Json(new { ok = true }, Web);
        });

        // ─── Gửi thử — đi ĐÚNG đường của bản tin thật ────────────────────────────
        // Dựng dòng bằng CHÍNH DigestEnqueuePlanner rồi bỏ vào hàng đợi, y như workflow làm mỗi
        // sáng. Trước đây gửi thử có đường riêng (bộ phát + 3 lớp kênh trong proxy) — nghĩa là
        // "Gửi thử OK" KHÔNG chứng minh được bản tin thật gửi được, vì hai đường khác nhau. Nay
        // chung một đường: thử thành công là bằng chứng thật.
        //
        // Đổi lại, kết quả không còn tức thì: dòng nằm hàng đợi tới nhịp rút kế (~1 phút). Chấp
        // nhận, vì cái người dùng cần biết là "kênh của tôi có nhận được không", chứ không phải
        // "nhận được trong 2 giây".
        g.MapPost("/subscriptions/{briefType}/test", async (string briefType, HttpContext ctx,
            TkSessionStore sessions, DigestSubscriptionRepository repo, MailQueueRepository queue,
            InsightRepository insights, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!BriefTypes.IsValid(briefType))
                return Results.BadRequest(new { error = "Loại bản tin không hợp lệ." });

            var sub = (await repo.ListForUserAsync(a.TenantId, a.Username, ct))
                .FirstOrDefault(s => s.BriefType == briefType);
            if (sub == null)
                return Results.BadRequest(new { error = "Chưa lưu đăng ký — bấm Lưu trước khi Gửi thử." });

            var nowVn = DigestDue.NowVn(DateTime.UtcNow);
            var body = "Đây là bản tin **THỬ** để kiểm tra kênh nhận. "
                     + "Đọc được tin này nghĩa là kênh đó hoạt động tốt.";
            var msg = new DigestMessage($"[Gửi thử] Bản tin {nowVn:dd/MM HH:mm}",
                body, SaleBriefBuilder.ToHtml(body), briefType);

            // Bước 1 — ghi Bảng tin: in-app là kho lưu luôn-bật nên bản thử cũng phải thấy được ở
            // đó (người dùng kiểm tra cả nút "Nghe" ngay trên bản thử).
            var insightId = await insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: sub.TenantId, Username: sub.Username,
                Kind: briefType, Severity: 0, Title: msg.Title, Body: msg.BodyMarkdown,
                DataJson: null, AlertKey: null, IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

            // Bước 2 — xếp hàng đợi từng kênh ngoài đang bật. ScheduledUtc = NGAY (khác bản tin
            // thật hẹn theo giờ người chọn) để rút ở nhịp kế thay vì đợi tới sáng.
            // CỐ Ý không đụng mốc "đã chuẩn bị hôm nay": gửi thử mà tính là đã gửi thì sáng mai
            // bản tin thật bị bỏ.
            var rows = DigestEnqueuePlanner.BuildRows(
                sub, insightId ?? 0, msg, DateTime.UtcNow, DigestDue.NowVn(DateTime.UtcNow).ToString("dd/MM/yyyy"));
            foreach (var r in rows) await queue.EnqueueAsync(r, ct);

            var channels = new List<string>(rows.Count + 1);
            if (insightId != null) channels.Add("trong app");
            channels.AddRange(rows.Select(r => OutboundChannels.Describe(r.Channel)));

            return Results.Json(new
            {
                ok = channels.Count > 0,
                queued = rows.Count,
                sentChannels = string.Join("+", channels),
                // Nói thẳng là chưa tới ngay, kẻo người dùng mở Zalo không thấy gì rồi tưởng hỏng.
                summary = rows.Count == 0
                    ? "Đã lưu vào Bảng tin. Bạn chưa bật kênh ngoài nào nên không có gì được gửi đi."
                    : $"Đã xếp {rows.Count} kênh vào hàng đợi — tin sẽ tới trong khoảng 1 phút. "
                    + "Kênh nào hỏng sẽ hiện lý do ở trang theo dõi hàng đợi.",
            }, Web);
        });

        // ─── Tự tìm chat id Telegram ─────────────────────────────────────────────
        // Telegram không cho tra chat id theo tên; chỉ hiện ra khi người dùng nhắn cho bot trước.
        // Nên cách duy nhất là: đưa họ một mã, họ nhắn mã đó, mình quét getUpdates tìm mã.
        g.MapPost("/telegram/detect", async (HttpContext ctx, TkSessionStore sessions,
            IHttpClientFactory http, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            var token = cfg["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token))
                return Results.Json(new { error = "Hệ thống chưa cấu hình bot Telegram." }, statusCode: 503);

            // Mã gắn với PHIÊN, không phải tên đăng nhập: đoán được mã của người khác là gán được
            // chat id của mình vào đăng ký của họ.
            var code = "TK-" + a.SessionId[..6].ToUpperInvariant();
            var botName = cfg["Telegram:BotUsername"];

            try
            {
                var client = http.CreateClient();
                var resp = await client.GetStringAsync(
                    $"https://api.telegram.org/bot{token}/getUpdates?limit=100", ct);

                using var doc = JsonDocument.Parse(resp);
                string? chatId = null;
                if (doc.RootElement.TryGetProperty("result", out var updates)
                    && updates.ValueKind == JsonValueKind.Array)
                {
                    // Quét từ mới nhất về cũ: người dùng nhắn lại thì lấy lần gần nhất.
                    foreach (var u in updates.EnumerateArray().Reverse())
                    {
                        if (!u.TryGetProperty("message", out var m)) continue;
                        var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        if (!text.Contains(code, StringComparison.OrdinalIgnoreCase)) continue;
                        if (m.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idEl))
                        {
                            chatId = idEl.GetRawText();
                            break;
                        }
                    }
                }

                return chatId != null
                    ? Results.Json(new { chatId, code }, Web)
                    : Results.Json(new
                    {
                        chatId = (string?)null, code, botUsername = botName,
                        hint = $"Nhắn đúng \"{code}\" cho bot{(string.IsNullOrWhiteSpace(botName) ? "" : " @" + botName)} rồi bấm lại nút này.",
                    }, Web);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Không để lỗi mạng Telegram thành 500 — đây là tiện ích phụ, người dùng vẫn có thể
                // tự dán chat id vào ô nhập.
                return Results.Json(new
                {
                    chatId = (string?)null, code,
                    hint = "Không hỏi được Telegram lúc này — bạn có thể tự dán chat id vào ô bên cạnh.",
                    detail = ex.Message,
                }, statusCode: 502);
            }
        });

        // 3 endpoint /zalo-config đã GỠ (14/08): Zalo nay gửi bằng ZNS qua OA của bên cung cấp
        // dịch vụ, khai một lần ở config hệ thống — không công ty nào phải khai OA riêng nữa.
        // Bảng dbo.TenantChannelSettings vẫn giữ (worker dùng để lưu token ZNS xoay vòng).

        return routes;
    }
}
