// Domain/Chat/ChannelFailures.cs
namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Vì sao một tin KHÔNG gửi được. Tám nhóm, chung cho cả sáu kênh.
///
/// <para><b>Vì sao phải phân nhóm thay vì giữ nguyên câu lỗi.</b> Trước 28/08/2026 mỗi lượt gửi
/// hỏng chỉ để lại một chuỗi chữ do nhà cung cấp trả về — thường bằng tiếng Anh, thường là mã số
/// trần trụi kiểu <c>(#551) This person isn't available right now</c>. Từ chuỗi đó không ai trả
/// lời được hai câu hỏi mà hệ thống BẮT BUỘC phải trả lời ngay:</para>
/// <list type="number">
///   <item><b>Thử lại có ích không?</b> Mạng chập thì thử lại là đúng; khách đã chặn công ty thì
///     thử lại năm lần chỉ tốn lượt gọi rồi vẫn hỏng, mà tin vẫn nằm im trong hàng đợi khiến
///     những tin sau nó tới muộn theo.</item>
///   <item><b>Ai phải làm gì?</b> Khoá đăng nhập hỏng thì phải gọi người quản trị nối lại kênh;
///     hết hạn mức thì phải nạp thêm; nội dung sai thì người trực sửa tin rồi gửi lại. Ba việc
///     khác nhau hoàn toàn, nhưng trên màn hình cũ chúng hiện y hệt nhau.</item>
/// </list>
///
/// <para>Giá trị số được ghi xuống CSDL nên <b>chỉ thêm ở cuối</b>, không đánh số lại.</para>
/// </summary>
public enum ChatFailure : short
{
    /// <summary>Không nhận ra. Vẫn cho thử lại — thà tốn một lượt còn hơn nuốt mất tin vì mình
    /// chưa kịp học mã lỗi mới của nhà cung cấp.</summary>
    Unknown = 0,

    /// <summary>Mạng chập, nhà cung cấp 5xx, dịch vụ đang bảo trì. Thử lại.</summary>
    Network = 1,

    /// <summary>Gọi quá dày. Thử lại — nhưng nên giãn ra.</summary>
    RateLimited = 2,

    /// <summary>Khoá đăng nhập sai/hết hạn/bị thu hồi. Thử lại vô ích: phải NỐI LẠI kênh.</summary>
    AuthFailed = 3,

    /// <summary>Thiếu quyền, hoặc nền tảng đang khoá tài khoản vì vi phạm chính sách.</summary>
    PermissionDenied = 4,

    /// <summary>Hết hạn mức (số tin, số tiền, hoặc cửa sổ chăm sóc khách đã đóng).</summary>
    QuotaExceeded = 5,

    /// <summary>Khách chặn công ty, hoặc đã từ chối nhận loại tin này.</summary>
    UserBlocked = 6,

    /// <summary>Người nhận không tồn tại / đã xoá tài khoản / mã người nhận sai.</summary>
    InvalidRecipient = 7,

    /// <summary>Nội dung tin sai: quá dài, thiếu tham số, mẫu chưa duyệt, tệp hỏng.</summary>
    PayloadInvalid = 8,
}

/// <summary>
/// Đọc mã lỗi thô của từng kênh thành <see cref="ChatFailure"/>, rồi từ đó suy ra "thử lại
/// không" và "người trực phải làm gì".
///
/// <para><b>Bảng mã chép từ dự án tham chiếu (ChatbotX)</b>, không tự nghĩ. Đây là loại tri thức
/// chỉ tích được bằng cách chạy thật rồi gặp lỗi thật: không tài liệu nào của Meta hay Zalo nói
/// "mã -216 nghĩa là khách chặn OA nên đừng thử lại". Ngồi suy ra là suy sai.</para>
///
/// <para><b>Hàm thuần, không gọi mạng</b> — vì thế test phủ được từng mã một.</para>
/// </summary>
public static class ChannelFailures
{
    // ── Hai câu hỏi mà mọi chỗ gọi cần trả lời ─────────────────────────────

