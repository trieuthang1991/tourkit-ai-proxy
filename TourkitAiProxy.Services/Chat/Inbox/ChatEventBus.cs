// Services/Chat/Inbox/ChatEventBus.cs
using System.Threading.Channels;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Đẩy sự kiện tới các tab đang mở hộp thư, thay cho hỏi-lại-4-giây.
///
/// <para><b>Kẹp theo tenant NGAY TRONG BUS</b>, không lọc ở endpoint: lọc ở ngoài thì một lần quên
/// là hộp thư công ty này nhận sự kiện của công ty khác — rò rỉ chéo tenant, thứ nặng nhất trong
/// danh sách rủi ro của spec.</para>
///
/// <para><b>Bắn là bỏ (fire-and-forget), có giới hạn.</b> Mỗi người nghe một hàng đợi 100 sự kiện,
/// đầy thì bỏ sự kiện CŨ NHẤT chứ không chặn. Chặn nghĩa là một tab treo làm nghẽn cả luồng xử lý
/// tin của khách — đắt hơn nhiều so với việc một tab lỡ mất vài sự kiện rồi tự tải lại.</para>
///
/// <para>⚠️ <b>Chỉ thấy sự kiện của CHÍNH instance này.</b> Chạy nhiều instance sau load-balancer
/// thì tin tới instance khác không đẩy sang được — đó là việc của Task 4.3 (Redis pub/sub). Giao
/// diện vẫn giữ đường lùi hỏi-lại nên không mất tin, chỉ chậm.</para>
/// </summary>
public class ChatEventBus
{
    private readonly List<(string Tenant, Channel<ChatEvent> Kenh)> _nghe = new();
    private readonly object _khoa = new();

    /// Số tab đang nghe. Dùng cho test và cho trang quản trị — người nghe không được gỡ ra khi tab
    /// đóng là rò rỉ: mỗi lần mở hộp thư thêm một hàng đợi không ai đọc.
    public int SoNguoiNghe { get { lock (_khoa) return _nghe.Count; } }

    /// <summary>
    /// Bắn một sự kiện. <b>Không bao giờ ném và không bao giờ chờ</b> — chỗ gọi là luồng xử lý tin
    /// của khách, hỏng ở đây mà lan ra là mất tin thật vì một tab không ai nhìn.
    /// </summary>
    public void Bao(ChatEvent e)
    {
        lock (_khoa)
            foreach (var (tenant, kenh) in _nghe)
                if (string.Equals(tenant, e.TenantId, StringComparison.Ordinal))
                    kenh.Writer.TryWrite(e);   // TryWrite: đầy thì bỏ, KHÔNG chặn
    }

    /// <summary>
    /// Nghe sự kiện của MỘT tenant cho tới khi <paramref name="ct"/> bị huỷ (tab đóng, mạng rớt).
    /// </summary>
    public async IAsyncEnumerable<ChatEvent> NgheAsync(string tenantId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var kenh = Channel.CreateBounded<ChatEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,   // mất sự kiện cũ còn hơn nghẽn
            SingleReader = true,
        });
        lock (_khoa) _nghe.Add((tenantId, kenh));
        try
        {
            await foreach (var e in kenh.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            // finally chứ không phải sau vòng lặp: người nghe phải được gỡ kể cả khi huỷ hoặc ném.
            lock (_khoa) _nghe.RemoveAll(x => x.Kenh == kenh);
        }
    }
}
