using System.Net.Http.Json;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.TourKit;

/// <summary>
/// Bắn "có tin nhắn chat nội bộ mới" sang app mobile, qua TourKit.Api.
///
/// <para><b>Vì sao phải gọi API chứ không tự ghi bảng:</b> ai-proxy KHÔNG nối DB của từng công ty
/// (xem <c>TourKitSourceHandler</c>) — mọi thay đổi dữ liệu CRM đều đi qua TourKit.Api. Bảng thông báo
/// <c>NotificationOfEachUser</c> nằm trong DB TỪNG công ty, nên chặng cuối do TourKit.Api làm: nó kiểm
/// người nhận có token FCM hay không, ghi bảng, rồi trigger <c>trg_SendToPushDb</c> tự đẩy sang PushDb
/// cho worker gửi FCM. Ghi bảng (thay vì bắn FCM thẳng) để mở app ra vẫn còn thấy trong danh sách
/// thông báo — FCM bay đi là mất.</para>
///
/// <para><b>Nhịp chống rung KHÔNG quyết ở đây</b> (tầng này không trả lời "khi nào thì gửi"): chỗ gọi
/// giữ khoá Redis theo từng (người nhận, phòng) ngay lúc lưu tin. Lớp này chỉ đọc hộ
/// <see cref="Cooldown"/> để hai bên dùng chung một con số cấu hình — xem <c>InternalChatEndpoints</c>.</para>
///
/// <para><b>Bắn-rồi-quên, nuốt lỗi tuyệt đối:</b> TourKit.Api chậm hay chết không được làm treo việc
/// gửi tin nhắn. Nhưng CÓ kiểm mã trạng thái và ghi log — 401 vì lệch khoá là lỗi im lặng kinh điển.</para>
/// </summary>
public sealed class InternalChatPushClient
{
    private const string Path = "/api/internal-chat/push";

    /// <summary>Trần cột bên TourKit.Api: Content nvarchar(255) → cắt 200; PushBody nvarchar(500).</summary>
    private const int MaxTitle = 200;
    private const int MaxBody = 500;

    /// <summary>Trần một lượt gọi — khớp MaxItems bên TourKit.Api.</summary>
    private const int MaxItems = 500;

    private const double DefaultCooldownMinutes = 5d;
    private const double MaxCooldownMinutes = 1440d;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<InternalChatPushClient> _log;
    private readonly string? _apiKey;

    /// <summary>Đã bắn cho một người ở một phòng thì nghỉ bấy nhiêu lâu mới bắn tiếp cho người đó.</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>Tắt hoặc thiếu khoá ⇒ false, để chỗ gọi khỏi tốn lượt Redis nào.</summary>
    public bool Available => _apiKey is { Length: > 0 };

    public InternalChatPushClient(IHttpClientFactory factory, IConfiguration cfg,
        ILogger<InternalChatPushClient> log)
    {
        _factory = factory;
        _log = log;

        var key = cfg["InternalChat:Notify:ApiKey"];
        if (!string.IsNullOrEmpty(key) && key.StartsWith("ENC:", StringComparison.Ordinal))
            key = Crypton.Decrypt(key.Substring(4));
        var enabled = cfg.GetValue("InternalChat:Notify:Enabled", false);
        _apiKey = enabled && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;

        // Chặn trần 1 ngày: gõ nhầm 5 thành 5000 thì thông báo im gần 4 ngày mà không ai hiểu vì sao.
        var minutes = cfg.GetValue("InternalChat:Notify:CooldownMinutes", DefaultCooldownMinutes);
        Cooldown = TimeSpan.FromMinutes(minutes is > 0 and <= MaxCooldownMinutes
            ? minutes
            : DefaultCooldownMinutes);

        _log.LogInformation("[chat-push] {TrangThai}; nghỉ giữa hai lần bắn: {Phut} phút",
            Available ? "BẬT" : enabled ? "TẮT (thiếu InternalChat:Notify:ApiKey)" : "TẮT (Enabled=false)",
            Cooldown.TotalMinutes);
    }

    /// <summary>
    /// Cố ý KHÔNG nhận CancellationToken của request: token đó bị huỷ ngay khi tin nhắn trả về cho
    /// người gửi, giữ nó lại thì thông báo bị cắt giữa đường. Chỗ gọi chịu trách nhiệm chạy nền —
    /// xem <c>InternalChatEndpoints.NotifyWithCooldown</c>.
    /// </summary>
    public Task NotifyAsync(string tenantId, long roomId, long messageId, int senderUserId,
        string? senderName, string? preview, IReadOnlyCollection<int> recipients)
    {
        if (!Available || recipients.Count == 0) return Task.CompletedTask;
        var list = recipients.Count <= MaxItems ? recipients : recipients.Take(MaxItems).ToList();
        return PostAsync(tenantId, roomId, messageId, senderUserId, senderName, preview, list);
    }

    private async Task PostAsync(string tenantId, long roomId, long messageId, int senderUserId,
        string? senderName, string? preview, IReadOnlyCollection<int> recipients)
    {
        try
        {
            // Tên phòng KHÔNG có trong AppendResult và cố ý không đi tra: thêm một lượt đọc DB cho MỖI
            // tin nhắn là cái giá quá đắt cho một dòng chữ. Mở thông báo ra là vào thẳng phòng.
            //
            // title = NỘI DUNG TIN, body = CÂU MÔ TẢ — KHÔNG phải ngược lại. Lý do: `title` đi vào cột
            // Content, mà thẻ trong danh sách thông báo của app lấy TÊN NGƯỜI GỬI làm tiêu đề rồi
            // (join theo senderUserId) và lấy Content làm thân thẻ. Đặt tên người vào `title` là thẻ
            // hiện tên hai lần mà không hiện tin nhắn nào.
            var name = (senderName ?? "").Trim();
            var title = Cut(string.IsNullOrWhiteSpace(preview) ? "Bạn có tin nhắn mới" : preview, MaxTitle);
            var text = Cut(name.Length > 0 ? $"{name} vừa nhắn tin" : "Tin nhắn nội bộ mới", MaxBody);

            var payload = new
            {
                tenantId,
                items = recipients.Select(u => new
                {
                    userId = u,
                    roomId,
                    senderUserId,
                    title,
                    body = text,
                }).ToArray(),
            };

            var http = _factory.CreateClient("tourkit");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var msg = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(payload) };
            msg.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
            using var resp = await http.SendAsync(msg, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                _log.LogWarning("[chat-push] {Tenant} phòng {Room}: HTTP {Code} — {Body}",
                    tenantId, roomId, (int)resp.StatusCode, Cut(err, 300));
                return;
            }

            _log.LogInformation("[chat-push] {Tenant} phòng {Room} tin {Msg}: đã chuyển cho {So} người",
                tenantId, roomId, messageId, recipients.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat-push] {Tenant} phòng {Room}: bắn thông báo lỗi — bỏ qua",
                tenantId, roomId);
        }
    }

    private static string Cut(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
