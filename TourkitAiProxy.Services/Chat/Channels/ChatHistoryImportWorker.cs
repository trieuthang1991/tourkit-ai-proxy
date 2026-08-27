// Services/Chat/Channels/ChatHistoryImportWorker.cs
using System.Threading.Channels;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Hàng đợi các lượt <b>lấy lại hội thoại cũ</b> mà người dùng vừa bấm.
///
/// <para><b>Vì sao không <c>Task.Run</c> thẳng trong endpoint.</b> Có một guard kiến trúc cấm
/// <c>Task.Run</c> trong tầng endpoint của hộp thư, và lý do của nó đúng: việc chạy rời trong bộ
/// nhớ thì IIS recycle / deploy / crash là mất, không dấu vết. Ở đây phần đáng giữ đã nằm an toàn
/// trong <c>chat_inbound_events</c> nên mất lượt lấy không mất tin — nhưng đi qua một
/// <see cref="BackgroundService"/> thì vẫn hơn hẳn: có giới hạn chạy đồng thời, dừng êm khi tắt
/// máy chủ, và không có tác vụ mồ côi nào chạy ngoài vòng đời ứng dụng.</para>
///
/// <para><b>Chạy MỘT lượt tại một thời điểm</b> cho cả máy chủ. Đọc lịch sử là việc tốn hạn mức
/// gọi Graph; ba công ty cùng bấm một lúc mà chạy song song thì đủ để Facebook chặn tạm cả ứng
/// dụng — lúc đó <b>tin trực tiếp của mọi công ty cũng ngừng về</b>. Xếp hàng chậm hơn, nhưng
/// chậm còn hơn kéo sập đường tin đang chạy.</para>
/// </summary>
public class ChatHistoryImportQueue
{
    public record YeuCau(string TenantId, ChatChannel Kenh, string AccountId);

    // Chặn ở 100: quá số này thì hoặc ai đó đang bấm loạn, hoặc có lỗi vòng lặp. Chờ chỗ trống
    // (chứ không bỏ) để không mất lượt của người dùng.
    private readonly Channel<YeuCau> _hang =
        Channel.CreateBounded<YeuCau>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ValueTask XepAsync(YeuCau y, CancellationToken ct) => _hang.Writer.WriteAsync(y, ct);

    public IAsyncEnumerable<YeuCau> DocAsync(CancellationToken ct) => _hang.Reader.ReadAllAsync(ct);
}

/// <inheritdoc cref="ChatHistoryImportQueue"/>
public class ChatHistoryImportWorker : BackgroundService
{
    private readonly ChatHistoryImportQueue _hang;
    private readonly ChatHistoryJobs _viec;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ChatHistoryImportWorker> _log;

    public ChatHistoryImportWorker(ChatHistoryImportQueue hang, ChatHistoryJobs viec,
        IServiceProvider sp, ILogger<ChatHistoryImportWorker> log)
    { _hang = hang; _viec = viec; _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var y in _hang.DocAsync(ct))
        {
            try
            {
                using var scope = _sp.CreateScope();
                var bo = scope.ServiceProvider.GetRequiredService<MetaHistoryImporter>();
                _viec.KetThuc(y.TenantId, y.Kenh, y.AccountId,
                    await bo.ImportAsync(y.TenantId, y.Kenh, y.AccountId, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Máy chủ đang tắt. Đánh dấu dừng để lần sau người dùng không thấy "đang chạy"
                // treo mãi ở một lượt đã chết cùng tiến trình cũ.
                _viec.KetThuc(y.TenantId, y.Kenh, y.AccountId,
                    new(0, 0, true, "Máy chủ khởi động lại giữa chừng. Bấm lại để lấy tiếp."));
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[chat/lịch-sử] lượt lấy {Kenh}/{Acc} hỏng hẳn", y.Kenh, y.AccountId);
                _viec.KetThuc(y.TenantId, y.Kenh, y.AccountId, new(0, 0, false, ex.Message));
            }
        }
    }
}
