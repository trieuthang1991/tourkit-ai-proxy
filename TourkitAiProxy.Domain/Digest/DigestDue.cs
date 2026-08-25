namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Chọn đăng ký "đến lúc chuẩn bị bản tin": từ mốc (giờ người chọn − cửa sổ chuẩn bị) trở đi, tính
/// theo giờ VIỆT NAM. Chống dựng trùng trong ngày do caller lo (<c>InsightRepository.ExistsTodayAsync</c>).
///
/// <para><b>Vì sao mọi thứ tính theo giờ VN chứ không UTC:</b> mốc "hôm nay" của người dùng là theo
/// giờ VN. Lấy ngày UTC thì trong khoảng 0h–7h sáng VN (17h–0h UTC hôm trước) sẽ ra ngày hôm trước —
/// đúng khung giờ bản tin sáng chạy, tức sai ngay ca dùng chính.</para>
/// </summary>
public static class DigestDue
{
    private static readonly TimeZoneInfo VnTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

    /// Giờ VN tương ứng với 1 mốc UTC. SpecifyKind phòng caller truyền DateTime Kind=Unspecified
    /// (Dapper đọc DATETIME2 ra Unspecified) — không có nó thì ConvertTimeFromUtc ném exception.
    public static DateTime NowVn(DateTime utcNow)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), VnTz);

    // IsDue/PendingFor đã GỠ (13/08). Chúng thuộc mô hình cũ "đến giờ thì gửi ngay, kênh nào hỏng
    // thì giờ sau thử lại theo cờ bit". Nay workflow chỉ CHUẨN BỊ trước giờ (ShouldPrepare) rồi bỏ
    // vào hàng đợi kèm mốc gửi (SendMomentUtc) — giờ gửi là việc của hàng đợi, không phải của lượt chạy.

    /// <summary>
    /// Đã tới lúc CHUẨN BỊ bản tin hôm nay chưa: từ mốc (giờ gửi − leadMinutes) trở đi, cho tới hết
    /// ngày VN. So theo PHÚT (bản cũ so Hour == làm MẤT bản tin nếu server sập trọn khung giờ).
    /// "Hôm nay đã chuẩn bị chưa" do caller kiểm (InsightRepository.ExistsTodayAsync) — hàm này thuần.
    /// </summary>
    public static bool ShouldPrepare(DigestSubscription sub, DateTime utcNow, int leadMinutes)
    {
        if (!sub.Enabled) return false;
        var vn = NowVn(utcNow);
        var openToday = vn.Date.AddHours(DigestSubscription.ClampHour(sub.SendHourLocal))
                              .AddMinutes(-Math.Max(0, leadMinutes));
        return vn >= openToday;
    }

    /// <summary>
    /// Mốc UTC để đặt ScheduledUtc: đúng giờ người chọn (giờ VN đổi ra UTC); đã QUA giờ (dựng muộn
    /// do sập/lỡ cửa sổ) → gửi ngay (trả utcNow). Trả Kind=Unspecified để ghi thẳng DATETIME2.
    /// </summary>
    public static DateTime SendMomentUtc(DigestSubscription sub, DateTime utcNow)
    {
        var vn = NowVn(utcNow);
        var sendAtVn = vn.Date.AddHours(DigestSubscription.ClampHour(sub.SendHourLocal));
        if (vn >= sendAtVn) return DateTime.SpecifyKind(utcNow, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(sendAtVn, DateTimeKind.Unspecified), VnTz);
    }
}
