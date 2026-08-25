using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflows;
using TourkitAiProxy.Domain.Digest;

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

    /// Body PUT "nơi nhận của tôi" — CHỈ kênh + địa chỉ, không kèm loại bản tin hay giờ nhận.
    public record ChannelsBody(bool ChannelEmail, string? Email,
        bool ChannelTelegram, string? TelegramChatId,
        bool ChannelZalo, string? ZaloPhone);

    /// Body PUT cấu hình Zalo của công ty. Bí mật để trống = giữ nguyên bản đang lưu.
    public record ZaloConfigBody(string? Mode, string? OaId, string? AppId,
        string? SecretKey, string? RefreshTokenSeed,
        Dictionary<string, string>? Templates);

    /// <summary>
    /// Những chức năng cần MẪU ZNS RIÊNG. Zalo duyệt mẫu theo nội dung, nên "bản tin sáng" và
    /// "nhắc thu tiền" là hai mẫu khác nhau — dùng chung một mã thì Zalo từ chối, hoặc gửi được
    /// nhưng nội dung nói sai chuyện.
    /// <para>Thêm chức năng gửi Zalo mới = thêm 1 dòng ở đây; giao diện tự mọc thêm ô nhập.</para>
    /// </summary>
    public static readonly string[] ZaloTemplateFeatures =
    {
        BriefTypes.Sale, BriefTypes.Ceo, "payment-alert",
    };

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

        // ─── Nơi nhận của tôi (dùng chung cho MỌI loại thông báo) ────────────────
        //
        // Tách khỏi PUT /subscriptions/{briefType} có chủ đích: địa chỉ nhận là hồ sơ CỦA NGƯỜI,
        // khai một lần rồi bản tin sáng lẫn cảnh báo đều dùng. Gộp vào endpoint đăng ký thì mỗi
        // lần đổi email lại phải gửi kèm loại bản tin + giờ nhận — client quên một trường là tự
        // tay tắt đăng ký của chính người đó mà không báo gì.
        g.MapPut("/my-channels", async (ChannelsBody body, HttpContext ctx, TkSessionStore sessions,
            DigestSubscriptionRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Bật kênh mà bỏ trống nơi nhận → kênh đó im lặng không gửi được. Nói ngay lúc lưu,
            // lúc người dùng còn đang nhìn màn hình, chứ không để tới sáng mai mới lộ.
            var missing = new List<string>();
            if (body.ChannelEmail && string.IsNullOrWhiteSpace(body.Email)) missing.Add("email nhận");
            if (body.ChannelTelegram && string.IsNullOrWhiteSpace(body.TelegramChatId)) missing.Add("chat id Telegram");
            if (body.ChannelZalo && string.IsNullOrWhiteSpace(body.ZaloPhone)) missing.Add("số điện thoại Zalo");
            if (missing.Count > 0)
                return Results.BadRequest(new { error = $"Còn thiếu {string.Join(", ", missing)}." });

            if (body.ChannelZalo && !DigestPhone.IsValid(body.ZaloPhone))
                return Results.BadRequest(new
                {
                    error = "Số điện thoại Zalo không hợp lệ — nhập số Việt Nam 10 chữ số bắt đầu bằng 0 (vd 0912345678).",
                });

            await repo.UpdateChannelsAsync(a.TenantId, a.Username,
                body.ChannelEmail, body.Email?.Trim(),
                body.ChannelTelegram, body.TelegramChatId?.Trim(),
                body.ChannelZalo, DigestPhone.Normalize(body.ZaloPhone), ct);

            return Results.Json(new { ok = true }, Web);
        });

        // ─── Lưu / cập nhật đăng ký ──────────────────────────────────────────────
        g.MapPut("/subscriptions/{briefType}", async (string briefType, SubBody body,
            HttpContext ctx, TkSessionStore sessions, DigestSubscriptionRepository repo,
            WorkflowRepository workflows, WorkflowRegistry registry, ILoggerFactory logs,
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

            // ── Chưa ai khai luật chung → KHÔNG cho bật nhận ────────────────────────
            //
            // Cho bật lúc này thì bản tin sẽ chạy bằng mặc định mà không ai từng xem qua: nhắc theo
            // trạng thái đoán mò, ngưỡng đoán mò. Thà chặn ngay và chỉ đúng chỗ cần khai, còn hơn
            // gửi một bản tin sai rồi mất lòng tin ngay buổi sáng đầu tiên.
            //
            // ⚠️ Kiểm TRƯỚC khi lưu. Bản đầu đặt sau Upsert nên trả 409 mà dòng đăng ký VẪN được ghi
            // — người dùng thấy báo lỗi nhưng thực tế đã đăng ký rồi (E2E bắt được: bước 6 FAIL còn
            // 6b lại PASS vì dòng đã đổi loại). Từ chối thì phải từ chối trọn vẹn.
            //
            // Workflow không có luật nào để khai (HasCompanyRules=false, vd ceo-brief) thì bỏ qua
            // kiểm này — chặn người dùng vì một thứ không tồn tại là chặn vào hư không.
            var wfDef = registry.Resolve(briefType);
            if (body.Enabled && wfDef is { HasCompanyRules: true })
            {
                var cfg0 = workflows.Get(a.TenantId, "", briefType);
                if (cfg0 is null || string.IsNullOrWhiteSpace(cfg0.OptionsJson) || cfg0.OptionsJson == "{}")
                    return Results.Json(new
                    {
                        error = "Công ty chưa cấu hình bản tin này. Nhờ người phụ trách vào Tự động hoá → "
                              + "mục \"Theo tổ chức\" khai các mục cần đưa vào bản tin rồi bấm Lưu cấu hình.",
                        needsCompanySetup = true,
                    }, Web, statusCode: 409);
            }

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

            // ── Bật nhận là ĐỦ: tự bật luôn lịch chạy của công ty ────────────────────
            //
            // Bản tin cần hai công tắc — đăng ký của người dùng VÀ lịch chạy cấp công ty (vì tác vụ
            // này là PerTenant: một lượt chạy phục vụ mọi người đăng ký). Bắt người dùng bật cả hai
            // là một cái bẫy: họ tick "Nhận bản tin này", thấy "Đã lưu", rồi sáng mai không có gì
            // tới — không lỗi nào hiện lên. Mà cũng không ai tỉnh táo lại muốn "có người đăng ký
            // nhưng lịch tắt": lượt chạy không có người đăng ký nào thì không tốn gì cả.
            //
            // Nên quy tắc là: công ty khai luật chung một lần, ai muốn nhận thì bật nhận — thế thôi.
            //
            // KHÔNG tự bật khi workflow đang bị tạm dừng do lỗi (PausedReason): dừng đó là dấu hiệu
            // hệ thống đang hỏng, một người bấm đăng ký không phải lý do để xoá dấu hiệu ấy đi.
            bool autoStarted = false;
            if (body.Enabled)
            {
                var cfg = workflows.Get(a.TenantId, "", briefType);
                if (cfg is null || (!cfg.Enabled && string.IsNullOrEmpty(cfg.PausedReason)))
                {
                    workflows.UpsertConfig(a.TenantId, "", briefType, enabled: true,
                        intervalMinutes: cfg?.IntervalMinutes ?? 15, updatedBy: a.Username);
                    autoStarted = true;
                    logs.CreateLogger("Digest").LogInformation(
                        "Tự bật lịch {Brief} cho tenant {Tenant} vì {User} đăng ký nhận", briefType, a.TenantId, a.Username);
                }
            }

            return Results.Json(new { ok = true, autoStarted }, Web);
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
            CancellationToken ct) =>
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

            // ⚠️ CỐ Ý KHÔNG ghi vào Bảng tin. Hai lý do, cái đầu là lỗi thật đã suýt lọt:
            //   1. Mốc chống trùng của bản tin thật đếm dòng trong dbo.AgentInsights
            //      (InsightRepository.ExistsTodayAsync). Bản thử mà ghi vào đó thì ai bấm "Gửi thử"
            //      buổi trưa sẽ MẤT bản tin thật sáng mai — workflow tưởng hôm nay chuẩn bị rồi.
            //   2. Bảng tin là nơi xem/nghe LẠI bản tin thật. Nhét bản thử vào làm bẩn lịch sử,
            //      người dùng phải bới giữa một đống "[Gửi thử]" mới thấy cái cần đọc.
            // Gửi thử là để thử KÊNH NGOÀI. Kênh trong app không cần thử — nó luôn bật và người
            // dùng thấy bản tin thật ở đó mỗi ngày.
            var rows = DigestEnqueuePlanner.BuildRows(
                sub, insightId: 0, msg, DateTime.UtcNow,
                DigestDue.NowVn(DateTime.UtcNow).ToString("dd/MM/yyyy"));

            if (rows.Count == 0)
                return Results.Json(new
                {
                    ok = false,
                    queued = 0,
                    sentChannels = "",
                    summary = "Bạn chưa bật kênh ngoài nào (email/Telegram/Zalo) nên không có gì để thử. "
                            + "Bản tin trong app thì luôn bật, không cần thử.",
                }, Web);

            // ScheduledUtc = NGAY (khác bản tin thật hẹn theo giờ người chọn) để rút ở nhịp kế.
            foreach (var r in rows) await queue.EnqueueAsync(r, ct);

            return Results.Json(new
            {
                ok = true,
                queued = rows.Count,
                sentChannels = string.Join("+", rows.Select(r => OutboundChannels.Describe(r.Channel))),
                // Nói thẳng là chưa tới ngay, kẻo người dùng mở Zalo không thấy gì rồi tưởng hỏng.
                summary = $"Đã xếp {rows.Count} kênh vào hàng đợi — tin sẽ tới trong khoảng 1 phút. "
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

        // ─── Zalo OA của công ty (17/08 — khôi phục lại per-tenant) ──────────────
        //
        // Bản 14/08 gỡ nhóm endpoint này để dùng OA chung. Đi gặp khách hàng 17/08 thì KHÔNG công ty
        // nào chịu: tin ZNS hiện tên OA người gửi, dùng OA của bên cung cấp dịch vụ nghĩa là khách
        // của họ thấy tên một công ty khác. Nên OA riêng là đường CHÍNH; ai dùng OA của bên cung cấp
        // thì nhập khoá được cấp — không có trạng thái "bỏ trống rồi hệ thống tự lo".
        //
        // Gác CH_HT_XEM: đây là bí mật cấp công ty do proxy tự giữ, TourKit không biết gì để lọc hộ.

        g.MapGet("/zalo-config", async (HttpContext ctx, TkSessionStore sessions,
            TenantChannelSettingsStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await CanConfigSystemAsync(a.SessionId, sessions, ct)) return Forbidden();

            var cfg = await store.GetZaloAsync(a.TenantId, ct);
            // KHÔNG trả bí mật ra client — chỉ nói đã khai hay chưa, giống cách tài khoản dịch vụ làm.
            return Results.Json(new
            {
                configured = cfg is { IsUsable: true },
                mode = cfg?.Mode ?? TenantChannelSettingsStore.ModeOwnOa,
                oaId = cfg?.OaId,
                appId = cfg?.AppId,
                hasSecret = !string.IsNullOrWhiteSpace(cfg?.SecretKey),
                hasRefreshToken = !string.IsNullOrWhiteSpace(cfg?.RefreshTokenSeed),
                templates = cfg?.Templates ?? new Dictionary<string, string>(),
                // Danh sách chức năng cần mẫu ZNS riêng — giao diện vẽ ô theo cái này, khỏi hard-code.
                features = ZaloTemplateFeatures,
            }, Web);
        });

        g.MapPut("/zalo-config", async (ZaloConfigBody body, HttpContext ctx, TkSessionStore sessions,
            TenantChannelSettingsStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await CanConfigSystemAsync(a.SessionId, sessions, ct)) return Forbidden();

            // `Mode` là DI SẢN — giao diện KHÔNG còn hỏi nữa (bỏ 18/08). Cả hai chế độ luôn đòi
            // đúng bộ oaId+appId+secretKey+refreshTokenSeed như nhau, và không dòng code nào ở
            // proxy lẫn worker rẽ nhánh theo nó — nó chỉ được lưu rồi đọc ra để in chữ hướng dẫn.
            // Giữ lại tham số cho client cũ; body không gửi thì rơi về 'own', vô hại vì không ai đọc.
            var mode = body.Mode == TenantChannelSettingsStore.ModeProvided
                ? TenantChannelSettingsStore.ModeProvided
                : TenantChannelSettingsStore.ModeOwnOa;

            // Bí mật để trống = GIỮ NGUYÊN bản đang lưu (giao diện không đọc lại được nên không gửi
            // lại được). Vì thế phải kiểm trên trạng thái SAU khi hợp nhất, không kiểm trên body.
            var current = await store.GetZaloAsync(a.TenantId, ct);
            var willHaveSecret = !string.IsNullOrWhiteSpace(body.SecretKey)
                              || !string.IsNullOrWhiteSpace(current?.SecretKey);
            var willHaveRefresh = !string.IsNullOrWhiteSpace(body.RefreshTokenSeed)
                              || !string.IsNullOrWhiteSpace(current?.RefreshTokenSeed);

            // CẢ HAI chế độ đòi cùng một bộ: dùng OA của bên cung cấp thì họ đưa sẵn giá trị để
            // công ty dán vào đúng những ô này, chứ không phải "khỏi nhập gì".
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(body.OaId)) missing.Add("OA ID");
            if (string.IsNullOrWhiteSpace(body.AppId)) missing.Add("App ID");
            if (!willHaveSecret) missing.Add("Secret key");
            // App ID + Secret KHÔNG đủ để lấy token: Zalo đổi refresh token lấy access token, và
            // refresh token đầu tiên chỉ có sau bước cấp quyền OA. Thiếu ô này thì công ty khai
            // xong tưởng chạy được, mà worker không bao giờ lấy nổi token.
            if (!willHaveRefresh) missing.Add("Refresh token lần đầu");

            if (missing.Count > 0)
                return Results.BadRequest(new
                {
                    error = $"Còn thiếu {string.Join(", ", missing)}. Chưa khai đủ thì tin Zalo không gửi được — "
                          + "hệ thống KHÔNG tự gửi bằng OA của đơn vị khác.",
                });

            var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (body.Templates != null)
                foreach (var kv in body.Templates)
                    // Chỉ nhận chức năng có trong danh sách: khoá lạ lưu vào thì không worker nào đọc,
                    // người khai tưởng đã xong mà thực ra mẫu đó không bao giờ được dùng.
                    if (ZaloTemplateFeatures.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                        templates[kv.Key] = kv.Value ?? "";

            await store.SaveZaloAsync(a.TenantId, mode, body.OaId, body.AppId,
                body.SecretKey, body.RefreshTokenSeed, templates, ct);

            return Results.Json(new { ok = true }, Web);
        });

        g.MapDelete("/zalo-config", async (HttpContext ctx, TkSessionStore sessions,
            TenantChannelSettingsStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await CanConfigSystemAsync(a.SessionId, sessions, ct)) return Forbidden();

            var removed = await store.DeleteZaloAsync(a.TenantId, ct);
            return Results.Json(new { ok = true, removed }, Web);
        });

        return routes;
    }

    /// Gác quyền cấu hình hệ thống — 1 nguồn ở SessionAuth (xem ghi chú ở đó).
    private static Task<bool> CanConfigSystemAsync(string sid, TkSessionStore sessions, CancellationToken ct)
        => SessionAuth.CanConfigSystemAsync(sid, sessions, ct);

    private static IResult Forbidden() => SessionAuth.ForbiddenConfigSystem();
}
