using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Digest;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Ảnh chụp bản tin XUYÊN TENANT cho trang admin: ai đăng ký nhận gì, hôm nay gửi được kênh nào.
///
/// <para><b>Câu hỏi nó trả lời</b> mà không chỗ nào khác trả lời được: "có ai đăng ký rồi mà bản tin
/// chưa từng tới không?". Có 3 kiểu hỏng đều IM LẶNG với người dùng — họ chỉ thấy sáng ra không có
/// gì, không có lỗi nào hiện lên:</para>
/// <list type="number">
/// <item>Người dùng bật nhận nhưng <b>công ty chưa bật lịch</b> chạy tác vụ.</item>
/// <item>Bật kênh nhưng <b>thiếu nơi nhận</b> (tick email mà bỏ trống địa chỉ).</item>
/// <item>Lịch bật, nơi nhận đủ, nhưng <b>kênh gửi hỏng</b> — thấy qua mask thiếu bit hoặc hết lượt thử.</item>
/// </list>
/// </summary>
public class AdminDigestRepository
{
    private readonly TourkitAiDb _db;

    public AdminDigestRepository(TourkitAiDb db) { _db = db; }

    /// <summary>
    /// DTO có setter — KHÔNG đọc thẳng vào record. <c>SendHourLocal</c>/<c>SentMask</c>/
    /// <c>SentAttempts</c> là TINYINT nên Dapper đi tìm constructor nhận <c>byte</c>; đây đúng là cái
    /// bẫy đã làm nổ <c>DigestSubscriptions</c> rồi <c>AgentInsights</c> trong cùng một ngày.
    /// </summary>
    private sealed class Row
    {
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string BriefType { get; set; } = "";
        public bool Enabled { get; set; }
        public int SendHourLocal { get; set; }
        public bool ChannelInApp { get; set; }
        public bool ChannelEmail { get; set; }
        public string? Email { get; set; }
        public bool ChannelTelegram { get; set; }
        public string? TelegramChatId { get; set; }
        public bool ChannelZalo { get; set; }
        public string? ZaloUserId { get; set; }
        public int SentMask { get; set; }
        public int SentAttempts { get; set; }
        public DateTime? LastSentUtc { get; set; }
        public DateTime? LastSentLocalDate { get; set; }
        public bool? ScheduleEnabled { get; set; }
        public string? PausedReason { get; set; }
        public DateTime? UpdatedUtc { get; set; }
    }

    /// 1 dòng cho trang admin. Cờ <c>Problem</c> tính sẵn ở server để mọi nơi hiểu "đang hỏng" giống nhau.
    public record DigestAdminRow(
        string TenantId, string Username, string BriefType,
        bool Enabled, int SendHourLocal,
        string ChannelsEnabled, string ChannelsSentToday, string ChannelsMissingToday,
        int SentAttempts, DateTime? LastSentUtc, DateTime? LastSentLocalDate,
        bool ScheduleEnabled, string? PausedReason,
        string? Problem, DateTime? UpdatedUtc);

    public async Task<List<DigestAdminRow>> ListAsync(string? tenantId, string? briefType,
        bool problemsOnly, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);

        // LEFT JOIN dbo.UserWorkflows với Username='' vì 2 tác vụ bản tin đều là PerTenant
        // (1 bản ghi lịch cho cả công ty) — xem WorkflowScope.PerTenant.
        var rows = await c.QueryAsync<Row>(@"
SELECT s.TenantId, s.Username, s.BriefType, s.Enabled, s.SendHourLocal,
       s.ChannelInApp, s.ChannelEmail, s.Email,
       s.ChannelTelegram, s.TelegramChatId, s.ChannelZalo, s.ZaloUserId,
       s.SentMask, s.SentAttempts, s.LastSentUtc, s.LastSentLocalDate, s.UpdatedUtc,
       w.Enabled AS ScheduleEnabled, w.PausedReason
FROM dbo.DigestSubscriptions s
LEFT JOIN dbo.UserWorkflows w
       ON w.TenantId = s.TenantId AND w.Username = '' AND w.WorkflowType = s.BriefType
WHERE (@tenantId IS NULL OR s.TenantId = @tenantId)
  AND (@briefType IS NULL OR s.BriefType = @briefType)
ORDER BY s.TenantId, s.BriefType, s.Username",
            new { tenantId, briefType });

        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        var list = new List<DigestAdminRow>();

        foreach (var r in rows)
        {
            var sub = new DigestSubscription(
                r.TenantId, r.Username, r.BriefType, r.Enabled, r.SendHourLocal,
                r.ChannelInApp, r.ChannelEmail, r.Email,
                r.ChannelTelegram, r.TelegramChatId, r.ChannelZalo, r.ZaloUserId,
                r.LastSentUtc, r.LastSentLocalDate, r.SentMask, r.SentAttempts);

            var enabledMask = ChannelMask.EnabledOf(sub);
            // Mask chỉ có nghĩa TRONG NGÀY: sang ngày mới nó chưa reset cho tới lượt gửi đầu tiên,
            // nên đọc mask của ngày cũ sẽ báo "đã gửi" cho hôm nay — sai.
            var sentToday = r.LastSentLocalDate?.Date == todayVn ? r.SentMask : ChannelMask.None;
            var missing = ChannelMask.Pending(enabledMask, sentToday);
            var scheduleOn = r.ScheduleEnabled == true && string.IsNullOrEmpty(r.PausedReason);

            list.Add(new DigestAdminRow(
                r.TenantId, r.Username, r.BriefType, r.Enabled, r.SendHourLocal,
                ChannelsEnabled: ChannelMask.Describe(enabledMask),
                ChannelsSentToday: ChannelMask.Describe(sentToday),
                ChannelsMissingToday: ChannelMask.Describe(missing),
                r.SentAttempts, r.LastSentUtc, r.LastSentLocalDate,
                scheduleOn, r.PausedReason,
                Problem: DetectProblem(r, sub, enabledMask, missing, scheduleOn, todayVn),
                r.UpdatedUtc));
        }

        return problemsOnly ? list.Where(x => x.Problem != null).ToList() : list;
    }

    /// <summary>
    /// Vì sao người này chưa nhận được bản tin. Trả null = không có gì bất thường.
    /// Xếp theo thứ tự nguyên nhân GỐC trước: lịch tắt thì mọi thứ phía sau đều vô nghĩa.
    /// </summary>
    private static string? DetectProblem(Row r, DigestSubscription sub, int enabledMask,
        int missing, bool scheduleOn, DateTime todayVn)
    {
        if (!r.Enabled) return null;   // tự tắt thì không phải sự cố

        if (!scheduleOn)
            return string.IsNullOrEmpty(r.PausedReason)
                ? "Đã đăng ký nhưng công ty CHƯA BẬT lịch chạy"
                : $"Lịch chạy đang tạm dừng: {r.PausedReason}";

        if (enabledMask == ChannelMask.None)
            return "Bật nhận nhưng chưa có nơi nhận nào dùng được (thiếu email/chat id/user id)";

        if (r.LastSentUtc == null)
            return "Đã bật, lịch đang chạy, nhưng CHƯA GỬI được lần nào";

        // Chỉ soi mask khi đúng là dữ liệu của hôm nay (xem ghi chú ở ListAsync).
        if (r.LastSentLocalDate?.Date == todayVn && missing != ChannelMask.None)
            return r.SentAttempts >= ChannelMask.MaxAttemptsPerDay
                ? $"Hết lượt thử trong ngày, còn thiếu: {ChannelMask.Describe(missing)}"
                : $"Hôm nay còn thiếu: {ChannelMask.Describe(missing)}";

        return null;
    }
}
