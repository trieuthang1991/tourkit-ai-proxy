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

/// <summary>
/// Tìm cuộc trò chuyện phía máy chủ — hàm thuần, có test. Tên người nằm ở danh bạ ERP (không ở CSDL
/// chat), nên chỗ gọi so tên bằng <see cref="NameMatches"/> rồi lọc phòng theo mã người; tên phòng / mã
/// bản ghi lọc trong SQL bằng <see cref="LikePattern"/>.
/// </summary>
public static class InternalRoomSearch
{
    /// Bỏ dấu tiếng Việt (kể cả đ/Đ), chữ thường, gộp khoảng trắng — "Trần THU Hà" → "tran thu ha".
    public static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// Tên có chứa chuỗi tìm không (không phân biệt hoa thường, có dấu hay không). Chuỗi tìm rỗng → false.
    public static bool NameMatches(string? name, string? q)
    {
        var f = Fold(q);
        return f.Length > 0 && Fold(name).Contains(f, StringComparison.Ordinal);
    }

    /// Mẫu cho <c>ILIKE … ESCAPE '\'</c>: thoát % _ \ để người gõ "50%" không thành ký tự đại diện.
    /// Rỗng → null (không lọc).
    public static string? LikePattern(string? q)
    {
        var t = q?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return "%" + t.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
    }
}

/// <summary>Dòng xem trước của một tin — chung cho câu ghi tin và tóm tắt sự kiện, để hai nơi không lệch nhau.</summary>
public static class InternalMessagePreview
{
    public const int MaxLength = 200;

    /// Chữ nếu có, không thì "[Ảnh] tên" / "[Tệp] tên"; cắt còn 200 ký tự.
    public static string Of(string? body, InternalAttachment? attachment)
    {
        var s = !string.IsNullOrEmpty(body) ? body
            : attachment is null ? "" : (attachment.Kind == "anh" ? "[Ảnh] " : "[Tệp] ") + attachment.Name;
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }
}

/// <summary>
/// Tham số tự do trang nhúng gửi kèm phòng theo bản ghi (<c>data</c> của openEntity / openByKey): lưu
/// nguyên, trả lại cho mọi thành viên. Riêng <c>url</c> có nghĩa — widget hiện nút "Xem lại" mở nó.
/// </summary>
public static class InternalRoomData
{
    /// Trần cỡ (byte UTF-8 của JSON gọn). Đủ cho vài chục trường; không để thành chỗ chứa tài liệu.
    public const int MaxBytes = 4096;

    /// <returns>(JSON gọn để lưu, null) · (null, null) = không truyền · (null, lỗi) = chặn.</returns>
    public static (string? Json, string? Error) Normalize(System.Text.Json.JsonElement? data)
    {
        if (data is not { } d || d.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            return (null, null);
        if (d.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, "data phải là một object");
        if (d.TryGetProperty("url", out var u)
            && (u.ValueKind != System.Text.Json.JsonValueKind.String || SafeUrl(u.GetString()) is null))
            return (null, "data.url phải là đường dẫn http(s) đầy đủ");
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) return (null, $"data tối đa {MaxBytes / 1024} KB");
        return (json, null);
    }

    /// Đường dẫn tuyệt đối http/https; còn lại (javascript:, data:, tương đối…) → null.
    public static string? SafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var x) && (x.Scheme == Uri.UriSchemeHttp || x.Scheme == Uri.UriSchemeHttps)
            ? url : null;
}

public record InternalMember(int UserId, DateTime JoinedUtc);

/// <param name="LastMessageId">Mã tin mới nhất của phòng — widget chỉ cộng chưa đọc theo sự kiện có mã LỚN
/// hơn, nên một tin vừa có trong danh sách vừa tới qua sự kiện không bị đếm hai lần.</param>
/// <param name="Data">Tham số tự do của phòng (<see cref="InternalRoomData"/>); null = không có.</param>
public record InternalRoom(long Id, string? Title, string EntityType, string? EntityId, string RoomKey,
    string? LastPreview, DateTime LastActivityAt, int UnreadCount, IReadOnlyList<InternalMember> Members,
    long? LastMessageId = null, System.Text.Json.JsonElement? Data = null);

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
    string? EntityId = null, System.Text.Json.JsonElement? Data = null);

/// <summary>Thay tham số tự do của phòng (<see cref="InternalRoomData"/>); Data null = xoá.</summary>
public record RoomDataRequest(System.Text.Json.JsonElement? Data);

public record RenameRoomRequest(string? Title);
public record EditMessageRequest(string? Body);
public record AddMembersRequest(List<int>? MemberUserIds);

/// <summary>Gửi tin: chữ, tệp đính kèm (đã tải lên qua /upload), hoặc cả hai — phải có ít nhất một.</summary>
public record SendInternalMessageRequest(string? Body, InternalAttachment? Attachment = null);

/// <summary>Kết quả ghi tin. <c>Id</c> null = người gửi không phải thành viên (chỗ gọi trả 403).
/// <c>Members</c> để bus biết giao sự kiện cho ai — lấy ngay trong câu ghi, không hỏi lại.</summary>
public record AppendResult(long? Id, DateTime? CreatedUtc, int[] Members);
