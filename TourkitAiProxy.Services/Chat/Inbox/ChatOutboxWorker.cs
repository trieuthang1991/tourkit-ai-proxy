// Services/Chat/Inbox/ChatOutboxWorker.cs
using TourkitAiProxy.Services.Chat.Channels;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Rút hàng đợi gửi của chat rồi đẩy ra kênh.
///
/// <para><b>Ba kết cục tách bạch</b>, theo đúng lệ của hàng đợi thông báo: gửi được → <c>1</c>;
/// hỏng mà thử lại vô ích (hết cửa sổ, chưa khai OA, khách chặn) → <c>2</c> kèm lý do đọc được;
/// hỏng tạm thời (mạng, nhà cung cấp 5xx) → trả về hàng đợi, hết lượt mới thành hỏng hẳn.
/// Gộp ba cái làm một thì hoặc quay vòng vô nghĩa, hoặc bỏ mất tin đáng lẽ gửi lại được.</para>
/// </summary>
public class ChatOutboxWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    /// Vét bao nhiêu dòng mỗi nhịp. Đủ đầy thì gửi tiếp ngay, không ngủ — xem vòng lặp.
    private const int PerCall = 10;

    private readonly IServiceProvider _sp;
    private readonly ChatWorkSignal _tin;
    private readonly ILogger<ChatOutboxWorker> _log;

    public ChatOutboxWorker(IServiceProvider sp, ChatWorkSignal tin, ILogger<ChatOutboxWorker> log)
    { _sp = sp; _tin = tin; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[chat/outbox] bắt đầu, nhịp {N}s (có tín hiệu đánh thức)", Tick.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            var lam = 0;
            try { lam = await OneTickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một nhịp hỏng — chết là hộp thư ngừng gửi trong im lặng.
                _log.LogError(ex, "[chat/outbox] nhịp hỏng");
            }

            // Vét đầy một lượt = gần như chắc chắn còn tồn. Ngủ lúc này là bắt khách chờ trong
            // khi máy đang rảnh.
            if (lam >= PerCall) continue;

            // Chờ TÍN HIỆU hoặc hết nhịp, cái nào tới trước. Nhân viên bấm Gửi là đánh thức ngay.
            if (!await _tin.WaitAsync(ChatLane.Out, Tick, ct) && ct.IsCancellationRequested) break;
        }
    }

    /// <summary>Trả về SỐ DÒNG đã vét — vòng lặp dùng nó để biết có nên gửi tiếp ngay không.</summary>
    private async Task<int> OneTickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        if (!repo.Configured) return 0;

        var adapters = scope.ServiceProvider.GetServices<IChatChannelAdapter>().ToList();
        var bus = scope.ServiceProvider.GetRequiredService<ChatEventBus>();
        var rows = await repo.ClaimOutboxAsync(PerCall, ct);
        foreach (var r in rows)
        {
            try { await OneRowAsync(repo, adapters, bus, r, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "[chat/outbox] gửi dòng {Id} hỏng", r.Id);
                await repo.FinishOutboxAsync(r.Id, false, r.RetryCount + 1 < MaxRetries, ex.Message, ct);
            }
        }

        return rows.Count;
    }

    private async Task OneRowAsync(ChatRepository repo, List<IChatChannelAdapter> adapters,
        ChatEventBus bus, ChatRepository.OutboxRow r, CancellationToken ct)
    {
        var hoiThoai = await repo.GetConversationAsync(r.TenantId, r.ConversationId, ct);
        if (hoiThoai is null)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, "Hội thoại không còn", ct);
            return;
        }

        var kenh = (ChatChannel)hoiThoai.Channel;
        var adapter = adapters.FirstOrDefault(a => a.Channel == kenh);
        if (adapter is null)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, $"Chưa hỗ trợ kênh {kenh}", ct);
            return;
        }

        var tin = (await repo.ListMessagesAsync(r.TenantId, r.ConversationId, 300, ct))
            .FirstOrDefault(m => m.Id == r.MessageId);
        if (tin is null)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, "Không thấy nội dung tin", ct);
            return;
        }

        // Kiểm CỬA SỔ GỬI trước khi gọi API. Gọi rồi mới biết hết hạn thì tin đã mất, và lý do
        // trả về của kênh thường là mã lỗi khó hiểu.
        //
        // ⚠️ Phải đọc TIN TRƯỚC rồi mới tính cửa sổ, vì cửa sổ phụ thuộc AI GỬI: Messenger và
        // Instagram cho NHÂN VIÊN nhắn tới 7 ngày (nhãn HUMAN_AGENT) trong khi bot chỉ có 24 giờ.
        // Đảo thứ tự lại là mọi tin đều bị tính theo luật của bot, và nhân viên mất 6 ngày.
        var nguoiGui = (ChatSender)tin.SenderKind;
        var cuaSo = ChatRules.ComputeSendWindow(kenh, hoiThoai.ContactRepliedAt, DateTime.UtcNow, nguoiGui);
        if (!cuaSo.Open)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, cuaSo.Reason, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Failed, cuaSo.Reason, ct);
            bus.Publish(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
            _log.LogInformation("[chat/outbox] bỏ dòng {Id}: {Ly}", r.Id, cuaSo.Reason);
            return;
        }

        // Có đính kèm → gửi media (chữ là chú thích, có thể rỗng). Không đính kèm → gửi chữ như
        // trước. Đính kèm ghi theo hình dạng CHUẨN {ten,kich,url} lúc AppendMessageAsync ở endpoint
        // /send, nên đọc thẳng bằng chieu=1 (mình gửi) — xem ChatAttachment.Doc.
        var (kq, coDinhKem) = ((ChatKind)tin.Kind) switch
        {
            ChatKind.Text => (await SendPlainTextAsync(adapter, r, hoiThoai, tin, cuaSo.Tag, ct), false),
            _ => (await SendMediaBodyAsync(adapter, r, hoiThoai, tin, cuaSo.Tag, ct), true),
        };
        if (kq is null)
        {
            await repo.FinishOutboxAsync(r.Id, false, false,
                coDinhKem ? "Đính kèm hỏng, không đọc được đường tải" : "Không thấy nội dung tin", ct);
            return;
        }
        if (kq.Ok)
        {
            await repo.FinishOutboxAsync(r.Id, true, false, null, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Sent, null, ct);
            // Mã tin của nền tảng — thứ duy nhất đối chiếu được khi nó báo lại "đã nhận"/"đã xem".
            // Telegram không bao giờ báo lại (Bot API không có), nhưng vẫn lưu: rẻ, và khi cần truy
            // vết một tin cụ thể trên nền tảng thì đúng cái mã này là thứ dán vào công cụ của họ.
            await repo.SetExternalMsgIdAsync(r.TenantId, r.MessageId, kq.ExternalMsgId, ct);
            bus.Publish(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
            return;
        }

        var conLuot = kq.ThuLai && r.RetryCount + 1 < MaxRetries;
        await repo.FinishOutboxAsync(r.Id, false, conLuot, kq.Error, ct);
        if (!conLuot)
        {
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Failed, kq.Error, ct);
            bus.Publish(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
        }
        _log.LogWarning("[chat/outbox] dòng {Id} hỏng ({Thu}): {Loi}",
            r.Id, conLuot ? "sẽ thử lại" : "dừng", kq.Error);
    }

    /// <summary>
    /// Gửi tin chữ. <paramref name="nhan"/> khác <c>None</c> nghĩa là đang đi qua cửa "người thật
    /// trả lời muộn" của Meta — chuyển sang đường gửi có nhãn, xem <see cref="ILateHumanReplySender"/>.
    ///
    /// <para>Kênh không cài giao diện đó mà lại có nhãn thì <b>không bao giờ xảy ra</b>: chỉ
    /// Messenger/Instagram mới sinh ra nhãn trong <c>ComputeSendWindow</c>. Vẫn để nhánh về đường
    /// thường thay vì ném — một luật mới thêm sau này không nên làm tin của khách biến mất.</para>
    /// </summary>
    private static async Task<SendResult?> SendPlainTextAsync(IChatChannelAdapter adapter, ChatRepository.OutboxRow r,
        ChatConversation hoiThoai, ChatMessage tin, MetaSendTag nhan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tin.Body)) return null;
        if (nhan == MetaSendTag.HumanAgent && adapter is ILateHumanReplySender muon)
            return await muon.SendTextAsHumanAgentAsync(r.TenantId, hoiThoai.AccountId,
                hoiThoai.ContactExternalId, tin.Body!, ct);
        return await adapter.SendTextAsync(r.TenantId, hoiThoai.AccountId, hoiThoai.ContactExternalId,
            tin.Body!, ct);
    }

    /// <inheritdoc cref="SendPlainTextAsync"/>
    private static async Task<SendResult?> SendMediaBodyAsync(IChatChannelAdapter adapter, ChatRepository.OutboxRow r,
        ChatConversation hoiThoai, ChatMessage tin, MetaSendTag nhan, CancellationToken ct)
    {
        var files = ChatAttachment.Read((ChatChannel)hoiThoai.Channel, (ChatKind)tin.Kind, tin.Attachment, tin.Direction);
        var url = files.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (nhan == MetaSendTag.HumanAgent && adapter is ILateHumanReplySender muon)
            return await muon.SendMediaAsHumanAgentAsync(r.TenantId, hoiThoai.AccountId,
                hoiThoai.ContactExternalId, (ChatKind)tin.Kind, url, tin.Body, ct);
        return await adapter.SendMediaAsync(r.TenantId, hoiThoai.AccountId, hoiThoai.ContactExternalId,
            (ChatKind)tin.Kind, url, tin.Body, ct);
    }
}
