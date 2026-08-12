namespace TourkitAiProxy.Services.Digest;

/// Loại bản tin (khớp WorkflowType của workflow tương ứng).
public static class BriefTypes
{
    public const string Sale = "sale-brief";
    public const string Ceo  = "ceo-brief";
    public static bool IsValid(string? t) => t == Sale || t == Ceo;
}

/// Đăng ký nhận bản tin per-(tenant, user, loại).
///
/// <para><b>SendHourLocal</b> = giờ theo GIỜ VIỆT NAM người dùng muốn nhận. Workflow chạy mỗi 60'
/// rồi tự chọn ai "đến giờ" — KHÔNG cần scheduler biết giờ trong ngày.</para>
///
/// <para><b>LastSentLocalDate</b> (ngày VN, không phải UTC) chống gửi trùng trong ngày: đã gửi
/// hôm nay rồi thì các tick sau trong cùng ngày bỏ qua. Dùng ngày VN vì mốc "hôm nay" của người
/// dùng là theo giờ VN; lấy ngày UTC sẽ sai lệch 7 tiếng ở khoảng 0h–7h sáng.</para>
///
/// <para><b>Kênh gửi tách 2 tầng</b>: bản ghi này giữ NƠI NHẬN của từng người (email/Zalo/Telegram).
/// Còn TÀI KHOẢN GỬI ĐI (OA Zalo, bot Telegram) là cấu hình của công ty, nằm ở
/// <c>dbo.TenantChannelSettings</c> — xem ghi chú "NGUYÊN TẮC CHUNG" trong plan Đợt 1.</para>
/// </summary>
public record DigestSubscription(
    string TenantId, string Username, string BriefType,
    bool Enabled, int SendHourLocal,
    bool ChannelInApp, bool ChannelEmail, string? Email,
    bool ChannelTelegram, string? TelegramChatId,
    bool ChannelZalo, string? ZaloUserId,
    DateTime? LastSentUtc, DateTime? LastSentLocalDate)
{
    /// Giờ gửi hợp lệ 0–23; giá trị rác → 7h sáng (default an toàn).
    public static int ClampHour(int h) => h is >= 0 and <= 23 ? h : 7;
}

/// Thông điệp bản tin đã render — mọi kênh dùng chung 1 nguồn.
/// Có sẵn cả markdown lẫn HTML để kênh nào cần dạng nào thì lấy dạng đó, không phải render lại.
public record DigestMessage(string Title, string BodyMarkdown, string BodyHtml, string Kind, int Severity = 0);

/// 1 dòng Insight Feed (dbo.AgentInsights). Username='' = tenant-wide (mọi người trong công ty thấy).
public record AgentInsight(
    long Id, string TenantId, string Username, string Kind, int Severity,
    string Title, string Body, string? DataJson, string? AlertKey,
    bool IsRead, DateTime CreatedUtc);
