// Services/Chat/Inbox/ChatModels.cs
namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Kênh chat. SỐ tường minh, lưu thẳng cột <c>channel</c> — số để tránh lỗi gõ chuỗi, và đổi tên
/// hiển thị không phải migrate dữ liệu.
///
/// <para>Thêm kênh mới = thêm 1 member ở đây + 1 lớp <see cref="Channels.IChatChannelAdapter"/>.
/// KHÔNG đụng phần lõi — nếu phải sửa lõi thì phần trừu tượng hoá đã sai.</para>
/// </summary>
public enum ChatChannel : short
{
    Zalo = 0,
    Messenger = 1,
    Webchat = 2,
    Telegram = 3,
}

/// Chiều của tin nhắn.
public enum ChatDirection : short { Vao = 0, Ra = 1 }

/// Ai gửi. Tách AI với nhân viên vì người đọc CẦN biết câu nào do máy trả lời.
public enum ChatSender : short { Khach = 0, Ai = 1, NhanVien = 2, HeThong = 3 }

/// Loại nội dung.
public enum ChatKind : short { Chu = 0, Anh = 1, Tep = 2, AmThanh = 3, Sticker = 4, ViTri = 5 }

/// Trạng thái vòng đời một tin GỬI ĐI. Thiếu delivered/seen/failed thì nhân viên không phân biệt
/// được "khách chưa đọc" với "gửi hỏng" — hai thứ dẫn tới hai hành động trái ngược.
public enum ChatState : short { Cho = 0, DaGui = 1, DaNhan = 2, DaXem = 3, Hong = 4 }

/// Trạng thái xử lý một hội thoại.
public enum ChatStatus : short { Moi = 0, DangXuLy = 1, DaDong = 2 }

/// <summary>Một sự kiện đến từ kênh, đã chuẩn hoá — lõi không cần biết kênh nào sinh ra nó.</summary>
/// <param name="IsEcho">Tin do CHÍNH OA/Page gửi, nhận lại dưới dạng tiếng vọng — nghĩa là có người
/// đang trả lời từ app của kênh (Zalo OA, Facebook Page). Phải ghi lại VÀ cho bot câm, nếu không
/// bot sẽ nói đè lên người thật.</param>
/// <param name="Watermark">Nền tảng báo lại trạng thái tin MÌNH đã gửi — không phải tin nhắn mới.
/// Xem <see cref="StateWatermark"/>.</param>
public record InboundChatEvent(
    ChatChannel Channel,
    string ExternalUserId,
    string? ExternalMsgId,
    ChatKind Kind,
    string? Text,
    string? AttachmentJson,
    DateTime SentUtc,
    bool IsEcho = false,
    string? DisplayName = null,
    StateWatermark? Watermark = null);

/// <summary>
/// Nền tảng báo lại trạng thái tin MÌNH đã gửi, theo kiểu <b>mốc nước</b>: mọi tin gửi trước
/// <paramref name="UpToUtc"/> đều đã đạt <paramref name="State"/>.
///
/// <para>Thay cho <c>SeenMarker</c> cũ (chuỗi <c>"seen"</c>): chuỗi đó chỉ nói được "đã xem",
/// không nói được "đã nhận", và không mang thời điểm — mà thiếu thời điểm thì hoặc đánh dấu cả
/// hội thoại (sai: tin gửi sau đó cũng bị coi là đã xem), hoặc không đánh dấu gì.</para>
/// </summary>
public record StateWatermark(ChatState State, DateTime UpToUtc);

