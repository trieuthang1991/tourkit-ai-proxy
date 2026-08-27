// Services/Chat/Channels/IChatChannelAdapter.cs
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>Kết quả gửi một tin ra kênh.</summary>
/// <param name="Ok">Gửi được chưa.</param>
/// <param name="ThuLai">Hỏng TẠM THỜI (mạng, nhà cung cấp 5xx) → để hàng đợi thử lại. false nghĩa
/// là thử lại cũng vô ích (hết cửa sổ, chưa khai OA, khách chặn) — đừng quay vòng vô nghĩa.</param>
/// <param name="ExternalMsgId">Id tin phía kênh, để đối soát về sau.</param>
public record SendResult(bool Ok, bool ThuLai, string? ExternalMsgId, string? Error);

/// <summary>
/// Một kênh chat. Lõi (nhận tin → sinh trả lời → xếp hàng đợi → gửi) KHÔNG biết kênh nào; mỗi kênh
/// chỉ cần cài đủ ba việc: xác thực webhook, bóc sự kiện, và gửi tin.
///
/// <para><b>Đa tài khoản/kênh</b> (một công ty có thể nối nhiều Trang Facebook, nhiều OA Zalo,
/// nhiều bot Telegram): mọi thao tác cần khoá đăng nhập đều nhận <c>accountId</c> — không còn giả
/// định "mỗi công ty một tài khoản/kênh".</para>
///
/// <para>Thêm kênh mới = thêm 1 lớp cài giao diện này + 1 member trong <see cref="ChatChannel"/>.
/// Nếu phải sửa phần lõi thì phần trừu tượng hoá này đã sai.</para>
/// </summary>
public interface IChatChannelAdapter
{
    ChatChannel Channel { get; }

    /// <summary>
    /// Kiểm chữ ký webhook và cho biết THÂN THÔ này thuộc TÀI KHOẢN nào đã khai.
    ///
    /// <para><paramref name="accountIdTuUrl"/>: Telegram bắt buộc có (mỗi bot một URL riêng, xem
    /// <c>IChatChannelAdapter</c> ghi chú ở endpoint). Zalo/Messenger truyền <c>null</c> — hai
    /// kênh này cho phép NHIỀU tài khoản dùng CHUNG một đường webhook của công ty (đúng cách hai
    /// nền tảng đó vận hành: đăng ký webhook 1 lần/ứng dụng, nhiều Trang/OA cùng trỏ vào), nên
    /// adapter tự soát chữ ký qua TỪNG tài khoản đã khai để tìm ra khớp cái nào.</para>
    ///
    /// <para><paramref name="rawBody"/> phải là THÂN THÔ, chưa parse. Ký trên bản đã parse rồi
    /// serialize lại chỉ đúng khi thứ tự khoá và khoảng trắng trùng khít bản gốc — gần như không
    /// bao giờ trùng, và chữ ký sẽ luôn sai.</para>
    /// </summary>
    /// <returns>Mã tài khoản đã khớp, hoặc <c>null</c> nếu không tài khoản nào khớp (từ chối).</returns>
    Task<string?> VerifyAsync(string tenantId, string? accountIdTuUrl, string rawBody,
        IHeaderDictionary headers, CancellationToken ct);

    /// Bóc thân webhook thành sự kiện chuẩn hoá. Bỏ qua loại không quan tâm bằng cách không trả về.
    IReadOnlyList<InboundChatEvent> Parse(string rawBody);

    /// Gửi một tin chữ ra kênh, bằng đúng tài khoản <paramref name="accountId"/>.
    Task<SendResult> SendTextAsync(string tenantId, string accountId, string externalUserId, string text,
        CancellationToken ct);

    /// <summary>
    /// Gửi ảnh/tệp ra kênh — <paramref name="url"/> phải TẢI CÔNG KHAI ĐƯỢC, vì cả ba kênh đọc
    /// media bằng cách tự tải về từ URL, không nhận file nhị phân trực tiếp qua API chat này.
    /// </summary>
    /// <param name="caption">Chữ đi kèm, nếu kênh hỗ trợ gộp chung một tin. Có thể rỗng.</param>
    Task<SendResult> SendMediaAsync(string tenantId, string accountId, string externalUserId, ChatKind loai,
        string url, string? caption, CancellationToken ct);

