namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Kênh gửi bản tin, đánh SỐ THỨ TỰ cho dễ tra và dễ nói chuyện ("kênh 2 lỗi").
///
/// <para><b>0 = CHƯA CHỌN GÌ</b> (không có kênh nào), KHÔNG phải "tất cả". Muốn "tất cả" thì
/// dùng cờ bit <see cref="ChannelMask.AllChannels"/> = 15. Hai ý này phải tách rõ: nếu coi 0 là
/// "tất cả" thì một bản ghi chưa cấu hình sẽ bị hiểu là bật đủ 4 kênh — sai ngược hoàn toàn.</para>
///
/// <para>Vì 0 đã dành cho "chưa chọn", kênh thật đánh số từ 1.</para>
///
/// <para>Cố ý KHÔNG dùng giá trị enum làm cờ bit: enum để người đọc, cờ bit để máy lưu.
/// Đổi qua lại bằng <see cref="ChannelMask.ToMask(DigestChannel)"/>. Nếu gộp hai vai vào một
/// thì thêm kênh thứ 5 là phải nhớ ghi 16 thay vì 5 — rất dễ ghi nhầm.</para>
/// </summary>
public enum DigestChannel
{
    /// Chưa chọn kênh nào (giá trị mặc định của số). Không phải một kênh, cũng không phải "tất cả".
    None     = 0,
    InApp    = 1,
    Email    = 2,
    Telegram = 3,
    Zalo     = 4,
}

/// <summary>
/// Cờ bit đánh dấu TỪNG KÊNH đã gửi được hay chưa, trong ngày. Lưu 1 cột <c>SentMask</c>.
///
/// <para><b>Vấn đề nó giải:</b> trước đây chỉ có một mốc <c>LastSentLocalDate</c> cho cả 4 kênh.
/// Nên <c>inapp:ok email:ok telegram:FAIL</c> vẫn bị đánh dấu "đã gửi hôm nay" → Telegram
/// KHÔNG BAO GIỜ được thử lại, và người dùng im lặng không nhận được gì mà chẳng ai biết.</para>
///
/// <para>Có mask thì mỗi lượt chỉ gửi phần <c>còn thiếu = đang bật &amp; chưa gửi</c>:
/// Telegram hỏng lúc 7h thì 8h thử lại — chỉ Telegram, không gửi lại email.</para>
///
/// <para><b>Rủi ro đã biết, chấp nhận có ý thức:</b> kênh trả <c>false</c> KHÔNG chắc là chưa gửi
/// (vd Telegram nhận tin rồi mới timeout) → thử lại có thể làm người dùng nhận 2 lần. Không tránh
/// được triệt để, nên chặn bằng <see cref="MaxAttemptsPerDay"/>: thà trùng hiếm hơn là mất tin.</para>
/// </summary>
public static class ChannelMask
{
    public const int None = 0;

    // Bit = 1 << (số thứ tự − 1). Viết tường minh cho dễ đọc khi soi DB.
    public const int InApp    = 1 << 0;   // kênh 1 → 1
    public const int Email    = 1 << 1;   // kênh 2 → 2
    public const int Telegram = 1 << 2;   // kênh 3 → 4
    public const int Zalo     = 1 << 3;   // kênh 4 → 8

    /// Mọi kênh hiện có. Thêm kênh mới thì SỬA Ở ĐÂY (và chỉ ở đây).
    public const int AllChannels = InApp | Email | Telegram | Zalo;   // 15

    /// Số lần thử gửi tối đa trong 1 ngày cho 1 người. Chặn vòng lặp thử lại vô tận khi
    /// một kênh hỏng suốt (vd token Zalo hết hạn) — và giảm rủi ro gửi trùng.
    public const int MaxAttemptsPerDay = 3;

    /// Số thứ tự kênh → cờ bit. <see cref="DigestChannel.None"/> (0 = chưa chọn) → 0.
    public static int ToMask(DigestChannel ch) => ch switch
    {
        DigestChannel.None     => None,
        DigestChannel.InApp    => InApp,
        DigestChannel.Email    => Email,
        DigestChannel.Telegram => Telegram,
        DigestChannel.Zalo     => Zalo,
        _                      => None,
    };

    /// Id kênh trong code (<c>IDigestChannel.Id</c>) → số thứ tự. Id lạ trả <c>null</c> để chỗ gọi
    /// tự quyết — cố ý KHÔNG quy về 0 hay 15, vì cả hai đều là kết luận sai về một id không hiểu được.
    public static DigestChannel? FromId(string? id) => id switch
    {
        "inapp"    => DigestChannel.InApp,
        "email"    => DigestChannel.Email,
        "telegram" => DigestChannel.Telegram,
        "zalo"     => DigestChannel.Zalo,
        _          => null,
    };

    /// Cờ bit của 1 id kênh. Id lạ → 0 (không đánh dấu gì).
    public static int MaskOfId(string? id)
    {
        var ch = FromId(id);
        return ch == null ? None : ToMask(ch.Value);
    }

    /// Các kênh người này ĐANG BẬT (theo cấu hình đăng ký), bất kể gửi được hay chưa.
    /// Bật mà thiếu thông tin nhận → KHÔNG tính, nếu không nó nằm mãi trong danh sách còn-thiếu.
    public static int EnabledOf(DigestSubscription s)
        => (s.ChannelInApp ? InApp : 0)
         | (s.ChannelEmail && !string.IsNullOrWhiteSpace(s.Email) ? Email : 0)
         | (s.ChannelTelegram && !string.IsNullOrWhiteSpace(s.TelegramChatId) ? Telegram : 0)
         | (s.ChannelZalo && !string.IsNullOrWhiteSpace(s.ZaloUserId) ? Zalo : 0);

    /// Còn phải gửi những kênh nào: đang bật NHƯNG chưa gửi được.
    public static int Pending(int enabled, int sent) => enabled & ~sent;

    public static bool Has(int mask, int bit) => (mask & bit) == bit;

    /// <summary>Lọc/tìm kiếm theo kênh. Quy ước của 2 cực:
    /// <list type="bullet">
    /// <item><c>wanted = 0</c> → người dùng CHƯA chọn kênh nào ⇒ không lọc gì, lấy hết.</item>
    /// <item><c>wanted = 15</c> → đòi có ĐỦ cả 4 kênh.</item>
    /// </list>
    /// Ở giữa thì đòi có đủ đúng những kênh đã tick (AND, không phải OR) — người tick email+zalo
    /// là muốn "có cả hai", không phải "có một trong hai".</summary>
    public static bool MatchesFilter(int mask, int wanted)
        => wanted == None || (mask & wanted) == wanted;

    /// Có ÍT NHẤT một kênh trong nhóm quan tâm. <c>wanted = 0</c> → false (chưa chọn thì không có gì để tìm).
    public static bool HasAny(int mask, int wanted) => (mask & wanted) != None;

    /// Tên kênh tiếng Việt để ghi vào tóm tắt lượt chạy cho người đọc hiểu.
    public static string Describe(int mask)
    {
        if (mask == None) return "(không kênh nào)";
        var names = new List<string>(4);
        if (Has(mask, InApp)) names.Add("trong app");
        if (Has(mask, Email)) names.Add("email");
        if (Has(mask, Telegram)) names.Add("telegram");
        if (Has(mask, Zalo)) names.Add("zalo");
        return string.Join("+", names);
    }
}