    /// <summary>
    /// Thử lại có ích không.
    ///
    /// <para><see cref="ChatFailure.Unknown"/> trả <c>true</c> là CỐ Ý: mã lạ thường là mã mới của
    /// nhà cung cấp, mà nuốt mất tin của khách thì tệ hơn tốn một lượt gọi. Hàng đợi vẫn có trần
    /// số lần thử nên không quay vòng vô tận.</para>
    /// </summary>
    public static bool ShouldRetry(ChatFailure loi) => loi switch
    {
        ChatFailure.Network => true,
        ChatFailure.RateLimited => true,
        ChatFailure.Unknown => true,
        _ => false,
    };

    /// <summary>
    /// Có phải kênh đã <b>đứt</b> và cần người quản trị nối lại không.
    ///
    /// <para>Khác <see cref="ShouldRetry"/>: cả hai nhóm dưới đây đều không đáng thử lại, nhưng
    /// chỉ hai nhóm này là <b>hỏng ở cấp KÊNH</b> — mọi tin gửi sau đó cũng sẽ hỏng y hệt. Những
    /// nhóm còn lại chỉ hỏng ở cấp MỘT tin hoặc MỘT khách, kênh vẫn chạy bình thường cho người
    /// khác. Lẫn hai mức đó với nhau là chỗ sinh ra hai lỗi trái ngược: hoặc báo động "mất kênh"
    /// mỗi lần một khách chặn, hoặc im lặng suốt trong khi cả kênh đã chết.</para>
    /// </summary>
    public static bool NeedsReconnect(ChatFailure loi) =>
        loi is ChatFailure.AuthFailed or ChatFailure.PermissionDenied;

    /// <summary>Câu ngắn hiện thẳng cho người trực. Không có mã số, không có tiếng Anh.</summary>
    public static string Label(ChatFailure loi) => loi switch
    {
        ChatFailure.Network => "Nhà cung cấp đang lỗi",
        ChatFailure.RateLimited => "Gửi quá dày",
        ChatFailure.AuthFailed => "Kênh đã mất kết nối",
        ChatFailure.PermissionDenied => "Kênh bị nền tảng hạn chế",
        ChatFailure.QuotaExceeded => "Hết hạn mức gửi",
        ChatFailure.UserBlocked => "Khách đã chặn",
        ChatFailure.InvalidRecipient => "Không tìm thấy người nhận",
        ChatFailure.PayloadInvalid => "Nội dung tin không hợp lệ",
        _ => "Chưa gửi được",
    };

    /// <summary>Việc cần làm. Đi kèm <see cref="Label"/> — nhãn nói CHUYỆN GÌ, đây nói LÀM GÌ.</summary>
    public static string Fix(ChatFailure loi) => loi switch
    {
        ChatFailure.Network => "Hệ thống sẽ tự gửi lại, không cần làm gì.",
        ChatFailure.RateLimited => "Hệ thống sẽ tự giãn ra rồi gửi lại.",
        ChatFailure.AuthFailed => "Vào Cài đặt kênh và bấm nối lại kênh này.",
        ChatFailure.PermissionDenied => "Kiểm tra tài khoản trên trang quản trị của nền tảng — "
            + "thường là thiếu quyền hoặc đang bị khoá vì vi phạm chính sách.",
        ChatFailure.QuotaExceeded => "Nạp thêm hạn mức, hoặc chờ sang kỳ mới.",
        ChatFailure.UserBlocked => "Không gửi được cho khách này nữa. Liên hệ bằng cách khác.",
        ChatFailure.InvalidRecipient => "Người nhận không còn tồn tại trên kênh này.",
        ChatFailure.PayloadInvalid => "Sửa lại nội dung tin rồi gửi lại.",
        _ => "Xem lại nhật ký kênh để biết chi tiết.",
    };

    // ── Meta: Messenger + Instagram (chung mã Graph) ───────────────────────

    private static readonly HashSet<int> MetaAuth =
        new() { 102, 190, 458, 459, 460, 463, 464, 467, 492 };

    private static readonly HashSet<int> MetaPermission = new() { 3, 10, 341, 368 };
    private static readonly HashSet<int> MetaRate = new() { 4, 17 };
    private static readonly HashSet<int> MetaPayload = new() { 506, 1_609_005 };
    private static readonly HashSet<int> MetaNetwork = new() { 1, 2 };

