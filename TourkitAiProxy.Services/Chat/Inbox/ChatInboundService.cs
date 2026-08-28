// Services/Chat/Inbox/ChatInboundService.cs
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services.Chat.Channels;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Đường xử lý tin khách nhắn tới: chống trùng → ghi → gộp cụm → bot trả lời → xếp hàng đợi gửi.
///
/// <para><b>Đợt 1 CHƯA nối nghiệp vụ CRM.</b> Bot trả lời bằng kiến thức chung + lời dặn của công
/// ty, không tra cứu tour/khách/giá. Nối CRM là việc của đợt sau — làm sớm thì mỗi tin khách nhắn
/// kéo theo một loạt truy vấn CRM trong khi phần đường ống còn chưa chắc.</para>
/// </summary>
public class ChatInboundService
{
    /// Chờ khách im bấy lâu rồi mới gộp cụm tin lại xử lý.
    private static readonly TimeSpan BurstIdle = TimeSpan.FromSeconds(4);

    private readonly ChatRepository _repo;
    private readonly ChatWorkSignal _tin;
    private readonly IEnumerable<IChatChannelAdapter> _adapters;
    private readonly ProviderRegistry _providers;
    private readonly AiCallContext _aiCtx;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatInboundService> _log;
    private readonly ChatBotSettingsRepository _cauHinh;
    private readonly ChatMediaMirror _soiTep;
    private readonly ChatEventBus _bus;

    public ChatInboundService(ChatRepository repo, IEnumerable<IChatChannelAdapter> adapters,
        ProviderRegistry providers, AiCallContext aiCtx, IConfiguration cfg,
        ILogger<ChatInboundService> log, ChatEventBus bus, ChatWorkSignal tin,
        ChatBotSettingsRepository cauHinh, ChatMediaMirror soiTep)
    { _repo = repo; _adapters = adapters; _providers = providers; _aiCtx = aiCtx; _cfg = cfg; _log = log; _bus = bus; _tin = tin; _cauHinh = cauHinh; _soiTep = soiTep; }

    public IChatChannelAdapter? Adapter(ChatChannel kenh)
        => _adapters.FirstOrDefault(a => a.Channel == kenh);

    /// <summary>
    /// Xử lý một loạt sự kiện của cùng một kênh.
    ///
    /// <para>Gọi từ NỀN, sau khi webhook đã trả 200. Kênh nào cũng gửi lại khi không thấy 200, mà
    /// xử lý mất vài giây (có gọi AI) — trả lời chậm là kênh gửi lại và khách nhận tin nhân đôi.</para>
    ///
    /// <para>⚠️ <b>NÉM RA khi có sự kiện hỏng — đừng nuốt.</b> Vẫn chạy hết cả loạt trước đã (mỗi
    /// tin là một khách khác nhau, một cái hỏng không được kéo theo cái khác), nhưng cuối cùng
    /// phải báo lên để <c>ChatInboundWorker</c> đánh dấu dòng là HỎNG và thử lại — nó có sẵn cả
    /// đường đó.</para>
    ///
    /// <para><b>Vì sao đây là chuyện lớn.</b> Bản trước ghi log rồi đi tiếp, nên chỗ gọi tưởng
    /// mọi việc êm và đánh dấu <c>status = 1</c> (xong) với <c>error_message</c> RỖNG. Kết quả là
    /// một sự cố ngừng nhận tin HOÀN TOÀN không để lại dấu vết nào trong CSDL: gói tin nằm đó,
    /// gắn nhãn đã xử lý, mà hội thoại thì không có. Đã xảy ra thật 27/08/2026 — bốn gói tin
    /// Telegram của khách biến mất kiểu đó, và cách duy nhất tìm ra là dò tay từng bảng.</para>
    ///
    /// <para>Chạy lại một dòng cũ là AN TOÀN: chống trùng nằm ở tầng CSDL (chỉ mục
    /// <c>ux_msg_external</c>), nên tin đã ghi sẽ không ghi lần hai — đó cũng chính là lý do hàng
    /// đợi lưu thân THÔ chứ không lưu bản đã bóc.</para>
    /// </summary>
    /// <param name="accountId">Tài khoản (Trang/OA/bot) đã kiểm chữ ký khớp — do endpoint webhook
    /// xác định TRƯỚC khi gọi hàm này, xem <see cref="IChatChannelAdapter.VerifyAsync"/>.</param>
    /// <exception cref="AggregateException">Có ít nhất một sự kiện trong loạt xử lý hỏng.</exception>
    public async Task HandleAsync(string tenantId, string accountId, IReadOnlyList<InboundChatEvent> sk,
        CancellationToken ct)
    {
        var hong = new List<Exception>();
        foreach (var e in sk)
        {
            try { await OneEventAsync(tenantId, accountId, e, ct); }
            catch (Exception ex)
            {
                // Một sự kiện hỏng không được kéo cả loạt: mỗi tin là một khách khác nhau.
                _log.LogError(ex, "[chat] xử lý sự kiện hỏng tenant={T} kênh={K} uid={U}",
                    tenantId, e.Channel, e.ExternalUserId);
                hong.Add(ex);
            }
        }

        if (hong.Count > 0)
            throw new AggregateException(
                $"{hong.Count}/{sk.Count} sự kiện xử lý hỏng (tenant={tenantId}, tài khoản={accountId})",
                hong);
    }

