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
    private static readonly TimeSpan Nhip = TimeSpan.FromSeconds(5);
    private const int SoLuotThuLai = 3;

    /// Vét bao nhiêu dòng mỗi nhịp. Đủ đầy thì gửi tiếp ngay, không ngủ — xem vòng lặp.
    private const int MoiLuot = 10;

    private readonly IServiceProvider _sp;
    private readonly ChatWorkSignal _tin;
    private readonly ILogger<ChatOutboxWorker> _log;

    public ChatOutboxWorker(IServiceProvider sp, ChatWorkSignal tin, ILogger<ChatOutboxWorker> log)
    { _sp = sp; _tin = tin; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[chat/outbox] bắt đầu, nhịp {N}s (có tín hiệu đánh thức)", Nhip.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            var lam = 0;
            try { lam = await MotNhipAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một nhịp hỏng — chết là hộp thư ngừng gửi trong im lặng.
                _log.LogError(ex, "[chat/outbox] nhịp hỏng");
            }

            // Vét đầy một lượt = gần như chắc chắn còn tồn. Ngủ lúc này là bắt khách chờ trong
            // khi máy đang rảnh.
            if (lam >= MoiLuot) continue;

            // Chờ TÍN HIỆU hoặc hết nhịp, cái nào tới trước. Nhân viên bấm Gửi là đánh thức ngay.
            if (!await _tin.ChoAsync(ChatLan.Ra, Nhip, ct) && ct.IsCancellationRequested) break;
        }
    }

    /// <summary>Trả về SỐ DÒNG đã vét — vòng lặp dùng nó để biết có nên gửi tiếp ngay không.</summary>
    private async Task<int> MotNhipAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        if (!repo.Configured) return 0;

        var adapters = scope.ServiceProvider.GetServices<IChatChannelAdapter>().ToList();
        var bus = scope.ServiceProvider.GetRequiredService<ChatEventBus>();
        var rows = await repo.ClaimOutboxAsync(MoiLuot, ct);
        foreach (var r in rows)
        {
            try { await MotDongAsync(repo, adapters, bus, r, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "[chat/outbox] gửi dòng {Id} hỏng", r.Id);
                await repo.FinishOutboxAsync(r.Id, false, r.RetryCount + 1 < SoLuotThuLai, ex.Message, ct);
            }
        }

        return rows.Count;
    }

    private async Task MotDongAsync(ChatRepository repo, List<IChatChannelAdapter> adapters,
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

        // Kiểm CỬA SỔ GỬI trước khi gọi API. Gọi rồi mới biết hết hạn thì tin đã mất, và lý do
        // trả về của kênh thường là mã lỗi khó hiểu.
        var cuaSo = ChatRules.TinhCuaSo(kenh, hoiThoai.ContactRepliedAt, DateTime.UtcNow);
        if (!cuaSo.Open)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, cuaSo.Reason, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Hong, cuaSo.Reason, ct);
            bus.Bao(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
            _log.LogInformation("[chat/outbox] bỏ dòng {Id}: {Ly}", r.Id, cuaSo.Reason);
            return;
        }

        var tin = (await repo.ListMessagesAsync(r.TenantId, r.ConversationId, 300, ct))
            .FirstOrDefault(m => m.Id == r.MessageId);
        if (tin is null)
        {
            await repo.FinishOutboxAsync(r.Id, false, false, "Không thấy nội dung tin", ct);
            return;
        }

        // Có đính kèm → gửi media (chữ là chú thích, có thể rỗng). Không đính kèm → gửi chữ như
        // trước. Đính kèm ghi theo hình dạng CHUẨN {ten,kich,url} lúc AppendMessageAsync ở endpoint
        // /send, nên đọc thẳng bằng chieu=1 (mình gửi) — xem ChatAttachment.Doc.
        var (kq, coDinhKem) = ((ChatKind)tin.Kind) switch
        {
            ChatKind.Chu => (await GuiChuAsync(adapter, r, hoiThoai, tin, ct), false),
            _ => (await GuiMediaAsync(adapter, r, hoiThoai, tin, ct), true),
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
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.DaGui, null, ct);
            // Mã tin của nền tảng — thứ duy nhất đối chiếu được khi nó báo lại "đã nhận"/"đã xem".
            // Telegram không bao giờ báo lại (Bot API không có), nhưng vẫn lưu: rẻ, và khi cần truy
            // vết một tin cụ thể trên nền tảng thì đúng cái mã này là thứ dán vào công cụ của họ.
            await repo.SetExternalMsgIdAsync(r.TenantId, r.MessageId, kq.ExternalMsgId, ct);
            bus.Bao(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
            return;
        }

        var conLuot = kq.ThuLai && r.RetryCount + 1 < SoLuotThuLai;
        await repo.FinishOutboxAsync(r.Id, false, conLuot, kq.Error, ct);
        if (!conLuot)
        {
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Hong, kq.Error, ct);
            bus.Bao(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
        }
        _log.LogWarning("[chat/outbox] dòng {Id} hỏng ({Thu}): {Loi}",
            r.Id, conLuot ? "sẽ thử lại" : "dừng", kq.Error);
    }

    private static async Task<SendResult?> GuiChuAsync(IChatChannelAdapter adapter, ChatRepository.OutboxRow r,
        ChatConversation hoiThoai, ChatMessage tin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tin.Body)) return null;
        return await adapter.SendTextAsync(r.TenantId, hoiThoai.AccountId, hoiThoai.ContactExternalId,
            tin.Body!, ct);
    }

    private static async Task<SendResult?> GuiMediaAsync(IChatChannelAdapter adapter, ChatRepository.OutboxRow r,
        ChatConversation hoiThoai, ChatMessage tin, CancellationToken ct)
    {
        var files = ChatAttachment.Doc((ChatChannel)hoiThoai.Channel, (ChatKind)tin.Kind, tin.Attachment, tin.Direction);
        var url = files.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        return await adapter.SendMediaAsync(r.TenantId, hoiThoai.AccountId, hoiThoai.ContactExternalId,
            (ChatKind)tin.Kind, url, tin.Body, ct);
    }
}
