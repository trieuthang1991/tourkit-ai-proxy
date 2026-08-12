namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Chọn đăng ký "đến giờ gửi": đúng giờ VIỆT NAM người dùng chọn, và hôm nay (theo ngày VN) chưa gửi.
///
/// <para>Workflow bản tin chạy mỗi 60' → mỗi giờ VN chỉ khớp đúng 1 lần. <c>LastSentLocalDate</c>
/// là chốt chặn thứ hai, chống gửi trùng khi có 2 instance cùng chạy hoặc workflow bị chạy tay.</para>
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

    public static bool IsDue(DigestSubscription sub, DateTime utcNow)
    {
        if (!sub.Enabled) return false;
        var vn = NowVn(utcNow);
        if (vn.Hour != DigestSubscription.ClampHour(sub.SendHourLocal)) return false;
        // .Date cả 2 vế: cột DB là DATE nhưng nếu chỗ nào lỡ nhét kèm giờ thì vẫn so đúng theo ngày.
        return sub.LastSentLocalDate?.Date != vn.Date;
    }
}