    private async Task OneEventAsync(string tenantId, string accountId, InboundChatEvent e, CancellationToken ct)
    {
        // Khách bấm nút: xác nhận với kênh NGAY, trước mọi việc khác. Xử lý bên dưới có gọi AI
        // nên mất vài giây, mà trong lúc đó Telegram để nút quay vòng trên máy khách.
        if (e.ButtonClickId is { Length: > 0 } maNut && Adapter(e.Channel) is { } boNut)
        {
            try { await boNut.AckButtonClickAsync(tenantId, accountId, maNut, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "[chat] xác nhận lượt bấm nút hỏng"); }
        }

        await _repo.UpsertContactAsync(tenantId, e.Channel, e.ExternalUserId, e.DisplayName, ct: ct);

        // Còn thiếu tên hoặc ảnh thì hỏi thẳng nhà cung cấp.
        //
        // Zalo và Telegram kèm sẵn tên trong gói tin nên nhánh này không bao giờ chạy cho hai kênh
        // đó. Riêng Messenger, gói tin của Meta CHỈ có mã người dùng — không hỏi thì cả hộp thư
        // hiện một dãy số như "4951953868228330" thay cho tên khách.
        //
        // Nuốt mọi lỗi bên trong adapter: không lấy được tên thì hiện mã, xấu nhưng vẫn dùng được.
        // Chặn tin của khách chỉ vì không lấy được cái tên là đổi một lỗi nhỏ lấy một lỗi to.
        if (await _repo.NeedsContactProfileAsync(tenantId, e.Channel, e.ExternalUserId, ct)
            && Adapter(e.Channel) is { } boNoi
            && await boNoi.ContactProfileAsync(tenantId, accountId, e.ExternalUserId, ct) is { } hoSo)
        {
            // Ảnh đại diện đi thẳng về kho mình ngay tại đây, không lưu url của Meta: url đó có
            // hạn, mà chỗ này lại KHÔNG bao giờ hỏi lại một khách đã có ảnh — nên lưu url của họ
            // đồng nghĩa hẹn ngày cả hộp thư hiện ảnh vỡ.
            var anh = (await MirrorAvatarAsync(tenantId, e.Channel, hoSo.AvatarUrl, ct)).Url
                      ?? hoSo.AvatarUrl;
            await _repo.UpsertContactAsync(tenantId, e.Channel, e.ExternalUserId, hoSo.Name, anh, ct);
        }
        var hoiThoai = await _repo.GetOrCreateConversationAsync(tenantId, e.Channel, e.ExternalUserId, accountId, ct);

        // Nguồn khách đến (quảng cáo/liên kết/QR). Ghi TRƯỚC mọi nhánh return bên dưới: nó có thể
        // đi kèm postback, kèm tin thường, hoặc tới MỘT MÌNH — bỏ ở nhánh nào là mất ở nhánh đó.
        if (e.Referral is { } tuDau)
            await _repo.SetReferralAsync(tenantId, hoiThoai.Id, tuDau, ct);

        // Gói CHỈ mang nguồn, không có tin nào (khách mở cuộc trò chuyện từ quảng cáo mà chưa gõ
        // gì). Ghi nguồn xong là về — tạo một tin rỗng trong hội thoại là bày rác cho nhân viên.
        if (e.Referral is not null && e.ExternalMsgId is null && string.IsNullOrWhiteSpace(e.Text)
            && e.AttachmentJson is null && e.Reaction is null && e.Watermark is null)
        {
            _bus.Publish(new(tenantId, hoiThoai.Id, "doi-hoi-thoai", null));
            return;
        }

        // Cảm xúc: gắn vào một tin ĐÃ CÓ, không phải tin mới. Xử lý xong là về — không đụng tới
        // mốc "khách vừa nhắn", không mở lại cửa sổ trả lời, không đánh thức bot. Thả tim vào tin
        // cũ mà làm hội thoại nhảy lên đầu danh sách như có tin mới là báo động giả.
        if (e.Reaction is { } camXuc)
        {
            await _repo.SetReactionAsync(tenantId, e.Channel, camXuc, e.ExternalUserId, ct);
            _bus.Publish(new(tenantId, hoiThoai.Id, "doi-hoi-thoai", null));
            return;
        }

        // Nền tảng báo trạng thái tin MÌNH đã gửi — không phải tin mới, xử lý xong là về.
        // Trước đây chỗ này bóc ra rồi BỎ, nên tin gửi đi dừng mãi ở "đã gửi" dù giao diện đã vẽ
        // sẵn dấu tích hai mức.
        if (e.Watermark is { } moc)
        {
            // Instagram KHÔNG gửi mốc thời gian, chỉ gửi mã tin cuối khách đã đọc — phải tra
            // ngược ra thời điểm của chính tin đó. Lấy tạm giờ nhận gói cho nhanh là đánh dấu
            // THỪA: mọi tin gửi trước đó bị coi là đã xem, kể cả tin khách chưa hề mở.
            var mocLuc = moc.UpToUtc;
            if (moc.ExternalMsgId is { Length: > 0 } maTinDoc)
            {
                var luc = await _repo.GetMessageSentAtAsync(tenantId, hoiThoai.Id, maTinDoc, ct);
                if (luc is null)
                {
                    // Tin không nằm trong hộp thư (nhân viên trả lời từ app Meta trước khi nối,
                    // hoặc tin đã xoá). Không có mốc thì KHÔNG đánh dấu gì — đoán là nói dối.
                    _log.LogDebug("[chat] báo đã xem theo mã tin {Ma} nhưng không tìm thấy tin", maTinDoc);
                    return;
                }
                mocLuc = luc.Value;
            }

            var soDong = await _repo.MarkStateWatermarkAsync(tenantId, hoiThoai.Id, moc.State, mocLuc, ct);
            _log.LogDebug("[chat] mốc {TT} tới {Luc:o} — đổi {N} tin, hội thoại {H}",
                moc.State, mocLuc, soDong, hoiThoai.Id);
            // Chỉ báo khi thật sự có tin đổi trạng thái — nền tảng gửi lại mốc cũ khá thường,
            // báo mọi lần là các tab tải lại liên tục cho một thứ y hệt.
            if (soDong > 0) _bus.Publish(new(tenantId, hoiThoai.Id, "doi-trang-thai", null));
            return;
        }

        // ── Tin CŨ: nền tảng trả lịch sử về lúc nối ──────────────────────────
        //
        // Ghi rồi DỪNG. Ba việc ngay dưới đây đều sai với tin cũ:
        //   · sinh câu trả lời — một năm lịch sử là hàng trăm câu trợ lý gửi thẳng cho khách
        //     HÔM NAY, về những chuyện đã xong từ lâu. Đây là kiểu hỏng không rút lại được.
        //   · cho bot câm 30 phút — tính từ giờ, vì một tin của ba năm trước.
        //   · chờ gộp tin — bốn giây nhân với vài nghìn tin lịch sử.
        //
        // Thời điểm lấy từ chính gói tin (e.SentUtc), không phải giờ nhập.
        if (e.IsHistory)
        {
            var idCu = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel,
                e.IsEcho ? ChatDirection.Out : ChatDirection.In,
                e.IsEcho ? ChatSender.Agent : ChatSender.Customer, null, e.Kind, e.Text,
                e.AttachmentJson, e.ExternalMsgId,
                e.IsEcho ? ChatState.Sent : ChatState.Delivered, ct, e.SentUtc);
            if (idCu is null) return;   // đã nhập lần trước — nền tảng gửi lại cùng một mảnh

            // Đánh dấu đã xử lý NGAY: nếu không, cụm gộp tin của lượt tin thật kế tiếp sẽ vơ cả
            // đống tin cũ vào làm câu hỏi cho trợ lý.
            await _repo.MarkProcessedAsync(tenantId, new[] { idCu.Value }, ct);
            await _repo.RecomputeActivityAsync(tenantId, hoiThoai.Id, ct);
            _bus.Publish(new(tenantId, hoiThoai.Id, "tin-moi", idCu.Value));
            return;
        }

