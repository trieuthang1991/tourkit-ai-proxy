namespace TourkitAiProxy.Domain.Digest;

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
/// <para><b>LastSentUtc / LastSentLocalDate ĐÃ NGỪNG DÙNG.</b> Chống gửi trùng trong ngày giờ hỏi
/// thẳng Bảng tin (<c>InsightRepository.ExistsTodayAsync</c>): bản tin đã dựng thì có dòng ở đó, đó
/// mới là sự thật. Hai cột cũ còn nằm trong DB (không xoá cột) nhưng code không ghi nữa — giữ trong
/// record để đọc lại dữ liệu cũ mà không phải sửa câu SELECT.</para>
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
    // ZaloPhone: SỐ ĐIỆN THOẠI, không phải Zalo user id. Zalo gửi bằng ZNS (nhắn theo số), nên
    // người dùng chỉ cần nhập số của mình — khỏi đi đào user id vốn khác nhau theo từng OA.
    bool ChannelZalo, string? ZaloPhone,
    DateTime? LastSentUtc, DateTime? LastSentLocalDate)
{
    /// <summary>
    /// Vì sao người này đang KHÔNG nhận được bản tin. <c>null</c> = đang ổn.
    ///
    /// <para>Thêm dạng thuộc tính <c>init</c> chứ không thêm vào danh sách tham số vị trí: record
    /// này được dựng ở nhiều nơi, đổi chữ ký là vỡ hết mà chẳng được gì.</para>
    /// </summary>
    public string? NotReadyReason { get; init; }

    /// Hỏng từ khi nào — để câu nhắc nói được "mấy hôm nay" thay vì một mốc trống.
    public DateTime? NotReadySinceUtc { get; init; }

    /// Đã nhắc lúc nào. Chỉ nhắc MỘT lần rồi tắt đăng ký, nên không cần đếm số lần.
    public DateTime? NotifiedNotReadyUtc { get; init; }

    /// <summary>
    /// Chữ cho NGƯỜI DÙNG đọc, sinh từ <see cref="NotReadyReason"/>.
    ///
    /// <para>Để ở đây chứ không ở giao diện: bảng ánh xạ mã→chữ nằm MỘT chỗ. Chép sang JavaScript
    /// là hai bản, và thêm một mã mới thì giao diện lặng lẽ hiện mã kỹ thuật cho người dùng đọc.
    /// Thuộc tính tính toán nên tự vào JSON, endpoint không phải sửa gì.</para>
    /// </summary>
    public string? NotReadyLabel =>
        NotReadyReason is null ? null : BriefReadiness.ReasonLabel(NotReadyReason);

    /// Việc người dùng cần làm — cũng sinh ở máy chủ, cùng lý do với <see cref="NotReadyLabel"/>.
    public string? NotReadyAction =>
        NotReadyReason is null ? null : BriefReadiness.ActionLabel(NotReadyReason);

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
