// Services/Chat/Inbox/ChatEventBus.cs
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Cache;

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
/// <para><b>Nhiều instance:</b> SSE giữ kết nối tới ĐÚNG MỘT instance, nên tin tới instance khác
/// thì tab đang mở không thấy — triệu chứng là "thỉnh thoảng tin mới không hiện", loại lỗi chỉ ra
/// mặt khi đông người dùng. Có <c>Redis:ConnectionString</c> thì mỗi sự kiện đi qua pub/sub sang
/// mọi instance. KHÔNG có Redis thì bus vẫn chạy trong một instance và giao diện tự lùi về hỏi lại
/// định kỳ — nhưng <b>nói rõ ở log lúc khởi động</b>, không im lặng chạy chế độ kém hơn.</para>
/// </summary>
public class ChatEventBus
{
    /// Kênh Redis. Một kênh cho MỌI tenant — kẹp tenant vẫn làm ở người nghe, y như bản một
    /// instance, nên không có đường thứ hai để quên lọc.
    private const string KenhRedis = "tkai:chat:events";

    private readonly List<(string Tenant, Channel<ChatEvent> Kenh)> _nghe = new();
    private readonly object _khoa = new();
    private readonly ISubscriber? _redis;

    /// Mã của instance này. Redis trả gói tin về cho CẢ người phát, nên phải nhận ra gói của chính
    /// mình mà bỏ — không thì mỗi sự kiện tới người nghe hai lần và giao diện tải lại gấp đôi.
    public string MaInstance { get; } = Guid.NewGuid().ToString("N");

    /// Đang chạy chế độ nhiều instance hay không. Giao diện đọc qua <c>/api/v1/features</c> để biết
    /// có được tin vào đẩy hay phải giữ đường lùi hỏi lại định kỳ.
    public bool NhieuInstance => _redis != null;

    public ChatEventBus(RedisProvider? redis = null, ILogger<ChatEventBus>? log = null)
    {
        _redis = redis?.Db?.Multiplexer.GetSubscriber();
        log?.LogInformation("[chat/events] chế độ {C}", _redis is null
            ? "MỘT INSTANCE (không có Redis — nhiều instance sau load-balancer sẽ mất sự kiện, "
              + "giao diện tự lùi về hỏi lại định kỳ)"
            : "nhiều instance qua Redis pub/sub");

        // Đăng ký nhận từ instance khác. Hỏng ở đây KHÔNG được làm sập khởi động: Redis tạm xuống
        // thì mất đẩy chéo instance, còn app thì vẫn phải phục vụ.
        try
        {
            _redis?.Subscribe(RedisChannel.Literal(KenhRedis), (_, val) => NhanTuXa(val.ToString()));
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "[chat/events] không đăng ký được Redis — lùi về một instance");
        }
    }

    /// Số tab đang nghe. Dùng cho test và cho trang quản trị — người nghe không được gỡ ra khi tab
    /// đóng là rò rỉ: mỗi lần mở hộp thư thêm một hàng đợi không ai đọc.
    public int SoNguoiNghe { get { lock (_khoa) return _nghe.Count; } }

    /// <summary>
    /// Bắn một sự kiện. <b>Không bao giờ ném và không bao giờ chờ</b> — chỗ gọi là luồng xử lý tin
    /// của khách, hỏng ở đây mà lan ra là mất tin thật vì một tab không ai nhìn.
    /// </summary>
    public void Bao(ChatEvent e)
    {
        BaoNoiBo(e);
        // FireAndForget: chỗ gọi là luồng xử lý tin của khách, chờ Redis ở đây là để mạng quyết
        // định tốc độ trả lời khách.
        try { _redis?.Publish(RedisChannel.Literal(KenhRedis), DongGoi(MaInstance, e), CommandFlags.FireAndForget); }
        catch { /* Redis xuống → mất đẩy chéo instance, không được lan ra ngoài */ }
    }

    private void BaoNoiBo(ChatEvent e)
    {
        lock (_khoa)
            foreach (var (tenant, kenh) in _nghe)
                if (string.Equals(tenant, e.TenantId, StringComparison.Ordinal))
                    kenh.Writer.TryWrite(e);   // TryWrite: đầy thì bỏ, KHÔNG chặn
    }

    private record GoiTin(string TuAi, ChatEvent SuKien);

    /// Gói một sự kiện kèm mã instance phát. Public để test dựng được gói tin mà không cần Redis thật.
    public static string DongGoi(string tuAi, ChatEvent e)
        => JsonSerializer.Serialize(new GoiTin(tuAi, e));

    /// <summary>
    /// Cửa vào của gói tin từ instance khác.
    ///
    /// <para><b>Không bao giờ ném.</b> Gói tin tới từ MẠNG — có thể là phiên bản cũ còn nằm trong
    /// Redis, hoặc ai đó publish nhầm kênh. Ném ở đây là chết luồng đăng ký, và từ đó instance này
    /// câm hẳn mà không ai biết.</para>
    /// </summary>
    public void NhanTuXa(string? tho)
    {
        GoiTin? goi;
        try { goi = string.IsNullOrWhiteSpace(tho) ? null : JsonSerializer.Deserialize<GoiTin>(tho); }
        catch { return; }
        if (goi?.SuKien is null || string.IsNullOrEmpty(goi.TuAi)) return;
        if (string.Equals(goi.TuAi, MaInstance, StringComparison.Ordinal)) return;   // của chính mình
        BaoNoiBo(goi.SuKien);
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