    /// <summary>Mã 551 = "người này hiện không nhận tin" — thực tế là khách đã chặn Trang.</summary>
    private static readonly HashSet<int> MetaBlocked = new() { 551 };

    /// <summary>
    /// Mã 200 của Meta là "thiếu quyền" nói chung, nhưng riêng mã phụ 1545041 nghĩa là
    /// <b>khách tự tắt nhận tin</b> — hai việc khác hẳn nhau, và chỉ mã phụ mới phân biệt được.
    /// </summary>
    private const long MetaOptedOutSubCode = 1_545_041;

    /// <param name="type">Trường <c>error.type</c> của Graph. <c>OAuthException</c> là lưới vét
    /// cuối: Meta đẻ mã mới liên tục, nhưng loại lỗi thì giữ nguyên tên.</param>
    public static ChatFailure FromMeta(int? code, long? subCode = null, string? type = null)
    {
        if (code is not { } c)
            return string.Equals(type, "OAuthException", StringComparison.OrdinalIgnoreCase)
                ? ChatFailure.AuthFailed : ChatFailure.Unknown;

        if (MetaAuth.Contains(c)) return ChatFailure.AuthFailed;
        if (MetaRate.Contains(c)) return ChatFailure.RateLimited;
        if (MetaBlocked.Contains(c)) return ChatFailure.UserBlocked;
        if (c == 200 && subCode == MetaOptedOutSubCode) return ChatFailure.UserBlocked;

        // 200–299 là dải "quyền" của Graph. Xét SAU mã phụ ở trên, vì 200 vừa là dải quyền vừa là
        // chỗ Meta nhét lỗi khách-tắt-nhận-tin vào.
        if (MetaPermission.Contains(c) || c is >= 200 and <= 299) return ChatFailure.PermissionDenied;

        if (MetaNetwork.Contains(c)) return ChatFailure.Network;
        if (MetaPayload.Contains(c)) return ChatFailure.PayloadInvalid;

        return string.Equals(type, "OAuthException", StringComparison.OrdinalIgnoreCase)
            ? ChatFailure.AuthFailed : ChatFailure.Unknown;
    }

    /// <summary>
    /// Bốn mã phụ dưới đây đi cùng mã 190 nghĩa là khoá đã bị <b>thu hồi hẳn</b>, không phải hết
    /// hạn tạm. Phân biệt để biết khi nào cần bảo người dùng nối lại NGAY thay vì chờ tự làm mới.
    /// </summary>
    private static readonly HashSet<long> MetaRevoked = new() { 458, 460, 463, 467 };

    public static bool MetaTokenRevoked(int? code, long? subCode) =>
        code == 190 && subCode is { } s && MetaRevoked.Contains(s);

    // ── WhatsApp Cloud API: cùng nhà Meta nhưng bảng mã riêng ──────────────

    private static readonly HashSet<int> WaAuth =
        new() { 0, 190, 133_005, 133_008, 133_009 };

    private static readonly HashSet<int> WaPermission = new()
    {
        3, 10, 368, 130_497, 131_005, 131_031, 131_042, 131_057, 131_064,
        133_000, 133_006, 133_010, 133_015,
    };

    private static readonly HashSet<int> WaRate =
        new() { 4, 80_007, 130_429, 131_048, 131_056, 133_016 };

    /// <summary>131047 = cửa sổ chăm sóc khách 24 giờ đã đóng — phải đi bằng mẫu duyệt sẵn.</summary>
    private static readonly HashSet<int> WaQuota = new() { 131_047 };

    private static readonly HashSet<int> WaBlocked =
        new() { 130_403, 131_026, 131_049, 131_050 };

    private static readonly HashSet<int> WaInvalidRecipient = new() { 33, 130_472, 131_021 };

    private static readonly HashSet<int> WaPayload = new()
    {
        100, 131_008, 131_009, 131_037, 131_052, 131_053, 131_055,
        132_000, 132_001, 132_005, 132_007, 132_012, 132_015, 132_016, 132_018,
        132_068, 132_069, 134_100, 134_101, 134_102, 135_000,
    };

    private static readonly HashSet<int> WaNetwork =
        new() { 1, 2, 131_000, 131_016, 133_004 };

