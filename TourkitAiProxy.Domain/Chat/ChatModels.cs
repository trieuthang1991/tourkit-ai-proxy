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
    /// <summary>Instagram Direct. Đi CÙNG hợp đồng nhắn tin của Meta với Messenger — xem
    /// <c>MetaMessagingParser</c> — nhưng khác đường gửi, khác khoá ký, và KHÔNG có báo
    /// "đã nhận".</summary>
    Instagram = 4,
    /// <summary>WhatsApp Cloud API. Cũng của Meta nhưng <b>hợp đồng khác hẳn</b> Messenger:
    /// gói tin là <c>entry[].changes[].value</c>, báo trạng thái theo <c>id</c> từng tin, và
    /// tệp khách gửi phải tải bằng khoá — không có URL công khai.</summary>
    WhatsApp = 5,
    /// <summary>TikTok Direct Message cho tài khoản doanh nghiệp.</summary>
    TikTok = 6,
}

/// Chiều của tin nhắn.
public enum ChatDirection : short { In = 0, Out = 1 }

/// Ai gửi. Tách AI với nhân viên vì người đọc CẦN biết câu nào do máy trả lời.
public enum ChatSender : short { Customer = 0, Ai = 1, Agent = 2, System = 3 }

/// Loại nội dung.
public enum ChatKind : short { Text = 0, Image = 1, File = 2, Audio = 3, Sticker = 4, Location = 5 }

/// Trạng thái vòng đời một tin GỬI ĐI. Thiếu delivered/seen/failed thì nhân viên không phân biệt
/// được "khách chưa đọc" với "gửi hỏng" — hai thứ dẫn tới hai hành động trái ngược.
public enum ChatState : short { Pending = 0, Sent = 1, Delivered = 2, Seen = 3, Failed = 4 }

/// Trạng thái xử lý một hội thoại.
public enum ChatStatus : short { New = 0, InProgress = 1, Closed = 2 }

/// <summary>
/// Một nút bấm gắn dưới tin nhắn.
///
/// <para><b>Chỉ hai kiểu, cố ý.</b> Dự án tham chiếu gắn vào nút một <c>payload</c> trỏ tới
/// bước trong luồng của nó — bên mình KHÔNG có trình dựng luồng, bot là trợ lý AI đọc CRM. Nên
/// nút ở đây mang nghĩa đơn giản hơn nhiều và không cần máy chạy luồng nào:</para>
/// <list type="bullet">
///   <item><b>Mở liên kết</b> (<see cref="Url"/> có giá trị) — bấm là mở trang.</item>
///   <item><b>Trả lời nhanh</b> (<see cref="Url"/> rỗng) — bấm là khách <b>NÓI ĐÚNG CÂU trên
///     nút</b>. Nền tảng gửi lại chữ đó như một tin của khách, rồi trợ lý xử như mọi câu khác.</item>
/// </list>
///
/// <para>Nhờ vế thứ hai mà nút không cần thêm cơ chế nào: bộ bóc tin <b>vốn đã</b> ghi lượt bấm
/// bằng CHỮ TRÊN NÚT chứ không phải mã kỹ thuật — xem <c>MetaMessagingParser</c>. Một vòng khép
/// kín, không có trạng thái nào phải giữ giữa hai lượt.</para>
/// </summary>
/// <param name="Label">Chữ trên nút. Cũng CHÍNH LÀ câu khách nói khi bấm, ở nút trả lời nhanh.</param>
public record ChatButton(string Label, string? Url = null)
{
    public bool IsLink => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Một sự kiện đến từ kênh, đã chuẩn hoá — lõi không cần biết kênh nào sinh ra nó.</summary>
/// <param name="IsEcho">Tin do CHÍNH OA/Page gửi, nhận lại dưới dạng tiếng vọng — nghĩa là có người
/// đang trả lời từ app của kênh (Zalo OA, Facebook Page). Phải ghi lại VÀ cho bot câm, nếu không
/// bot sẽ nói đè lên người thật.</param>
/// <param name="Watermark">Nền tảng báo lại trạng thái tin MÌNH đã gửi — không phải tin nhắn mới.
/// Xem <see cref="StateWatermark"/>.</param>
/// <param name="ButtonClickId">Mã lượt bấm nút cần xác nhận lại với kênh. Chỉ Telegram có:
/// không xác nhận thì nút quay vòng trên máy khách tới lúc hết giờ rồi báo lỗi, dù mình đã
/// xử lý xong. Xem <c>IChatChannelAdapter.AckButtonClickAsync</c>.</param>
/// <param name="IsHistory">Tin CŨ do nền tảng trả về lúc nối, không phải tin vừa xảy ra.
///
/// <para><b>Phải đi đường riêng, không dùng lại đường tin thường.</b> Ba việc lõi làm với mỗi
/// tin của khách đều SAI với tin cũ: sinh một câu trả lời của trợ lý (một năm lịch sử là hàng
/// trăm câu trả lời gửi thẳng cho khách hôm nay), cho bot câm 30 phút, và chờ gộp tin. Ngoài
/// ra tin cũ phải giữ đúng <see cref="InboundChatEvent.SentUtc"/> làm thời điểm — đóng dấu giờ
/// nhập là cả năm hội thoại dồn vào một phút.</para></param>
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
    StateWatermark? Watermark = null,
    ChatReaction? Reaction = null,
    ChatReferral? Referral = null,
    string? ButtonClickId = null,
    bool IsHistory = false);

