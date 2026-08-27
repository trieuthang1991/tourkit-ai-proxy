// Services/Chat/Channels/TelegramChatAdapter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Telegram.
///
/// <para><b>Mỗi bot MỘT đường webhook riêng</b> — đây là điểm khác Zalo/Messenger. Telegram không
/// gửi kèm bất kỳ thông tin nào trong THÂN tin cho biết "tin này của bot nào"; định danh DUY NHẤT
/// nằm ở chính đường dẫn Telegram gọi tới (đường đó gắn với token lúc gọi <c>setWebhook</c>). Nên
/// một công ty muốn nhiều bot (vd một bot chăm khách lẻ, một bot cho đại lý) thì mỗi bot phải có
/// một mã tài khoản riêng trên URL — không gộp chung một đường như Zalo/Messenger được.</para>
///
/// <para><b>Không ký chữ ký.</b> Telegram xác thực bằng một chuỗi bí mật khai lúc đăng ký địa chỉ
/// webhook, gửi lại trong header <c>X-Telegram-Bot-Api-Secret-Token</c>. Không khai chuỗi đó thì
/// <b>ai biết địa chỉ webhook cũng bơm tin vào được</b> — nên ở đây bắt buộc phải có.</para>
///
/// <para>Bot token có thể lấy từ khoá riêng của tài khoản, hoặc rơi về <c>Telegram:BotToken</c>
/// dùng chung — CHỈ khi công ty chưa khai tài khoản nào (tương thích ngược với bản một-bot cũ).</para>
///
/// <para><b>Telegram KHÔNG báo đã nhận / đã xem.</b> Bot API không cung cấp — khác hẳn Zalo
/// (<c>user_seen_message</c>) và Messenger (<c>delivery</c>/<c>read</c>). Nên tin gửi qua kênh này
/// dừng ở "đã gửi" vĩnh viễn, và <b>đó là đúng</b>. Đừng "sửa" bằng cách tự đặt trạng thái cao hơn
/// khi gửi thành công: như thế là nói dối nhân viên rằng khách đã nhận trong khi mình không biết.
/// Giao diện nói rõ chuyện này ở tooltip dấu tích (xem <c>DauGui</c> trong chat-inbox.jsx).</para>
/// </summary>
public class TelegramChatAdapter : IChatChannelAdapter
{
    private const string ApiBase = "https://api.telegram.org";

    private readonly IHttpClientFactory _http;
    private readonly ChannelCredentialStore _cred;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TelegramChatAdapter> _log;

    public TelegramChatAdapter(IHttpClientFactory http, ChannelCredentialStore cred,
        IConfiguration cfg, ILogger<TelegramChatAdapter> log)
    { _http = http; _cred = cred; _cfg = cfg; _log = log; }

    public ChatChannel Channel => ChatChannel.Telegram;

    // ── Nối bot bằng MỘT nút ────────────────────────────────────────────────

    /// <summary>Kết quả nối một bot.</summary>
    /// <param name="ChuoiBiMat">Chuỗi bí mật webhook do MÁY CHỦ sinh — phải lưu lại, vì đó là thứ
    /// DUY NHẤT dùng để kiểm tin đến có thật hay không.</param>
    public record ConnectBotResult(bool Ok, string? BotId, string? Username, string? ChuoiBiMat, string? Loi);