    public static ChatFailure FromWhatsApp(int? code)
    {
        if (code is not { } c) return ChatFailure.Unknown;
        if (WaAuth.Contains(c)) return ChatFailure.AuthFailed;
        if (WaRate.Contains(c)) return ChatFailure.RateLimited;
        if (WaQuota.Contains(c)) return ChatFailure.QuotaExceeded;
        if (WaBlocked.Contains(c)) return ChatFailure.UserBlocked;
        if (WaInvalidRecipient.Contains(c)) return ChatFailure.InvalidRecipient;
        if (WaPermission.Contains(c)) return ChatFailure.PermissionDenied;
        if (WaPayload.Contains(c)) return ChatFailure.PayloadInvalid;
        if (WaNetwork.Contains(c)) return ChatFailure.Network;
        return ChatFailure.Unknown;
    }

    // ── Zalo OA: mã ÂM, và nhiều nhất trong sáu kênh ───────────────────────

    private static readonly HashSet<int> ZaloAuth =
        new() { -101, -103, -104, -124, -1241, -148, -149, -1491, -150 };

    private static readonly HashSet<int> ZaloPermission =
        new() { -117, -120, -1202, -135, -1351, -136, -138, -1381, -145 };

    private static readonly HashSet<int> ZaloQuota =
        new() { -115, -126, -144, -1441, -147, -1471, -1472, -160 };

    private static readonly HashSet<int> ZaloBlocked = new() { -139, -140, -141, -216 };
    private static readonly HashSet<int> ZaloInvalidRecipient = new() { -108, -118 };

    private static readonly HashSet<int> ZaloPayload = new()
    {
        -107, -109, -1091, -111, -112, -1121, -1122, -1123, -1124, -113, -1131, -1132,
        -116, -121, -122, -125, -127, -130, -131, -132, -142, -143, -151, -152, -153,
        -158, -159, -161, -162, -249,
    };

    private static readonly HashSet<int> ZaloNetwork = new() { -137 };

    public static ChatFailure FromZalo(int? code)
    {
        if (code is not { } c) return ChatFailure.Unknown;
        if (ZaloAuth.Contains(c)) return ChatFailure.AuthFailed;
        if (ZaloPermission.Contains(c)) return ChatFailure.PermissionDenied;
        if (ZaloQuota.Contains(c)) return ChatFailure.QuotaExceeded;
        if (ZaloBlocked.Contains(c)) return ChatFailure.UserBlocked;
        if (ZaloInvalidRecipient.Contains(c)) return ChatFailure.InvalidRecipient;
        if (ZaloPayload.Contains(c)) return ChatFailure.PayloadInvalid;
        if (ZaloNetwork.Contains(c)) return ChatFailure.Network;
        return ChatFailure.Unknown;
    }

    // ── Telegram và TikTok: không có mã số ổn định, phải đọc CHỮ ───────────

    /// <summary>
    /// Telegram trả lỗi bằng <c>description</c> viết cho người đọc ("bot was blocked by the user")
    /// chứ không phải mã số phân loại được. Đối chiếu chuỗi con là cách duy nhất — dự án tham
    /// chiếu cũng làm y hệt.
    /// </summary>
    private static readonly (string[] Tu, ChatFailure Loi)[] TelegramWords =
    {
        (new[] { "auth_key_unregistered", "auth_key_invalid", "auth_key_perm_empty",
                 "auth_key_duplicated", "session_revoked", "session_expired", "api_id_invalid",
                 "unauthorized" }, ChatFailure.AuthFailed),
        (new[] { "user_is_blocked", "you_blocked_user", "user_privacy_restricted",
                 "user_not_participant", "chat_write_forbidden", "bot was blocked by the user",
                 "bot can't initiate conversation with a user", "bot can't send messages to bots",
                 "user is restricted" }, ChatFailure.UserBlocked),
        (new[] { "user_deactivated", "phone_number_unoccupied", "user is deactivated",
                 "chat not found", "user not found", "the group chat was deleted",
                 "group chat was upgraded to a supergroup", "bot was kicked", "chat_id is empty",
                 "peer_id_invalid" }, ChatFailure.InvalidRecipient),
        (new[] { "chat_admin_required", "not enough rights",
                 "method is available only for supergroups", "bot is not a member" },
            ChatFailure.PermissionDenied),
        (new[] { "flood_wait", "flood_premium_wait", "slowmode_wait", "2fa_confirm_wait",
                 "too many requests", "retry after" }, ChatFailure.RateLimited),
        (new[] { "file_migrate", "phone_migrate", "network_migrate", "user_migrate",
                 "etimedout", "econnreset", "webhook error" }, ChatFailure.Network),
        (new[] { "message is too long", "wrong file identifier", "failed to get http url content",
                 "entity too large", "button_data_invalid", "query is too old", "bad webhook",
                 "file is too big", "image_process_failed", "message to delete not found",
                 "message can't be deleted", "photo_invalid_dimensions", "file_part_invalid",
                 "md5_checksum_invalid", "terminated by other getupdates" },
            ChatFailure.PayloadInvalid),
    };

