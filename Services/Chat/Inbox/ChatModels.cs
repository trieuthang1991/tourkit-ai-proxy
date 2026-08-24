// Services/Chat/Inbox/ChatModels.cs
namespace TourkitAiProxy.Services.Chat.Inbox;

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
    string? SeenMarker = null);

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
    public DateTime? AgentLastReadAt { get; set; }
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