    /// <summary>
    /// Sinh chuỗi bí mật webhook. <b>Máy chủ sinh, không nhận từ người dùng</b> — trước đây ô này
    /// bắt người khai tự nghĩ ra rồi tự dán vào lệnh <c>setWebhook</c>, tức là đã tự tay làm cái
    /// việc mà nút này sinh ra để bỏ đi.
    ///
    /// <para>Telegram chỉ nhận <c>A-Z a-z 0-9 _ -</c> cho <c>secret_token</c>, nên phải dùng
    /// base64url chứ không phải base64 thường: một dấu <c>+</c> hay <c>/</c> lọt vào là Telegram
    /// từ chối, mà lời từ chối của họ không nói vướng ở đâu.</para>
    /// </summary>
    public static string NewWebhookSecret()
    {
        Span<byte> b = stackalloc byte[24];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Nối một bot: xác thực token → sinh chuỗi bí mật → đăng ký địa chỉ webhook.
    ///
    /// <para>Trước 27/08 ba việc này là việc TAY của người khai: tự nghĩ chuỗi bí mật, copy URL
    /// trên màn hình, rồi tự gõ lệnh <c>setWebhook</c> ngoài trình duyệt. Không công ty du lịch
    /// nào làm nổi — đúng lý do đã phải đổi cách nối của Zalo.</para>
    /// </summary>
    public async Task<ConnectBotResult> ConnectBotAsync(string? botToken, string webhookUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            return new(false, null, null, null, "Chưa dán bot token lấy từ @BotFather");

        // ⚠️ Xác thực token TRƯỚC khi đăng ký. Làm ngược thứ tự thì token sai vẫn kịp trỏ một địa
        // chỉ công khai vào một bot không tồn tại, và bản ghi rác nằm lại trong danh sách kênh.
        var me = await CallJsonAsync(botToken!, "getMe", null, ct);
        if (me is null || me["ok"]?.GetValue<bool>() != true)
            return new(false, null, null, null,
                "Bot token không đúng, hoặc Telegram không trả lời. Kiểm tra lại chuỗi @BotFather đưa.");

        var botId = me["result"]?["id"]?.ToString();
        var username = me["result"]?["username"]?.ToString();
        var biMat = NewWebhookSecret();

        var kq = await CallJsonAsync(botToken!, "setWebhook", new JsonObject
        {
            ["url"] = webhookUrl,
            // Telegram không ký nội dung; chuỗi này là thứ duy nhất ngăn người ngoài bơm tin giả.
            ["secret_token"] = biMat,
            // ⚠️ HAI CHỖ, THIẾU MỘT LÀ HỎNG IM LẶNG. Telegram CHỈ gửi những loại nằm trong danh
            // sách này, và danh sách MẶC ĐỊNH của họ đã bỏ sẵn message_reaction. Viết mã bóc cảm
            // xúc mà quên khai ở đây thì không bao giờ có gói tin nào tới: không lỗi, không log,
            // chỉ là một thứ không bao giờ xảy ra.
            ["allowed_updates"] = new JsonArray(
                "message", "edited_message", "callback_query", "message_reaction", "my_chat_member"),
            // Bỏ gói tồn đọng (Telegram giữ tối đa 24h). Nhận vào thì bot trả lời những câu hỏi
            // từ hôm qua như thể khách vừa hỏi — và có thể là tin của một hệ thống khác đang dùng
            // chính bot này trước đó.
            ["drop_pending_updates"] = true,
        }, ct);

        if (kq is null || kq["ok"]?.GetValue<bool>() != true)
            return new(false, botId, username, null,
                "Telegram từ chối đăng ký địa chỉ nhận tin: "
                + (kq?["description"]?.ToString() ?? "không rõ lý do")
                + ". Địa chỉ phải là https công khai — lúc chạy ở máy dev thì phải khai Chat:PublicBaseUrl.");

        _log.LogInformation("[chat/telegram] đã nối bot @{Ten} ({Id})", username, botId);
        return new(true, botId, username, biMat, null);
    }

    /// <summary>
    /// Gỡ địa chỉ nhận tin của bot. Không gọi thì Telegram <b>nện vào URL cũ mãi mãi</b>: mỗi lượt
    /// một dòng từ chối trong nhật ký, và bot vẫn tưởng nó đang được dùng.
    ///
    /// <para><c>drop_pending_updates=false</c>: gỡ khỏi hộp thư này không có nghĩa là vứt tin của
    /// khách — công ty có thể đang chuyển bot sang nơi khác dùng tiếp.</para>
    /// </summary>
    public async Task<bool> DisconnectBotAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return false;
        var kq = await CallJsonAsync(token, "deleteWebhook",
            new JsonObject { ["drop_pending_updates"] = false }, ct);
        return kq?["ok"]?.GetValue<bool>() == true;
    }