    /// <summary>
    /// Hỏi nhà cung cấp tên + ảnh đại diện của khách. Trả <c>null</c> khi kênh không có (hoặc
    /// không cần) — <b>mặc định là không làm gì</b>.
    ///
    /// <para><b>Vì sao không bắt mọi kênh làm.</b> Zalo và Telegram gửi sẵn tên ngay trong gói
    /// tin webhook nên không tốn lượt gọi nào. Chỉ Messenger là gói tin chỉ có mã người dùng —
    /// muốn biết tên phải hỏi riêng.</para>
    /// </summary>
    Task<ContactProfile?> ContactProfileAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => Task.FromResult<ContactProfile?>(null);

    /// <summary>
    /// Xác nhận với kênh rằng lượt bấm nút đã tiếp nhận. <b>Mặc định không làm gì</b>.
    ///
    /// <para>Telegram BẮT BUỘC gọi: không gọi thì nút <b>quay vòng</b> trên máy khách cho tới lúc
    /// hết giờ rồi hiện lỗi — kể cả khi mình đã xử lý xong và đã trả lời. Zalo/Messenger không có
    /// khái niệm này, nên đây là mặc định rỗng chứ không phải hàm bắt buộc.</para>
    ///
    /// <para>Nuốt mọi lỗi trong từng bộ nối: mất một lượt xác nhận không đáng để chặn tin.</para>
    /// </summary>
    Task AckButtonClickAsync(string tenantId, string accountId, string maBamNut,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Bật dấu "đang gõ" bên phía khách. <b>Mặc định không làm gì</b> — kênh nào không có thì bỏ.
    ///
    /// <para>Nuốt mọi lỗi trong từng bộ nối: mất một chi tiết lịch sự không đáng để chặn tin.</para>
    /// </summary>
    Task SendTypingAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Báo cho khách biết tin của họ ĐÃ ĐƯỢC MỞ. <b>Mặc định không làm gì.</b>
    ///
    /// <para>Chỉ gọi khi có NGƯỜI THẬT mở hội thoại. Bot đọc mà cũng báo đã xem là nói dối khách:
    /// họ tưởng có nhân viên đang nhìn, rồi chờ.</para>
    /// </summary>
    Task MarkSeenAsync(string tenantId, string accountId, string externalUserId,
        CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Kênh có cửa <b>"người thật trả lời muộn"</b> thì cài thêm giao diện này. Hiện chỉ Messenger và
/// Instagram: Meta cho nhân viên nhắn tới <b>7 ngày</b> kể từ tin của khách, ngoài cửa sổ 24 giờ
/// thường, miễn là đính nhãn <c>HUMAN_AGENT</c>.
///
/// <para><b>Vì sao tách ra chứ không thêm tham số vào <see cref="IChatChannelAdapter"/>.</b> Bốn
/// kênh còn lại không có khái niệm này: Zalo đi bằng ZNS, WhatsApp đi bằng mẫu duyệt sẵn,
/// Telegram/TikTok không giới hạn. Nhét một tham số riêng của Meta vào chữ ký chung là bắt mọi
/// kênh mang theo một thứ chúng không dùng — đúng cái mà ghi chú ở giao diện chính bảo đừng làm.</para>
///
/// <para><b>Ai được gọi:</b> chỉ khi <see cref="TourkitAiProxy.Domain.Chat.ChatRules.ComputeSendWindow"/>
/// trả về <see cref="TourkitAiProxy.Domain.Chat.MetaSendTag.HumanAgent"/> — tức tin do NHÂN VIÊN
/// gõ. Đính nhãn này cho tin của bot là vi phạm chính sách Meta.</para>
/// </summary>
public interface ILateHumanReplySender
{
    Task<SendResult> SendTextAsHumanAgentAsync(string tenantId, string accountId, string externalUserId,
        string text, CancellationToken ct);

    Task<SendResult> SendMediaAsHumanAgentAsync(string tenantId, string accountId, string externalUserId,
        ChatKind loai, string url, string? caption, CancellationToken ct);
}

/// <param name="Anh">Ảnh đại diện. ⚠️ Meta ký hạn vào URL này nên nó <b>HẾT HẠN</b> sau một thời
/// gian — lưu rồi để mãi là vài tuần sau cả hộp thư hiện ảnh vỡ. Vì thế có mốc làm mới.</param>
public record ContactProfile(string? Name, string? AvatarUrl);