/// <summary>
/// Khách đến từ đâu — quảng cáo nào, liên kết nào, mã QR nào.
///
/// <para><b>Nhà cung cấp chỉ nói MỘT LẦN</b>, ngay lúc khách mở cuộc trò chuyện. Không ghi lại
/// lúc đó là mất vĩnh viễn: không có API nào tra ngược được "khách này đến từ quảng cáo nào".
/// Đây là dữ liệu bán hàng thật, không phải thông tin kỹ thuật.</para>
/// </summary>
/// <param name="Nguon">ADS · SHORTLINK · CUSTOMER_CHAT_PLUGIN…</param>
/// <param name="Ref">Tham số <c>ref</c> do CHÍNH MÌNH đặt trên liên kết m.me hoặc mã QR.</param>
public record ChatReferral(string? Source, string? Ref, string? AdId);

/// <summary>
/// Khách thả (hoặc gỡ) cảm xúc lên MỘT tin đã có.
///
/// <para><b>Không phải một tin nhắn mới.</b> Ghi nó thành tin trong hội thoại là dòng thời gian
/// loạn ngay: biểu tượng "❤️" hiện như một câu khách vừa nói, và mọi thứ đếm theo tin (chưa đọc,
/// xem trước, cửa sổ trả lời) đều lệch.</para>
///
/// <para><b>Dạng chung cho mọi kênh</b>, cố ý không gắn cứng vào Meta: Zalo và Telegram cũng có
/// cảm xúc nhưng đặt tên trường khác hẳn. Cái bất biến là "ai, thả gì, lên tin nào".</para>
/// </summary>
/// <param name="ExternalMsgId">Tin BỊ thả — mã của nhà cung cấp, không phải id nội bộ.</param>
/// <param name="Bo"><c>true</c> = GỠ cảm xúc. Bỏ sót nhánh này là cảm xúc đã gỡ vẫn hiện mãi.</param>
public record ChatReaction(string ExternalMsgId, string? Emoji, string? Name, bool Removed);

/// <summary>Một cảm xúc đọc lên từ CSDL, để đính vào tin lúc liệt kê.</summary>
public class ChatReactionRow
{
    public string ExternalMsgId { get; set; } = "";
    public string ActorExternalId { get; set; } = "";
    public string? Emoji { get; set; }
    public string? ReactionName { get; set; }
}

/// <summary>
/// Nền tảng báo lại trạng thái tin MÌNH đã gửi, theo kiểu <b>mốc nước</b>: mọi tin gửi trước
/// <paramref name="UpToUtc"/> đều đã đạt <paramref name="State"/>.
///
/// <para>Thay cho <c>SeenMarker</c> cũ (chuỗi <c>"seen"</c>): chuỗi đó chỉ nói được "đã xem",
/// không nói được "đã nhận", và không mang thời điểm — mà thiếu thời điểm thì hoặc đánh dấu cả
/// hội thoại (sai: tin gửi sau đó cũng bị coi là đã xem), hoặc không đánh dấu gì.</para>
/// </summary>
/// <param name="ExternalMsgId">Instagram KHÔNG gửi <c>watermark</c> — chỉ gửi mã tin cuối
/// khách đã đọc. Mốc thời gian phải tra ngược từ chính tin đó, mà tra thì phải chạm CSDL nên
/// không làm được trong hàm bóc tin thuần. Có giá trị ở đây nghĩa là <c>UpToUtc</c> chưa dùng
/// được, lõi phải tra trước.</param>
public record StateWatermark(ChatState State, DateTime UpToUtc, string? ExternalMsgId = null);

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
    int Unread,
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
    public string? AvatarUrl { get; set; }     // ghép từ chat_contacts khi liệt kê
    public string? ReferralSource { get; set; }
    public string? ReferralRef { get; set; }
    public string? ReferralAdId { get; set; }
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
    /// Mã tin của nhà cung cấp. Khoá để gắn cảm xúc — cảm xúc tới theo mã này, không theo id nội bộ.
    public string? ExternalMsgId { get; set; }
    public short State { get; set; }
    /// Nút đã gửi kèm tin, dạng JSON. Xem <see cref="ChatButton"/>.
    public string? Buttons { get; set; }
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
    public static string Encode(ConvCursor c)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
               $"{c.LastActivityAt.Ticks}|{c.Id}"))
           .TrimEnd('=').Replace('+', '-').Replace('/', '_');   // base64url — đi trên URL không phải escape

    /// <summary>Mã hỏng → <c>null</c>, KHÔNG ném: con trỏ nằm trên URL nên người dùng sửa tay được,
    /// và mã cũ từ bản trước vẫn có thể còn trong lịch sử trình duyệt. Ném là cả trang trắng.</summary>
    public static ConvCursor? Decode(string? s)
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

/// <summary>Một dòng nhật ký thao tác. <c>Detail</c> là JSON thô, KHÔNG chứa nội dung tin.</summary>
public class ChatAuditRow
{
    public long Id { get; set; }
    public long? ConversationId { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Ghi chú nội bộ về khách. <b>Khách không bao giờ thấy</b> — chỉ nhân viên đọc.</summary>
public class ChatNote
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
