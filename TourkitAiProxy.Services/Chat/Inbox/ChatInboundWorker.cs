// Services/Chat/Inbox/ChatInboundWorker.cs
using TourkitAiProxy.Services.Chat.Channels;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Rút hàng đợi sự kiện VÀO của chat rồi xử lý (nhận diện khách, ghi tin, gọi bot).
///
/// <para><b>Vì sao phải có hàng đợi thay vì xử lý thẳng trong webhook.</b> Webhook buộc phải trả
/// 200 ngay — kênh nào cũng gửi lại khi không thấy 200, mà xử lý có gọi AI nên mất vài giây. Nhưng
/// đã trả 200 thì kênh sẽ KHÔNG gửi lại nữa: nếu lúc đó việc xử lý còn nằm trong bộ nhớ (một
/// <c>Task.Run</c> chạy rời), thì IIS recycle / deploy / crash làm mất hẳn tin của khách, không để
/// lại dấu vết nào. Ghi thân thô xuống CSDL trước rồi mới xử lý thì mất điện giữa chừng vẫn còn
/// nguyên tin để chạy lại.</para>
///
/// <para><b>Ba kết cục</b>, giống <see cref="ChatOutboxWorker"/>: xử lý xong → <c>1</c>; hỏng mà
/// thử lại vô ích (thân tin sai, kênh đã gỡ) → <c>2</c> kèm lý do; hỏng tạm thời (AI timeout,
/// mạng) → trả về hàng đợi, hết lượt mới thành hỏng hẳn.</para>
/// </summary>
public class ChatInboundWorker : BackgroundService
{
    // Nhanh hơn hàng đợi gửi: đây là độ trễ khách CẢM NHẬN được (nhắn xong bao lâu thì bot đáp).
    private static readonly TimeSpan Nhip = TimeSpan.FromSeconds(2);
    private const int SoLuotThuLai = 3;

    /// Vét bao nhiêu dòng mỗi nhịp. Đủ đầy thì làm tiếp ngay, không ngủ — xem vòng lặp.
    private const int MoiLuot = 10;

    private readonly IServiceProvider _sp;
    private readonly ChatWorkSignal _tin;
    private readonly ILogger<ChatInboundWorker> _log;

    public ChatInboundWorker(IServiceProvider sp, ChatWorkSignal tin, ILogger<ChatInboundWorker> log)
    { _sp = sp; _tin = tin; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[chat/inbound] bắt đầu, nhịp {N}s (có tín hiệu đánh thức)", Nhip.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            var lam = 0;
            try { lam = await MotNhipAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một nhịp hỏng — chết là hộp thư ngừng nhận trong im lặng.
                _log.LogError(ex, "[chat/inbound] nhịp hỏng");
            }

            // Vét đầy một lượt = gần như chắc chắn còn tồn. Ngủ tiếp lúc này là để tin của khách
            // nằm chờ trong khi máy đang rảnh.
            if (lam >= MoiLuot) continue;

            // Chờ TÍN HIỆU hoặc hết nhịp, cái nào tới trước. Webhook ghi xong là đánh thức ngay,
            // nên đường thường tính bằng mili giây chứ không phải giây.
            if (!await _tin.ChoAsync(ChatLan.Vao, Nhip, ct) && ct.IsCancellationRequested) break;
        }
    }

    /// <summary>Trả về SỐ DÒNG đã vét — vòng lặp dùng nó để biết có nên làm tiếp ngay không.</summary>
    private async Task<int> MotNhipAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        if (!repo.Configured) return 0;

        var svc = scope.ServiceProvider.GetRequiredService<ChatInboundService>();
        var rows = await repo.ClaimInboundAsync(MoiLuot, ct);
        foreach (var r in rows)
        {
            try { await MotDongAsync(repo, svc, r, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "[chat/inbound] xử lý dòng {Id} hỏng", r.Id);
                await repo.FinishInboundAsync(r.Id, false, r.RetryCount + 1 < SoLuotThuLai, ex.Message, ct);
            }
        }

        return rows.Count;
    }

    private async Task MotDongAsync(ChatRepository repo, ChatInboundService svc,
        ChatRepository.InboundRow r, CancellationToken ct)
    {
        var kenh = (ChatChannel)r.Channel;
        var adapter = svc.Adapter(kenh);
        if (adapter is null)
        {
            // Kênh đã gỡ khỏi hệ thống — thử lại bao nhiêu lần cũng thế.
            await repo.FinishInboundAsync(r.Id, false, false, $"Chưa hỗ trợ kênh {kenh}", ct);
            return;
        }

        // Bóc thân thô ở ĐÂY chứ không lưu bản đã bóc: sửa adapter xong là chạy lại được dòng cũ,
        // còn lưu bản đã bóc thì lỗi bóc tin nằm lại vĩnh viễn trong hàng đợi.
        var sk = adapter.Parse(r.RawBody);
        if (sk.Count == 0)
        {
            await repo.FinishInboundAsync(r.Id, true, false, null, ct);
            return;
        }

        await svc.HandleAsync(r.TenantId, r.AccountId, sk, ct);
        await repo.FinishInboundAsync(r.Id, true, false, null, ct);
    }
}
