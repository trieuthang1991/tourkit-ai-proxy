// Services/Chat/Inbox/ChatOutboxWorker.cs
using TourkitAiProxy.Services.Chat.Channels;

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

    private readonly IServiceProvider _sp;
    private readonly ILogger<ChatOutboxWorker> _log;

    public ChatOutboxWorker(IServiceProvider sp, ILogger<ChatOutboxWorker> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[chat/outbox] bắt đầu, nhịp {N}s", Nhip.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try { await MotNhipAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một nhịp hỏng — chết là hộp thư ngừng gửi trong im lặng.
                _log.LogError(ex, "[chat/outbox] nhịp hỏng");
            }
            try { await Task.Delay(Nhip, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task MotNhipAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        if (!repo.Configured) return;

        var adapters = scope.ServiceProvider.GetServices<IChatChannelAdapter>().ToList();
        var rows = await repo.ClaimOutboxAsync(10, ct);
        foreach (var r in rows)
        {
            try { await MotDongAsync(repo, adapters, r, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "[chat/outbox] gửi dòng {Id} hỏng", r.Id);
                await repo.FinishOutboxAsync(r.Id, false, r.RetryCount + 1 < SoLuotThuLai, ex.Message, ct);
            }
        }
    }

    private async Task MotDongAsync(ChatRepository repo, List<IChatChannelAdapter> adapters,
        ChatRepository.OutboxRow r, CancellationToken ct)
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
            _log.LogInformation("[chat/outbox] bỏ dòng {Id}: {Ly}", r.Id, cuaSo.Reason);
            return;
        }

        var tin = (await repo.ListMessagesAsync(r.TenantId, r.ConversationId, 300, ct))
            .FirstOrDefault(m => m.Id == r.MessageId);
        if (tin is null || string.IsNullOrWhiteSpace(tin.Body))
        {
            await repo.FinishOutboxAsync(r.Id, false, false, "Không thấy nội dung tin", ct);
            return;
        }

        var kq = await adapter.SendTextAsync(r.TenantId, hoiThoai.ContactExternalId, tin.Body!, ct);
        if (kq.Ok)
        {
            await repo.FinishOutboxAsync(r.Id, true, false, null, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.DaGui, null, ct);
            return;
        }

        var conLuot = kq.ThuLai && r.RetryCount + 1 < SoLuotThuLai;
        await repo.FinishOutboxAsync(r.Id, false, conLuot, kq.Error, ct);
        if (!conLuot)
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.Hong, kq.Error, ct);
        _log.LogWarning("[chat/outbox] dòng {Id} hỏng ({Thu}): {Loi}",
            r.Id, conLuot ? "sẽ thử lại" : "dừng", kq.Error);
    }
}
