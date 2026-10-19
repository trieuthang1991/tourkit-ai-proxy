namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Khoá phòng chat nội bộ, ghi vào <c>chat_conversations.contact_external_id</c> (channel = 7).
///
/// <para>Chống trùng nằm ở chỉ mục duy nhất sẵn có <c>ux_conv_scope_surface</c>, nên khoá phải
/// SINH RA GIỐNG HỆT cho cùng một phòng: 1-1 xếp hai mã tăng dần (A mở với B hay B mở với A là
/// cùng phòng), đơn/công ty mỗi thứ đúng một phòng. Ghi chuỗi rỗng là mỗi công ty chỉ tạo được
/// đúng một phòng — dòng thứ hai vỡ chỉ mục.</para>
/// </summary>
public static class InternalRoomKey
{
    public const short Channel = (short)ChatChannel.Internal;

    /// "deal" = Cơ hội bán hàng (BookingTicket) — RIÊNG với "order": khác thực thể ở CRM, dùng chung
    /// khoá thì cơ hội #12 và đơn #12 rơi vào cùng một phòng.
    public static bool IsValidType(string? t) => t is "direct" or "order" or "company" or "deal" or "group" or "key";

    /// <summary>
    /// Khoá TỰ DO do trang gọi tự sinh ("donhang_123", "deal_12131") → "key:donhang_123". Lấy-hoặc-tạo:
    /// cùng khoá luôn ra cùng một phòng trong công ty. Không kiểm quyền CRM (như order/company) — ai
    /// có phiên hợp lệ trong công ty và biết khoá thì vào được. Chỉ chữ, số và _ - . : (tối đa 100).
    /// null = khoá không hợp lệ.
    /// </summary>
    public static string? ForCustomKey(string? raw)
    {
        var k = raw?.Trim();
        if (string.IsNullOrEmpty(k) || k.Length > 100) return null;
        foreach (var ch in k)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':')) return null;
        return "key:" + k;
    }
    public static bool IsEntityType(string? t) => t is "order" or "company" or "deal";

    /// <summary>
    /// Khoá của phòng gắn với MỘT bản ghi (đơn / công ty / cơ hội / khoá tự đặt) — để HỎI phòng đã có
    /// chưa trước khi tạo. Cùng cách chuẩn hoá với đường tạo phòng (mã cơ hội "012" = "12"), không thì
    /// hỏi một đằng tạo một nẻo. 1-1 / nhóm không hỏi theo cách này → null. Mã hỏng → null.
    /// </summary>
    public static string? ForLookup(string? entityType, string? entityId)
    {
        var t = entityType?.Trim().ToLowerInvariant();
        if (t == "key") return ForCustomKey(entityId);
        if (!IsEntityType(t)) return null;
        var id = t == "deal" ? CanonicalDealId(entityId) : entityId?.Trim();
        return string.IsNullOrEmpty(id) ? null : ForEntity(t!, id);
    }

    public static string Direct(int a, int b)
    {
        if (a == b) throw new ArgumentException("Phòng 1-1 cần hai người khác nhau");
        return a < b ? $"direct:{a}:{b}" : $"direct:{b}:{a}";
    }

    public static string ForEntity(string entityType, string entityId)
    {
        if (!IsEntityType(entityType))
            throw new ArgumentException($"Loại phòng theo ngữ cảnh không hợp lệ: {entityType}");
        var id = entityId?.Trim();
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Thiếu mã đơn/công ty");
        return $"{entityType}:{id}";
    }

    public static string Group() => "group:" + Guid.NewGuid().ToString("N");

    /// Mã cơ hội dạng chuẩn ("012", "+12", " 12 " → "12"); không phải số dương → null. Không chuẩn hoá
    /// thì cùng một cơ hội ra nhiều khoá → nhiều phòng.
    public static string? CanonicalDealId(string? raw)
        => long.TryParse(raw?.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
               System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// Câu thông báo hệ thống trong phòng ("A đã thêm B…") — để ai đổi gì trong phòng đều để lại dấu,
/// không thì không ai truy được người nào đã bỏ ai. Hàm thuần, chữ cho người dùng cuối.
/// </summary>
public static class InternalRoomNotice
{
    /// <param name="viec">"đổi tên" | "thêm" | "bỏ" | "rời".</param>
    /// <param name="gi">Tên phòng mới / danh sách tên người — tuỳ việc.</param>
    public static string Text(string ai, string viec, string? gi) => viec switch
    {
        "đổi tên" => string.IsNullOrWhiteSpace(gi) ? $"{ai} đã bỏ tên phòng" : $"{ai} đã đổi tên phòng thành “{gi}”",
        "thêm" => $"{ai} đã thêm {gi} vào phòng",
        "bỏ" => $"{ai} đã bỏ {gi} khỏi phòng",
        "rời" => $"{ai} đã rời phòng",
        _ => $"{ai} đã thay đổi phòng",
    };
}

public record InternalMember(int UserId, DateTime JoinedUtc);

public record InternalRoom(long Id, string? Title, string EntityType, string? EntityId, string RoomKey,
    string? LastPreview, DateTime LastActivityAt, int UnreadCount, IReadOnlyList<InternalMember> Members);

public record InternalMessage(long Id, long RoomId, int? SenderUserId, string? SenderUsername,
    string? Body, DateTime CreatedUtc, DateTime? DeletedUtc, InternalAttachment? Attachment = null,
    DateTime? EditedUtc = null, bool System = false);

/// <summary>Tệp đính kèm một tin nội bộ. <c>Kind</c>: "anh" | "tep" — cùng chữ với hộp thư khách.</summary>
public record InternalAttachment(string Url, string Name, long Size, string Kind);

/// <summary>Luật tệp đính kèm chat nội bộ — hàm thuần để test được không cần CSDL/kho.</summary>
public static class InternalAttachmentRules
{
    public const long MaxBytes = 15 * 1024 * 1024;

    /// <summary>
    /// Đuôi tệp được nhận. DANH SÁCH TRẮNG chứ không danh sách đen: kho local phục vụ tệp ngay trên
    /// miền của proxy, nên một tệp <c>.html</c>/<c>.svg</c> tải lên là chạy được script trên miền đó
    /// — nơi trình duyệt đang giữ phiên đăng nhập.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".zip", ".rar",
    };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static bool IsAllowed(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName) && Allowed.Contains(Path.GetExtension(fileName));

    public static string KindOf(string fileName) => Images.Contains(Path.GetExtension(fileName)) ? "anh" : "tep";

    /// Khoá kho: theo công ty + phòng, tên tệp là GUID (tên gốc giữ ở tin, không ở đường dẫn).
    public static string KeyPrefix(string tenant, long roomId) => $"internal/{tenant}/{roomId}/";

    /// <summary>
    /// URL đính kèm có phải do CHÍNH kho này cấp cho ĐÚNG công ty + phòng này không. Không kiểm thì
    /// ai cũng gửi được một link tuỳ ý (kể cả <c>javascript:</c>) đóng vai "tệp đính kèm".
    /// </summary>
    /// <param name="bases">Các đoạn đầu hợp lệ của URL kho (PublicBase của r2/s3, hoặc
    /// <c>{scheme}://{host}/chat-files/</c> với local), mỗi đoạn kết thúc bằng "/".</param>
    public static bool IsOwnUrl(string? url, string tenant, long roomId, IEnumerable<string> bases)
    {
        // Kho không bao giờ cấp URL có %, ? hay # — chặn hết thay vì đoán từng kiểu mã hoá (%2e%2e
        // trình duyệt tự giải thành "..", thoát khỏi thư mục của phòng).
        if (string.IsNullOrWhiteSpace(url) || url.Contains("..", StringComparison.Ordinal)
            || url.IndexOfAny(new[] { '%', '?', '#' }) >= 0) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return false;
        var prefix = KeyPrefix(tenant, roomId);
        return bases.Any(b => url.StartsWith(b + prefix, StringComparison.Ordinal));
    }
}

public record CreateRoomRequest(string? Title, List<int>? MemberUserIds, string EntityType = "direct",
    string? EntityId = null);

public record RenameRoomRequest(string? Title);
public record EditMessageRequest(string? Body);
public record AddMembersRequest(List<int>? MemberUserIds);

/// <summary>Gửi tin: chữ, tệp đính kèm (đã tải lên qua /upload), hoặc cả hai — phải có ít nhất một.</summary>
public record SendInternalMessageRequest(string? Body, InternalAttachment? Attachment = null);

/// <summary>Kết quả ghi tin. <c>Id</c> null = người gửi không phải thành viên (chỗ gọi trả 403).
/// <c>Members</c> để bus biết giao sự kiện cho ai — lấy ngay trong câu ghi, không hỏi lại.</summary>
public record AppendResult(long? Id, DateTime? CreatedUtc, int[] Members);