public class ChatContact
{
    public string TenantId { get; set; } = "";
    public short Channel { get; set; }
    public string ExternalId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public int? CrmCustomerId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Bộ đếm cho hộp thư: theo trạng thái, theo kênh, số chưa đọc, tổng.</summary>
/// <param name="TheoTrangThai">status -> số hội thoại.</param>
/// <param name="TheoKenh">channel -> số hội thoại. Dải kênh bên trái giao diện đọc cái này.</param>
public record ChatInboxCounts(
    Dictionary<short, int> TheoTrangThai,
    Dictionary<short, int> TheoKenh,
    int ChuaDoc,
    int Tong);

public class ChatConversation
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public short Channel { get; set; }
    public string ContactExternalId { get; set; } = "";
    // Tài khoản (Trang/OA/bot) đã nhận cuộc trò chuyện này — rỗng ở dòng cũ tạo trước 24/08.
    public string AccountId { get; set; } = "";
    public short Status { get; set; }
    public string? AssignedUsername { get; set; }
    public DateTime? BotResumeAt { get; set; }
    public DateTime? ContactRepliedAt { get; set; }
    public DateTime? AgentRepliedAt { get; set; }
    /// Mốc đọc CHUNG của cả công ty. Giữ lại làm mốc ban đầu cho người chưa có dòng riêng —
    /// KHÔNG còn được ghi mới. Xem chat_conversation_reads.
    public DateTime? AgentLastReadAt { get; set; }
    /// Mốc đọc của CHÍNH người đang xem, ghép từ chat_conversation_reads lúc liệt kê.
    public DateTime? MyLastReadAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public string? LastPreview { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? DisplayName { get; set; }   // ghép từ chat_contacts khi liệt kê
}

public class ChatMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public short Direction { get; set; }
    public short SenderKind { get; set; }
    public string? SenderUsername { get; set; }
    public short Kind { get; set; }
    public string? Body { get; set; }
    public string? Attachment { get; set; }
    public short State { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Vị trí đọc tiếp trong danh sách hội thoại (sắp theo <c>last_activity_at DESC, id DESC</c>).</summary>
/// <param name="Id">BẮT BUỘC có, không chỉ mốc thời gian: hai hội thoại hoàn toàn có thể cùng
/// <c>last_activity_at</c> tới từng micro giây (hai webhook xử lý song song). Chỉ so thời gian thì
/// hoặc lặp một dòng, hoặc mất một dòng — và người dùng không bao giờ báo lại được lỗi kiểu đó.</param>
public record ConvCursor(DateTime LastActivityAt, long Id);

/// <summary>
/// Mã hoá con trỏ thành chuỗi đi trên URL. Hàm thuần — đây là chỗ có test thật.
/// </summary>
public static class ChatCursor
{
    public static string Ma(ConvCursor c)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
               $"{c.LastActivityAt.Ticks}|{c.Id}"))
           .TrimEnd('=').Replace('+', '-').Replace('/', '_');   // base64url — đi trên URL không phải escape

    /// <summary>Mã hỏng → <c>null</c>, KHÔNG ném: con trỏ nằm trên URL nên người dùng sửa tay được,
    /// và mã cũ từ bản trước vẫn có thể còn trong lịch sử trình duyệt. Ném là cả trang trắng.</summary>
    public static ConvCursor? Giai(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var b = s.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var phan = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b)).Split('|');
            if (phan.Length != 2) return null;
            if (!long.TryParse(phan[0], out var ticks) || !long.TryParse(phan[1], out var id)) return null;
            return new(new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch { return null; }
    }
}

/// <summary>
/// Một chuyện vừa xảy ra trong hộp thư, đủ để tab đang mở biết phải tải lại cái gì.
///
/// <para><b>Cố ý KHÔNG mang nội dung tin.</b> Sự kiện chỉ nói "hội thoại này vừa đổi"; tab tự gọi
/// API để lấy dữ liệu — nhờ vậy luật xem-được-gì vẫn nằm nguyên ở endpoint, không phải nhân bản
/// vào kênh đẩy. Đẩy thẳng nội dung qua đây là một đường rò dữ liệu thứ hai phải canh riêng.</para>
/// </summary>
/// <param name="Loai">"tin-moi" · "doi-trang-thai" · "doi-hoi-thoai".</param>
public record ChatEvent(string TenantId, long ConversationId, string Loai, long? MessageId);

/// <summary>Một dòng nhật ký thao tác. <c>ChiTiet</c> là JSON thô, KHÔNG chứa nội dung tin.</summary>
public class ChatAuditRow
{
    public long Id { get; set; }
    public long? ConversationId { get; set; }
    public string Username { get; set; } = "";
    public string HanhDong { get; set; } = "";
    public string? ChiTiet { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Ghi chú nội bộ về khách. <b>Khách không bao giờ thấy</b> — chỉ nhân viên đọc.</summary>
public class ChatNote
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string NoiDung { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