        // ── Tiếng vọng: nhân viên trả lời từ app của kênh ────────────────────
        if (e.IsEcho)
        {
            var idV = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.Out,
                ChatSender.Agent, null, e.Kind, e.Text, e.AttachmentJson, e.ExternalMsgId,
                ChatState.Sent, ct);
            if (idV is null) return;   // đã ghi lần trước
            await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.Summarize(e.Text), false, ct);
            // Có người thật đang trả lời → bot phải câm, nếu không nó nói đè lên người ta.
            // Bao lâu là do công ty đặt: đội trực dày thì để ngắn, đội mỏng thì để dài.
        await _repo.PauseBotAsync(tenantId, hoiThoai.Id,
            (await _cauHinh.GetAsync(tenantId, ct)).MuteMinutes, ct);
            _bus.Publish(new(tenantId, hoiThoai.Id, "tin-moi", idV.Value));
            return;
        }

        // ── Tin của khách ────────────────────────────────────────────────────
        var id = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.In,
            ChatSender.Customer, null, e.Kind, e.Text, e.AttachmentJson, e.ExternalMsgId, ChatState.Delivered, ct);
        if (id is null) return;   // webhook gửi lại — bỏ qua, KHÔNG sinh thêm câu trả lời

        await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.Summarize(e.Text), true, ct);
        // Bắn NGAY, trước quãng nghỉ gộp tin: nhân viên phải thấy tin khách lập tức, đừng bắt họ
        // chờ thêm bốn giây chỉ vì bot đang đợi xem khách có gõ tiếp không.
        _bus.Publish(new(tenantId, hoiThoai.Id, "tin-moi", id.Value));

        // Soi ảnh/tệp về kho riêng. ĐẶT SAU khi đã ghi tin và đã bắn tin cho giao diện: tải tệp
        // là chạm mạng, đặt trước thì tin của khách nằm chờ một tấm ảnh 5MB tải xong mới hiện.
        //
        // Bắt buộc phải làm, không phải cho đẹp: URL của nền tảng đều có hạn (Meta ~5 ngày,
        // Telegram ~1 giờ), lưu nguyên thì hộp thư tự rỗng dần mà không ai làm gì sai.
        await MirrorMessageMediaAsync(tenantId, hoiThoai.Id, id.Value, e.Channel, e.Kind,
            e.AttachmentJson, ct);

        // Gộp tin nhắn liên tiếp: chờ khách im rồi mới xử lý cả cụm. Chờ thẳng ở đây thay vì hẹn
        // giờ riêng — đang chạy nền, vài giây không ảnh hưởng ai, mà đỡ hẳn một cơ chế hẹn giờ.
        await Task.Delay(BurstIdle, ct);

        // Cấu hình của CÔNG TY NÀY, không phải của máy chủ. Đọc trước mọi việc tốn kém: tắt bot
        // thì khỏi chờ gộp tin, khỏi gọi AI, khỏi bật dấu "đang gõ" rồi im.
        var cfgBot = await _cauHinh.GetAsync(tenantId, ct);
        if (!cfgBot.Enabled) return;

        var moi = await _repo.GetConversationAsync(tenantId, hoiThoai.Id, ct);
        if (moi is null || !ChatRules.BotMayReply(moi, DateTime.UtcNow)) return;

        var choXuLy = await _repo.ListPendingInboundAsync(tenantId, hoiThoai.Id, ct);
        if (choXuLy.Count == 0) return;   // luồng khác đã xử lý cụm này

        // Chỉ trả lời khi có CHỮ. Ảnh/sticker/vị trí thì ghi vào hộp thư cho nhân viên xem, bot
        // đoán bừa nội dung ảnh sẽ trả lời lạc đề.
        var cauHoi = ChatRules.JoinBurst(choXuLy.Select(m => m.Body));
        await _repo.MarkProcessedAsync(tenantId, choXuLy.Select(m => m.Id), ct);
        if (string.IsNullOrWhiteSpace(cauHoi)) return;

        // Bật ba chấm "đang gõ" TRƯỚC khi hỏi AI. Sinh câu trả lời mất vài giây; không có dấu hiệu
        // nào thì khách nhìn màn hình trống và tưởng không ai đọc tin của mình.
        //
        // Đặt sau mọi phép kiểm ở trên là cố ý: chỉ báo khi CHẮC CHẮN sắp trả lời. Bật rồi im là
        // tệ hơn không bật — khách thấy "đang gõ" rồi chờ mãi không có gì.
        if (Adapter(e.Channel) is { } boNoiGo)
            await boNoiGo.SendTypingAsync(tenantId, accountId, e.ExternalUserId, ct);

        // Đọc lại đoạn hội thoại làm ngữ cảnh. Trước 28/08/2026 bot chỉ nhận đúng cụm tin vừa
        // tới, nên khách nhắn "Thế còn tháng 10?" là nó không biết đang nói về tour nào — mà
        // khách nào cũng nhắn kiểu đó.
        //
        // Lấy dư vài tin rồi mới lọc: hàm dựng bỏ tin hỏng và tin chờ gửi (khách chưa hề đọc),
        // nên xin đúng số lượt là hụt mất mấy dòng.
        var lichSu = await _repo.ListMessagesAsync(tenantId, hoiThoai.Id, cfgBot.HistoryTurns * 2, ct);
        var nhacLai = ChatRules.BuildConversationPrompt(lichSu, cauHoi, cfgBot.HistoryTurns);

        var traLoi = await GenerateReplyAsync(tenantId, hoiThoai.Id, nhacLai, cfgBot, ct);
        if (string.IsNullOrWhiteSpace(traLoi)) return;

        var idRa = await _repo.AppendMessageAsync(tenantId, hoiThoai.Id, e.Channel, ChatDirection.Out,
            ChatSender.Ai, null, ChatKind.Text, traLoi, null, null, ChatState.Pending, ct);
        if (idRa is null) return;

        await _repo.TouchConversationAsync(tenantId, hoiThoai.Id, ChatRules.Summarize(traLoi), false, ct);
        await _repo.EnqueueOutboxAsync(tenantId, hoiThoai.Id, idRa.Value, ct);
        // Bot vừa soạn xong thì đẩy đi NGAY, đừng để nằm chờ hết nhịp — khách đang nhìn màn hình.
        _tin.Signal(ChatLane.Out);
        _bus.Publish(new(tenantId, hoiThoai.Id, "tin-moi", idRa.Value));
    }

    /// <summary>
    /// Tải ảnh/tệp của một tin về kho riêng rồi ghi đè phần đính kèm bằng URL của mình.
    ///
    /// <para><b>Không bao giờ ném và không bao giờ chặn tin.</b> Soi hỏng thì giữ nguyên đính
    /// kèm gốc — ảnh hết hạn sau vài ngày vẫn hơn là mất luôn cả tin ngay bây giờ.</para>
    ///
    /// <para>Nhãn dán truyền kèm <c>sticker_id</c> để đi ĐƯỜNG NHANH: mã đó cố định cho mọi
    /// khách nên cái like thứ hai trở đi không phải tải lại — xem <see cref="ChatMediaMirror"/>.</para>
    /// </summary>
    private async Task<MirrorOutcome> MirrorMessageMediaAsync(string tenantId, long hoiThoaiId,
        long messageId, ChatChannel kenh, ChatKind loai, string? attachmentJson, CancellationToken ct)
    {
        if (!_soiTep.Configured || string.IsNullOrWhiteSpace(attachmentJson))
            return MirrorOutcome.Retry;

        try
        {
            var tep = ChatAttachment.Read(kenh, loai, attachmentJson, 0);
            if (tep.Count == 0) return MirrorOutcome.Retry;

            // Mã nhãn dán nằm trong gói THÔ, không có trong hình dạng đã chuẩn hoá — đọc riêng.
            // KHÔNG lọc theo loai: tin nhận trước lúc bộ bóc biết nhận nhãn dán bị ghi nhầm là
            // ẢNH, mà những tin đó chính là thứ soi lại đang đi cứu.
            var maNhanDan = StickerIdOf(attachmentJson);
            var khoa = await ChannelTokenAsync(tenantId, kenh, ct);

            var ra = new JsonArray();
            var coDoi = false;
            var conCuuDuoc = false;
            foreach (var f in tep)
            {
                var kq = string.IsNullOrWhiteSpace(f.Url)
                    ? new ChatMediaMirror.KetQuaSoi(null, true)
                    : await _soiTep.MirrorAsync(tenantId, kenh, new(f.Url!, maNhanDan, khoa), ct);

                if (kq.Url is not null) coDoi = true;
                else if (!kq.HetCuu) conCuuDuoc = true;

                ra.Add(new JsonObject
                {
                    ["ten"] = f.Name,
                    ["kich"] = f.Size,
                    ["url"] = kq.Url ?? f.Url,
                });
            }

            // Không soi được cái nào thì đừng ghi đè: giữ hình dạng gốc để lần chạy sau (hoặc
            // đường proxy tệp của Telegram) vẫn còn đủ dữ liệu mà làm việc.
            //
            // Và nói rõ cho chỗ gọi biết vì sao hỏng: nếu MỌI tệp đều bị nhà cung cấp từ chối
            // bằng một mã không cứu được thì thử lại chỉ tốn công — xem GiveUpMediaAsync.
            if (!coDoi) return conCuuDuoc ? MirrorOutcome.Retry : MirrorOutcome.GiveUp;

            await _repo.SetAttachmentAsync(tenantId, messageId,
                new JsonObject { ["tk"] = 1, ["tep"] = ra }.ToJsonString(), ct);
            _bus.Publish(new(tenantId, hoiThoaiId, "doi-trang-thai", messageId));
            return MirrorOutcome.Mirrored;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat] soi tệp hỏng, giữ url gốc — tin {Id}", messageId);
            return MirrorOutcome.Retry;
        }
    }

    /// <summary>
    /// Kết cục một lượt soi MỘT tin. Ba nhánh chứ không phải được/không được, vì "không được"
    /// có hai loại đối xử ngược nhau — xem <see cref="ChatMediaMirror.KetQuaSoi"/>.
    /// </summary>
    public enum MirrorOutcome
    {
        /// <summary>Đã soi và đã ghi đè đính kèm.</summary>
        Mirrored,
        /// <summary>Hỏng tạm — để lượt sau thử lại.</summary>
        Retry,
        /// <summary>Hỏng hẳn — đừng thử nữa, thử lại chỉ tốn băng thông.</summary>
        GiveUp,
    }

    /// <summary>
    /// Soi lại tệp của những tin NHẬN TRƯỚC khi có bước soi — một mẻ mỗi lượt gọi.
    ///
    /// <para><b>Vì sao phải có, chứ không chỉ soi tin mới.</b> Mọi tin nhận trước hôm nay vẫn
    /// đang trỏ thẳng ra CDN của nền tảng. Đo trên hộp thư thật (27/08/2026): ảnh trong đó hết
    /// hạn 01/09/2026. Không soi lại thì đúng ngày đó cả loạt ảnh cũ biến thành ô vỡ, và lúc ấy
    /// không còn cách nào lấy lại.</para>
    ///
    /// <para><b>Tiến độ nằm trong CSDL, không nằm ở chỗ gọi.</b> Mỗi dòng lấy ra đã được
    /// <see cref="ChatRepository.ClaimMediaAsync"/> đánh dấu ngay lúc lấy, nên gọi lại là ra mẻ
    /// KẾ TIẾP chứ không phải mẻ cũ — không cần truyền mốc, không sợ hai chỗ gọi giẫm nhau, và
    /// dừng giữa chừng thì phần đã cứu vẫn được giữ.</para>
    ///
    /// <para>Một tin hỏng chỉ là một tin hỏng: nó không ghi đè đính kèm (giữ nguyên gói gốc) và
    /// không làm hỏng cả mẻ. Hỏng tạm thì lượt sau thử lại, tối đa
    /// <see cref="ChatRepository.MirrorMaxTries"/> lần. Hỏng hẳn thì bỏ ngay, không đợi hết số lần.</para>
    /// </summary>
    /// <param name="tenantId">
    /// <c>null</c> = mọi công ty, dùng cho worker nền chạy cho cả máy chủ. Truyền tên một công ty
    /// khi chạy tay cho riêng công ty đó. Dù thế nào tệp cũng vào kho của công ty SỞ HỮU tin, lấy
    /// từ chính dòng dữ liệu.
    /// </param>
    public async Task<BackfillResult> BackfillMediaAsync(string? tenantId, int soToiDa,
        short tranTang, CancellationToken ct)
    {
        if (!_soiTep.Configured) return BackfillResult.Rong;

        var ds = await _repo.ClaimMediaAsync(tenantId, soToiDa, tranTang, ct);
        var xong = 0;
        var boHan = 0;
        foreach (var t in ds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                switch (await MirrorMessageMediaAsync(t.TenantId, t.ConversationId, t.Id,
                            (ChatChannel)t.Channel, (ChatKind)t.Kind, t.Attachment, ct))
                {
                    case MirrorOutcome.Mirrored: xong++; break;
                    case MirrorOutcome.GiveUp:
                        await _repo.GiveUpMediaAsync(t.Id, ct);
                        boHan++;
                        break;
                }
            }
            catch (Exception ex)
            {
                // Một tin hỏng không được kéo cả mẻ. Chỗ này bắt được thứ MirrorMessageMediaAsync
                // không bắt: lượt ghi cờ xuống CSDL. Một cú nấc kết nối lúc đó mà làm dừng cả mẻ
                // thì 24 tin còn lại coi như mất lượt oan.
                _log.LogWarning(ex, "[chat] soi lại tệp hỏng — tin {Id}", t.Id);
            }
        }

        if (ds.Count > 0)
            _log.LogInformation("[chat] soi lại tệp cũ: {Xong}/{Tong} tin ({Bo} bỏ hẳn)",
                xong, ds.Count, boHan);
        return new(xong, ds.Count, ds.Count == 0 ? (short)0 : ds.Min(x => x.Tries));
    }

    /// <summary>
    /// Kết quả một mẻ soi lại.
    ///
    /// <para><paramref name="Examined"/> = 0 nghĩa là hết việc — điều kiện dừng của vòng quét.
    /// <paramref name="Mirrored"/> chỉ đếm thứ soi được, phần chênh là thứ hỏng.</para>
    ///
    /// <para><b><paramref name="Tier"/> — tầng thử lại thấp nhất trong mẻ này</b> (0 = có thứ
    /// chưa ai đụng tới). Đây là thứ thay cho một cột mốc thời gian trong CSDL: vòng quét chạy
    /// hết tầng thấp nhất rồi DỪNG, để tầng cao hơn dành cho vòng sau. Nhờ vậy mỗi thứ chỉ được
    /// thử đúng một lần mỗi vòng, và khoảng cách giữa hai lần thử chính là nhịp của vòng quét —
    /// một sự cố mạng ngắn không đốt sạch số lần thử của cả hộp thư trong vài phút.</para>
    /// </summary>
    public record BackfillResult(int Mirrored, int Examined, short Tier)
    {
        /// <summary>Mẻ rỗng — chưa khai kho, hoặc hết việc.</summary>
        public static readonly BackfillResult Rong = new(0, 0, 0);
    }

    /// <summary>
    /// Tải ảnh đại diện của khách về kho riêng, trả url của mình.
    ///
    /// <para>Trả <c>null</c> khi không soi được — chỗ gọi giữ nguyên url của kênh: ảnh sẽ hết hạn
    /// sau vài ngày, nhưng có vẫn hơn không.</para>
    ///
    /// <para>Bỏ qua url TƯƠNG ĐỐI: đó là ảnh Telegram, vốn đã đi qua đường proxy của mình
    /// (đường tải thật của Telegram chứa bot token nên không đưa ra trình duyệt được).</para>
    /// </summary>
    private async Task<ChatMediaMirror.KetQuaSoi> MirrorAvatarAsync(string tenantId, ChatChannel kenh,
        string? url, CancellationToken ct)
    {
        if (!_soiTep.Configured) return new(null);
        // Url rỗng hoặc tương đối: không có gì để tải, và sẽ mãi như vậy.
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new(null, true);

        var khoa = await ChannelTokenAsync(tenantId, kenh, ct);
        return await _soiTep.MirrorAsync(tenantId, kenh, new(url, null, khoa), ct);
    }

    /// <summary>
    /// Soi lại ảnh đại diện của những khách đã lưu TRƯỚC khi có bước soi — cùng lý do với
    /// <see cref="BackfillMediaAsync"/>, và còn gấp hơn: một url ảnh đại diện hết hạn không chỉ
    /// làm vỡ một tin, nó làm vỡ khuôn mặt khách ở mọi chỗ trong hộp thư.
    ///
    /// <para>Cách nhận việc và cách dừng giống hệt <see cref="BackfillMediaAsync"/> — xem
    /// <c>ChatRepository.ClaimAvatarsAsync</c>.</para>
    /// </summary>
    public async Task<BackfillResult> BackfillAvatarsAsync(string? tenantId, int soToiDa,
        short tranTang, CancellationToken ct)
    {
        if (!_soiTep.Configured) return BackfillResult.Rong;

        var ds = await _repo.ClaimAvatarsAsync(tenantId, _soiTep.KhoCuaMinh, soToiDa, tranTang, ct);
        var xong = 0;
        var boHan = 0;
        foreach (var lh in ds)
        {
            if (ct.IsCancellationRequested) break;
            var kenh = (ChatChannel)lh.Channel;
            try
            {
                var kq = await MirrorAvatarAsync(lh.TenantId, kenh, lh.AvatarUrl, ct);
                if (kq.Url is { } moi)
                {
                    await _repo.SetContactAvatarAsync(lh.TenantId, kenh, lh.ExternalId, moi, ct);
                    xong++;
                }
                else if (kq.HetCuu)
                {
                    await _repo.GiveUpAvatarAsync(lh.TenantId, kenh, lh.ExternalId, ct);
                    boHan++;
                }
            }
            catch (Exception ex)
            {
                // Một khách hỏng không được kéo cả mẻ: mỗi dòng là một ảnh độc lập.
                _log.LogWarning(ex, "[chat] soi ảnh đại diện hỏng — {Kenh}/{Id}", kenh, lh.ExternalId);
            }
        }

        if (ds.Count > 0)
            _log.LogInformation("[chat] soi lại ảnh đại diện: {Xong}/{Tong} khách ({Bo} bỏ hẳn)",
                xong, ds.Count, boHan);
        return new(xong, ds.Count, ds.Count == 0 ? (short)0 : ds.Min(x => x.Tries));
    }

    /// <summary>Mã nhãn dán trong gói thô của Meta. Kênh khác chưa có khái niệm này.</summary>
    private static string? StickerIdOf(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray ds) return null;
            foreach (var x in ds.OfType<JsonNode>())
                if (x["payload"]?["sticker_id"]?.ToString() is { Length: > 0 } ma) return ma;
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Khoá kèm khi tải tệp. <b>WhatsApp bắt buộc</b> — thiếu là Meta trả 401 và ảnh mất trắng.
    /// Messenger/Instagram không cần (URL đã ký sẵn) nhưng gửi kèm cũng không sao.
    /// </summary>
    private async Task<string?> ChannelTokenAsync(string tenantId, ChatChannel kenh, CancellationToken ct)
    {
        if (kenh != ChatChannel.WhatsApp) return null;
        // Chỉ WhatsApp cần, và adapter của nó tự biết lấy khoá ở đâu.
        return Adapter(kenh) is Channels.WhatsAppChatAdapter wa
            ? await wa.AccessTokenForMediaAsync(tenantId, ct) : null;
    }
    /// <summary>
    /// Sinh câu trả lời.
    ///
    /// <para>AI hỏng thì trả <c>null</c> — <b>im lặng còn hơn gửi câu rác cho khách</b>. Hội thoại
    /// vẫn nằm trong hộp thư, nhân viên thấy và trả lời tay được.</para>
    /// </summary>
    private async Task<string?> GenerateReplyAsync(string tenantId, long hoiThoaiId, string cauHoi,
        ChatBotSettings cfgBot, CancellationToken ct)
    {
        // Khung an toàn: máy chủ khai đè được (Chat:SystemPrompt) để sửa nóng khi cần, còn mặc
        // định nằm trong mã. Lời dặn RIÊNG của công ty NỐI THÊM vào, không thay thế — khung chứa
        // luật chống bịa giá tour, bỏ nó là bot hứa giữ chỗ với khách thật.
        var khung = _cfg["Chat:SystemPrompt"] ?? DefaultSystemPrompt;
        var loiDan = cfgBot.BuildSystemPrompt(khung);
        try
        {
            // Gọi từ NỀN nên không có HttpContext — phải Push thủ công, nếu không là bỏ qua hạn
            // mức tenant và log ra feature "unknown".
            using var _ = _aiCtx.Push(AiFeatures.ChatInbox, tenantId);
            var provider = _providers.Resolve(null);
            var res = await provider.CompleteAsync(new CompleteRequest(
                Prompt: cauHoi, Provider: null, Model: null,
                MaxTokens: 700, Temperature: 0.5, System: loiDan), ct);

            var text = res.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _log.LogWarning("[chat] AI trả rỗng, hội thoại={C}", hoiThoaiId);
                return null;
            }
            return text;
        }
        catch (QuotaExhaustedException)
        {
            _log.LogWarning("[chat] tenant={T} hết lượt AI — bot im, nhân viên trả lời tay", tenantId);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[chat] gọi AI hỏng, hội thoại={C}", hoiThoaiId);
            return null;
        }
    }

    /// <summary>
    /// Lời dặn mặc định cho bot.
    ///
    /// <para><b>Cấm bịa số là dòng quan trọng nhất.</b> Đợt 1 bot chưa tra được CRM, nên nếu không
    /// cấm nó sẽ tự nghĩ ra giá tour và lịch khởi hành — khách đọc xong tưởng thật, và công ty phải
    /// chịu. Thà nói "để em kiểm rồi báo lại".</para>
    /// </summary>
    private const string DefaultSystemPrompt = """
        Bạn là nhân viên tư vấn của một công ty du lịch Việt Nam, đang trả lời khách qua tin nhắn.

        Cách trả lời:
        - Tiếng Việt, xưng "em", gọi khách là "anh/chị". Ngắn gọn, 2-4 câu, như tin nhắn thật.
        - Thân thiện nhưng không màu mè, không dùng emoji quá một cái.

        TUYỆT ĐỐI KHÔNG được bịa: giá tour, lịch khởi hành, số chỗ còn, khuyến mãi, chính sách hoàn
        huỷ. Bạn KHÔNG có dữ liệu thật của công ty. Gặp câu hỏi cần số liệu thì nói thật là sẽ kiểm
        tra rồi báo lại, và hỏi thêm thông tin cần thiết (ngày đi, số khách, điểm đến).

        Không hứa thay công ty. Không tự nhận đã đặt chỗ hay đã giữ chỗ cho khách.
        """;
}