    /// <param name="httpStatus">Chỉ dùng khi không câu chữ nào khớp. Telegram trả 403 cho ca
    /// khách chặn bot chứ không phải "thiếu quyền" như phần lớn API khác.</param>
    public static ChatFailure FromTelegram(int httpStatus, string? description)
    {
        var loi = MatchWords(TelegramWords, description);
        if (loi is { } l) return l;

        return httpStatus switch
        {
            303 => ChatFailure.Network,
            401 => ChatFailure.AuthFailed,
            403 => ChatFailure.UserBlocked,
            420 or 429 => ChatFailure.RateLimited,
            400 or 404 or 405 or 406 or 409 => ChatFailure.PayloadInvalid,
            >= 500 => ChatFailure.Network,
            _ => ChatFailure.Unknown,
        };
    }

    private static readonly (string[] Tu, ChatFailure Loi)[] TikTokWords =
    {
        (new[] { "access_token_invalid", "access_token_expired", "token_not_valid",
                 "unauthorized", "invalid_token", "token revoked", "auth_failed" },
            ChatFailure.AuthFailed),
        (new[] { "user_blocked", "message_blocked", "user_restricted", "cannot send message",
                 "dm_disabled" }, ChatFailure.UserBlocked),
        (new[] { "user_not_found", "open_id_not_found", "recipient_not_found",
                 "invalid_open_id" }, ChatFailure.InvalidRecipient),
        (new[] { "permission_denied", "scope_not_authorized", "insufficient_scope",
                 "not_authorized" }, ChatFailure.PermissionDenied),
        (new[] { "rate_limit_exceeded", "too_many_requests", "quota_exceeded" },
            ChatFailure.RateLimited),
        (new[] { "etimedout", "econnreset", "webhook error" }, ChatFailure.Network),
        (new[] { "message_too_long", "invalid_message", "bad_request", "parameter_invalid" },
            ChatFailure.PayloadInvalid),
    };

    public static ChatFailure FromTikTok(int httpStatus, string? description)
    {
        var loi = MatchWords(TikTokWords, description);
        if (loi is { } l) return l;

        return httpStatus switch
        {
            401 => ChatFailure.AuthFailed,
            403 => ChatFailure.PermissionDenied,
            404 => ChatFailure.InvalidRecipient,
            429 => ChatFailure.RateLimited,
            400 => ChatFailure.PayloadInvalid,
            >= 500 => ChatFailure.Network,
            _ => ChatFailure.Unknown,
        };
    }

    /// <summary>Lưới vét cuối khi chỉ có mã HTTP trần, không đọc được thân trả về.</summary>
    public static ChatFailure FromHttp(int httpStatus) => httpStatus switch
    {
        401 => ChatFailure.AuthFailed,
        403 => ChatFailure.PermissionDenied,
        404 => ChatFailure.InvalidRecipient,
        429 => ChatFailure.RateLimited,
        >= 500 => ChatFailure.Network,
        >= 400 => ChatFailure.PayloadInvalid,
        _ => ChatFailure.Unknown,
    };

    private static ChatFailure? MatchWords((string[] Tu, ChatFailure Loi)[] bang, string? chu)
    {
        if (string.IsNullOrWhiteSpace(chu)) return null;
        var thap = chu!.ToLowerInvariant();
        foreach (var (tu, loi) in bang)
            foreach (var t in tu)
                if (thap.Contains(t, StringComparison.Ordinal))
                    return loi;
        return null;
    }
}