    /// <summary>Gọi một phương thức Bot API, trả JSON thô. Không ném — chỗ gọi tự đọc <c>ok</c>.</summary>
    /// <remarks>Token nằm TRONG đường dẫn nên tuyệt đối không ghi URL ra nhật ký.</remarks>
    private async Task<JsonObject?> CallJsonAsync(string token, string phuongThuc, JsonObject? than,
        CancellationToken ct)
    {
        try
        {
            var http = _http.CreateClient();
            using var res = than is null
                ? await http.GetAsync($"{ApiBase}/bot{token}/{phuongThuc}", ct)
                : await http.PostAsync($"{ApiBase}/bot{token}/{phuongThuc}",
                    new StringContent(than.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/telegram] gọi {PhuongThuc} hỏng", phuongThuc);
            return null;
        }
    }

    private async Task<string?> TokenAsync(string tenantId, string accountId, CancellationToken ct)
    {
        var c = await _cred.GetAsync(tenantId, Channel, accountId, ct);
        if (c is not null && c.TryGetValue("botToken", out var rieng) && !string.IsNullOrWhiteSpace(rieng))
            return rieng;
        var chung = _cfg["Telegram:BotToken"];
        return string.IsNullOrWhiteSpace(chung) ? null : chung;
    }

    public async Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct)
    {
        // Telegram BẮT BUỘC có mã tài khoản trên URL — xem docstring lớp. Thiếu là lỗi định tuyến,
        // không phải chuyện xác thực.
        if (string.IsNullOrWhiteSpace(accountIdTuUrl)) return null;

        var c = await _cred.GetAsync(tenantId, Channel, accountIdTuUrl, ct);
        if (c is null || !c.TryGetValue("webhookSecret", out var mong) || string.IsNullOrWhiteSpace(mong))
        {
            // KHÔNG cho qua khi thiếu chuỗi bí mật. Telegram không ký nội dung, nên chuỗi này là
            // thứ DUY NHẤT ngăn người ngoài bơm tin giả vào hộp thư.
            _log.LogWarning("[chat/telegram] tenant={T} account={A} chưa khai webhookSecret — bỏ webhook",
                tenantId, accountIdTuUrl);
            return null;
        }

        var gui = headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(gui)) return null;

