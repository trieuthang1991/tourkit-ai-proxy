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
        => PendingFor(sub, utcNow) != ChannelMask.None;

    /// <summary>
    /// Cần gửi những kênh nào NGAY BÂY GIỜ. Trả 0 = không phải lúc, hoặc không còn gì để gửi.
    ///
    /// <para>Hai tình huống cho ra việc:</para>
    /// <list type="number">
    /// <item><b>Gửi lần đầu trong ngày</b> — đúng giờ VN người dùng chọn.</item>
    /// <item><b>Thử lại phần còn thiếu</b> — đã gửi hôm nay nhưng có kênh hỏng (vd Telegram sập lúc
    /// 7h) thì các giờ sau trong CÙNG NGÀY thử lại, và chỉ thử đúng kênh còn thiếu — không gửi lại
    /// email cho người đã nhận. Trước khi có mask, cả 4 kênh dùng chung một mốc ngày nên kênh hỏng
    /// im lặng mất tin luôn.</item>
    /// </list>
    ///
    /// <para>Chặn bằng <see cref="ChannelMask.MaxAttemptsPerDay"/>: kênh hỏng suốt cả ngày (token
    /// Zalo hết hạn) không được thử lại mỗi giờ, vừa vô ích vừa tăng rủi ro gửi trùng.</para>
    /// </summary>
    public static int PendingFor(DigestSubscription sub, DateTime utcNow)
    {
        if (!sub.Enabled) return ChannelMask.None;

        var enabled = ChannelMask.EnabledOf(sub);
        if (enabled == ChannelMask.None) return ChannelMask.None;   // bật bản tin nhưng chưa có nơi nhận

        var vn = NowVn(utcNow);
        // .Date cả 2 vế: cột DB là DATE nhưng nếu chỗ nào lỡ nhét kèm giờ thì vẫn so đúng theo ngày.
        var sentToday = sub.LastSentLocalDate?.Date == vn.Date;

        if (!sentToday)
            return vn.Hour == DigestSubscription.ClampHour(sub.SendHourLocal) ? enabled : ChannelMask.None;

        // Bản ghi CŨ (có từ trước khi thêm SentMask): đã gửi hôm nay mà mask=0 & attempts=0.
        // Coi như đã gửi đủ — nếu tính là "còn thiếu cả 4" thì ngày nâng cấp ai cũng nhận tin 2 lần.
        if (sub.SentMask == ChannelMask.None && sub.SentAttempts == 0) return ChannelMask.None;

        if (sub.SentAttempts >= ChannelMask.MaxAttemptsPerDay) return ChannelMask.None;

        return ChannelMask.Pending(enabled, sub.SentMask);
    }

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