        var a = Encoding.UTF8.GetBytes(mong);
        var b = Encoding.UTF8.GetBytes(gui);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? accountIdTuUrl : null;
    }

    public IReadOnlyList<InboundChatEvent> Parse(string rawBody)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        // Khách bấm nút. Telegram gói RIÊNG, không nằm trong "message" — đọc sót là nút bấm rơi
        // vào hư không và khách nhìn thấy nút quay vòng rồi báo lỗi.
        if (goc?["callback_query"] is { } bam) return ReadButtonClick(bam);

        // Cảm xúc khách thả. Cũng là gói riêng, và gắn vào MỘT tin đã có chứ không phải tin mới.
        if (goc?["message_reaction"] is { } camXuc) return ReadReaction(camXuc);

        // edited_message: khách sửa lại tin đã gửi. Coi như tin mới — id khác nên không trùng, và
        // nội dung sửa thường là ý họ thật sự muốn nói.
        var msg = goc?["message"] ?? goc?["edited_message"];
        if (msg is null) return ra;

        var chatId = msg["chat"]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(chatId)) return ra;

        // ⚠️ Telegram gói MỖI loại đính kèm vào MỘT trường tên khác nhau — không có trường chung
        // nào cho biết "tin này có tệp". Thiếu một nhánh là loại đó rơi xuống ChatKind.Text với nội
        // dung null: một dòng TRẮNG trong hộp thư, không lỗi, không log. Đã dính thật với `video`
        // và `audio` (đối chiếu ChatbotX 27/08 mới lộ ra).
        var loai = ChatKind.Text;
        string? att = null;
        if (msg["photo"] is JsonArray p && p.Count > 0) { loai = ChatKind.Image; att = p.ToJsonString(); }
        else if (msg["video"] is JsonNode vid) { loai = ChatKind.File; att = vid.ToJsonString(); }
        // video_note = ô video tròn (bấm giữ quay). Trường riêng, không nằm trong `video`.
        else if (msg["video_note"] is JsonNode vn) { loai = ChatKind.File; att = vn.ToJsonString(); }
        // `audio` (tệp nhạc/ghi âm đính kèm) KHÁC `voice` (bấm giữ nói) nhưng cùng là âm thanh:
        // xếp nhầm sang "tệp" thì nhân viên phải tải về mới biết có nghe được không.
        else if (msg["audio"] is JsonNode au) { loai = ChatKind.Audio; att = au.ToJsonString(); }
        else if (msg["document"] is JsonNode d) { loai = ChatKind.File; att = d.ToJsonString(); }
        else if (msg["voice"] is JsonNode v) { loai = ChatKind.Audio; att = v.ToJsonString(); }
        else if (msg["location"] is JsonNode l) { loai = ChatKind.Location; att = l.ToJsonString(); }
        else if (msg["sticker"] is JsonNode s) { loai = ChatKind.Sticker; att = s.ToJsonString(); }

        var ten = string.Join(' ', new[] { msg["from"]?["first_name"]?.ToString(),
                                           msg["from"]?["last_name"]?.ToString() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        var chu = msg["text"]?.ToString() ?? msg["caption"]?.ToString();

        // Telegram còn hàng chục loại nữa (poll, dice, game, invoice, hoá đơn…). Không nhận ra thì
        // BỎ QUA chứ đừng ghi một dòng trắng: dòng trắng vẫn đẩy hội thoại lên đầu danh sách và
        // vẫn tính là chưa đọc, nhân viên mở ra không thấy gì. Ghi log để còn biết mà bổ sung —
        // đây là chỗ DUY NHẤT nhìn ra "khách có gửi mà hộp thư không hiện".
        if (att is null && string.IsNullOrWhiteSpace(chu))
        {
            _log.LogWarning("[chat/telegram] loại tin chưa hỗ trợ, bỏ qua. Các trường trong gói: {Truong}",
                string.Join(", ", msg.AsObject().Select(x => x.Key)));
            return ra;
        }

        var luc = long.TryParse(msg["date"]?.ToString(), out var d2)
            ? DateTimeOffset.FromUnixTimeSeconds(d2).UtcDateTime : DateTime.UtcNow;

        // Khách đến từ đâu. Telegram KHÔNG có trường riêng cho việc này như Meta: cách duy nhất
        // là tham số trên liên kết t.me/<bot>?start=<tham số>, và nó tới đúng MỘT LẦN, đội lốt
        // một câu tin bình thường. Không tách ra thì hộp thư có một câu "/start fb_ads_hue" vô
        // nghĩa, còn dữ liệu bán hàng thì mất vĩnh viễn — không API nào tra ngược được.
        ChatReferral? tuDau = null;
        if (chu is not null && chu.StartsWith("/start", StringComparison.Ordinal))
        {
            var thamSo = chu.Length > "/start".Length ? chu["/start".Length..].Trim() : "";
            if (thamSo.Length > 0)
            {
                tuDau = new("DEEPLINK", thamSo, null);
                // Bỏ hẳn phần chữ VÀ mã tin: gói này chỉ mang nguồn, không phải câu khách nói.
                // Còn mã tin thì lõi coi đây là một tin thật và ghi một dòng trắng vào hội thoại.
                chu = null;
            }
            // "/start" trơn (bấm nút Bắt đầu trong chính Telegram) KHÔNG có nguồn nào — ghi bừa
            // một nguồn rỗng là làm bẩn báo cáo "khách đến từ đâu".
        }

        if (tuDau is not null)
        {
            ra.Add(new(ChatChannel.Telegram, chatId!, null, ChatKind.Text, null, null, luc,
                DisplayName: string.IsNullOrWhiteSpace(ten) ? msg["from"]?["username"]?.ToString() : ten,
                Referral: tuDau));
            return ra;
        }

        ra.Add(new(ChatChannel.Telegram, chatId!,
            // Telegram đánh số tin theo từng cuộc trò chuyện, không phải toàn cục — phải ghép
            // chat id vào, không thì hai khách khác nhau đụng cùng một số và tin sau bị coi là trùng.
            $"{chatId}:{msg["message_id"]}",
            loai, chu, att, luc,
            DisplayName: string.IsNullOrWhiteSpace(ten) ? msg["from"]?["username"]?.ToString() : ten));
        return ra;
    }

    /// <summary>
    /// Khách bấm nút bàn phím gắn dưới tin (<c>callback_query</c>).
    ///
    /// <para>Ghi lại bằng <b>CHỮ TRÊN NÚT</b> chứ không phải <c>callback_data</c>: nhân viên đọc
    /// lại hội thoại phải thấy đúng thứ khách nhìn thấy, không phải một chuỗi mã như
    /// "MENU_TOUR_DN". Tin cũ không kèm bàn phím thì đành lùi về mã nút — vẫn hơn dòng trống.</para>
    /// </summary>
    private static List<InboundChatEvent> ReadButtonClick(JsonNode bam)
    {
        var ra = new List<InboundChatEvent>();
        var tin = bam["message"];
        var chatId = tin?["chat"]?["id"]?.ToString() ?? bam["from"]?["id"]?.ToString();
        var maBam = bam["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(maBam)) return ra;

        var maNut = bam["data"]?.ToString();
        var ten = string.Join(' ', new[] { bam["from"]?["first_name"]?.ToString(),
                                           bam["from"]?["last_name"]?.ToString() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        ra.Add(new(ChatChannel.Telegram, chatId!,
            // Mã riêng cho lượt bấm: dùng lại mã tin gốc thì lượt bấm bị coi là trùng với chính
            // tin mang nút, và biến mất.
            $"{chatId}:cb:{maBam}",
            ChatKind.Text, ButtonLabel(tin?["reply_markup"], maNut) ?? maNut, null,
            // ⚠️ Thời điểm là BÂY GIỜ, không phải `date` của tin mang nút — tin đó có thể gửi từ
            // hôm qua, lấy nhầm là lượt bấm nằm ngược dòng thời gian và không ai thấy nó.
            DateTime.UtcNow,
            DisplayName: string.IsNullOrWhiteSpace(ten) ? bam["from"]?["username"]?.ToString() : ten,
            ButtonClickId: maBam));
        return ra;
    }

    /// <summary>Tìm chữ hiện trên nút theo <c>callback_data</c> khách vừa bấm.</summary>
    private static string? ButtonLabel(JsonNode? banPhim, string? maNut)
    {
        if (banPhim?["inline_keyboard"] is not JsonArray hang || string.IsNullOrWhiteSpace(maNut))
            return null;
        foreach (var h in hang.OfType<JsonArray>())
            foreach (var nut in h.OfType<JsonNode>())
                if (nut["callback_data"]?.ToString() == maNut)
                    return nut["text"]?.ToString();
        return null;
    }

    /// <summary>
    /// Khách thả (hoặc gỡ) cảm xúc lên một tin đã có.
    ///
    /// <para>⚠️ <b>Telegram gửi TRẠNG THÁI MỚI, không gửi "thêm" hay "bớt".</b> Gỡ cảm xúc là một
    /// gói có <c>new_reaction</c> RỖNG — khác hẳn Meta vốn nói thẳng <c>action="unreact"</c>. Đọc
    /// nhầm là cảm xúc đã gỡ vẫn hiện mãi trên màn hình.</para>
    ///
    /// <para>Gói này chỉ tới khi đã khai <c>message_reaction</c> trong <c>allowed_updates</c> lúc
    /// đăng ký webhook — danh sách mặc định của Telegram KHÔNG có nó.</para>
    /// </summary>
    private static List<InboundChatEvent> ReadReaction(JsonNode camXuc)
    {
        var ra = new List<InboundChatEvent>();
        var chatId = camXuc["chat"]?["id"]?.ToString();
        var maTin = camXuc["message_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(maTin)) return ra;

        var moi = camXuc["new_reaction"] as JsonArray;
        var dau = moi is { Count: > 0 } ? moi[0] : null;

        var luc = long.TryParse(camXuc["date"]?.ToString(), out var giay)
            ? DateTimeOffset.FromUnixTimeSeconds(giay).UtcDateTime : DateTime.UtcNow;

        ra.Add(new(ChatChannel.Telegram, chatId!, null, ChatKind.Text, null, null, luc,
            // Mã tin phải ghép chat id, y như lúc ghi tin — không thì không khớp được với tin nào.
            Reaction: new($"{chatId}:{maTin}", dau?["emoji"]?.ToString(),
                dau?["custom_emoji_id"]?.ToString(), dau is null)));
        return ra;
    }

    // ── Năng lực kênh ───────────────────────────────────────────────────────

    /// <summary>
    /// Ba chấm "đang gõ" bên phía khách. Telegram tự tắt sau 5 giây hoặc khi tin tới, nên không
    /// có (và không cần) lượt tắt.
    /// </summary>
    public async Task SendTypingAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return;
        await CallJsonAsync(token, "sendChatAction",
            new JsonObject { ["chat_id"] = externalUserId, ["action"] = "typing" }, ct);
    }

    /// <inheritdoc />
    public async Task AckButtonClickAsync(string tenantId, string accountId, string maBamNut,
        CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return;
        await CallJsonAsync(token, "answerCallbackQuery",
            new JsonObject { ["callback_query_id"] = maBamNut }, ct);
    }

    /// <summary>
    /// Tên + ảnh đại diện khách.
    ///
    /// <para>Tên đã có sẵn trong gói tin, nên hàm này chạy chủ yếu vì <b>ảnh</b> — thứ Telegram
    /// không bao giờ gửi kèm. Phải đi hai lượt: <c>getUserProfilePhotos</c> ra mã tệp, rồi đổi mã
    /// tệp thành đường xem.</para>
    ///
    /// <para>⚠️ <b>Đường tải thật của Telegram chứa BOT TOKEN</b> (<c>/file/bot&lt;token&gt;/…</c>).
    /// Lưu thẳng chuỗi đó làm ảnh đại diện là phát bot token cho mọi trình duyệt mở hộp thư — ai
    /// cầm được nó thì đọc và trả lời được toàn bộ tin của công ty. Nên ở đây chỉ lưu một đường
    /// TƯƠNG ĐỐI trỏ về máy chủ mình; máy chủ mới là nơi cầm token.</para>
    /// </summary>
    public async Task<ContactProfile?> ContactProfileAsync(string tenantId, string accountId,
        string externalUserId, CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return null;

        var hs = await CallJsonAsync(token,
            $"getChat?chat_id={Uri.EscapeDataString(externalUserId)}", null, ct);
        var chat = hs?["result"];
        var ten = string.Join(' ', new[] { chat?["first_name"]?.ToString(), chat?["last_name"]?.ToString() }
            .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(ten)) ten = chat?["username"]?.ToString() ?? "";

        var anh = await CallJsonAsync(token,
            $"getUserProfilePhotos?user_id={Uri.EscapeDataString(externalUserId)}&limit=1", null, ct);
        // photos[0] = ảnh mới nhất, các phần tử bên trong là các cỡ (nhỏ trước). Ảnh đại diện chỉ
        // hiện cỡ 32px nên lấy cỡ NHỎ NHẤT là đúng — khác hẳn ảnh khách gửi (lấy cỡ lớn nhất để
        // còn soi được chữ trên hoá đơn/hộ chiếu).
        var maTep = anh?["result"]?["photos"]?[0]?[0]?["file_id"]?.ToString();

        var duongAnh = string.IsNullOrWhiteSpace(maTep) ? null
            : $"/api/v1/chat/avatars/{accountId}/{Uri.EscapeDataString(maTep!)}";

        return string.IsNullOrWhiteSpace(ten) && duongAnh is null
            ? null : new ContactProfile(string.IsNullOrWhiteSpace(ten) ? null : ten, duongAnh);
    }

    public Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId, string text,
        CancellationToken ct)
        => GuiAsync(tenantId, accountId, "sendMessage", new { chat_id = externalUserId, text }, ct);

    /// <summary>
    /// Telegram nhận media qua trường <c>photo</c>/<c>document</c> = URL — bot TỰ TẢI về, không
    /// nhận nhị phân. Đây là kênh DUY NHẤT trong ba kênh cho ảnh + chữ chú thích trong CÙNG một
    /// tin (<c>caption</c>), nên không cần gửi tin phụ như Zalo/Messenger.
    /// </summary>
    public Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct)
    {
        var (truong, phuong) = loai switch
        {
            ChatKind.Image => ("photo", "sendPhoto"),
            ChatKind.Audio => ("audio", "sendAudio"),
            _ => ("document", "sendDocument"),
        };
        object body = string.IsNullOrWhiteSpace(caption)
            ? new Dictionary<string, object> { ["chat_id"] = externalUserId, [truong] = url }
            : new Dictionary<string, object> { ["chat_id"] = externalUserId, [truong] = url, ["caption"] = caption };
        return GuiAsync(tenantId, accountId, phuong, body, ct);
    }

    private async Task<SendResult> GuiAsync(string tenantId, string accountId, string phuongThuc, object body,
        CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null)
            return new(false, false, null, "Chưa khai bot token Telegram cho tài khoản này");

        try
        {
            var http = _http.CreateClient();
            using var res = await http.PostAsJsonAsync($"{ApiBase}/bot{token}/{phuongThuc}", body, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            var o = JsonNode.Parse(raw)?.AsObject();

            if (o?["ok"]?.GetValue<bool>() == true)
                return new(true, false, o["result"]?["message_id"]?.ToString(), null);

            var moTa = o?["description"]?.ToString() ?? Truncate(raw);
            // 5xx là hỏng tạm thời phía Telegram; 4xx là mình sai (khách chặn bot, sai chat id) —
            // thử lại chỉ tốn công.
            return new(false, (int)res.StatusCode >= 500, null, $"Telegram từ chối: {moTa}");
        }
        catch (Exception ex)
        {
            return new(false, true, null, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
