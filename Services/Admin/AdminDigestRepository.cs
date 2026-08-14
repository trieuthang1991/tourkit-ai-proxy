using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Ảnh chụp bản tin XUYÊN TENANT cho trang admin: ai đăng ký nhận gì, hôm nay bản tin đi tới đâu.
///
/// <para><b>Câu hỏi nó trả lời</b> mà không chỗ nào khác trả lời được: "có ai đăng ký rồi mà bản tin
/// chưa từng tới không?". Có 3 kiểu hỏng đều IM LẶNG với người dùng — họ chỉ thấy sáng ra không có
/// gì, không có lỗi nào hiện lên:</para>
/// <list type="number">
/// <item>Người dùng bật nhận nhưng <b>công ty chưa bật lịch</b> chạy tác vụ.</item>
/// <item>Bật kênh nhưng <b>thiếu nơi nhận</b> (tick email mà bỏ trống địa chỉ).</item>
/// <item>Lịch bật, nơi nhận đủ, nhưng <b>kênh gửi hỏng</b> — thấy qua dòng hàng đợi Status=2.</item>
/// </list>
///
/// <para><b>Nguồn "hôm nay tới đâu" là HÀNG ĐỢI, không phải cờ bit trên bản đăng ký nữa.</b>
/// Từ khi bản tin chuyển sang chuẩn bị-trước-rồi-gửi-qua-hàng-đợi, mỗi kênh là một dòng riêng có
/// trạng thái riêng — đọc thẳng ở đó vừa đúng hơn (biết cả "đang chờ tới giờ") vừa không phải
/// canh mốc ngày như cờ bit cũ.</para>
/// </summary>
public class AdminDigestRepository
{
    private readonly TourkitAiDb _db;

    public AdminDigestRepository(TourkitAiDb db) { _db = db; }

    /// <summary>
    /// DTO có setter — KHÔNG đọc thẳng vào record. <c>SendHourLocal</c> là TINYINT nên Dapper đi
    /// tìm constructor nhận <c>byte</c>; đây đúng là cái bẫy đã làm nổ <c>DigestSubscriptions</c>
    /// rồi <c>AgentInsights</c> trong cùng một ngày.
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
        public DateTime? LastSentUtc { get; set; }
        public bool? ScheduleEnabled { get; set; }
        public string? PausedReason { get; set; }
        public DateTime? UpdatedUtc { get; set; }
    }

    /// 1 dòng hàng đợi gom theo (người, kênh, trạng thái) trong ngày hôm nay.
    private sealed class QueueStat
    {
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public int Channel { get; set; }
        public int Status { get; set; }
        public int Cnt { get; set; }
        public DateTime? LastProcessedUtc { get; set; }
    }

    /// 1 dòng cho trang admin. Cờ <c>Problem</c> tính sẵn ở server để mọi nơi hiểu "đang hỏng" giống nhau.
    public record DigestAdminRow(
        string TenantId, string Username, string BriefType,
        bool Enabled, int SendHourLocal,
        string ChannelsEnabled, string ChannelsSentToday, string ChannelsFailedToday,
        string ChannelsPendingToday,
        DateTime? LastSentUtc,
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
       s.LastSentUtc, s.UpdatedUtc,
       w.Enabled AS ScheduleEnabled, w.PausedReason
FROM dbo.DigestSubscriptions s
LEFT JOIN dbo.UserWorkflows w
       ON w.TenantId = s.TenantId AND w.Username = '' AND w.WorkflowType = s.BriefType
WHERE (@tenantId IS NULL OR s.TenantId = @tenantId)
  AND (@briefType IS NULL OR s.BriefType = @briefType)
ORDER BY s.TenantId, s.BriefType, s.Username",
            new { tenantId, briefType });

        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(todayVn, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));

        // Trạng thái giao HÔM NAY, đọc từ hàng đợi. Gom sẵn 1 lượt cho mọi tenant rồi tra trong bộ
        // nhớ — trang admin xem xuyên tenant, hỏi từng người sẽ thành N+1 truy vấn.
        var stats = await c.QueryAsync<QueueStat>(@"
SELECT TenantId, Username, Channel, [Status], COUNT(*) AS Cnt, MAX(ProcessedUtc) AS LastProcessedUtc
FROM dbo.OutboundMails
WHERE Kind = @kind AND CreatedUtc >= @fromUtc
GROUP BY TenantId, Username, Channel, [Status]",
            new { kind = DigestEnqueuePlanner.Kind, fromUtc });

        var byUser = stats
            .Where(s => !string.IsNullOrEmpty(s.Username))
            .GroupBy(s => (s.TenantId, s.Username!))
            .ToDictionary(g => g.Key, g => g.ToList());

        var list = new List<DigestAdminRow>();

        foreach (var r in rows)
        {
            var sub = new DigestSubscription(
                r.TenantId, r.Username, r.BriefType, r.Enabled, r.SendHourLocal,
                r.ChannelInApp, r.ChannelEmail, r.Email,
                r.ChannelTelegram, r.TelegramChatId, r.ChannelZalo, r.ZaloUserId,
                r.LastSentUtc, LastSentLocalDate: null);

            var enabled = OutboundChannels.EnabledOf(sub);
            byUser.TryGetValue((r.TenantId, r.Username), out var mine);

            var sent = ChannelsWithStatus(mine, OutboundMailStatus.Sent);
            var failed = ChannelsWithStatus(mine, OutboundMailStatus.Failed);
            var pending = ChannelsWithStatus(mine, OutboundMailStatus.Pending);
            var scheduleOn = r.ScheduleEnabled == true && string.IsNullOrEmpty(r.PausedReason);

            // "Gửi được lần cuối" lấy từ hàng đợi chứ không phải cột LastSentUtc trên bản đăng ký —
            // cột đó đã ngừng ghi từ khi chuyển sang pipeline hàng đợi, đọc vào chỉ thấy số cũ.
            var lastSentUtc = mine?
                .Where(s => s.Status == OutboundMailStatus.Sent && s.LastProcessedUtc != null)
                .Select(s => DateTime.SpecifyKind(s.LastProcessedUtc!.Value, DateTimeKind.Utc))
                .DefaultIfEmpty()
                .Max();
            if (lastSentUtc == default) lastSentUtc = null;

            list.Add(new DigestAdminRow(
                r.TenantId, r.Username, r.BriefType, r.Enabled, r.SendHourLocal,
                // In-app luôn có mặt: bản tin luôn được lưu ở Bảng tin, kể cả khi không bật kênh ngoài nào.
                ChannelsEnabled: Describe(enabled, withInApp: true),
                ChannelsSentToday: Describe(sent, withInApp: false),
                ChannelsFailedToday: Describe(failed, withInApp: false),
                ChannelsPendingToday: Describe(pending, withInApp: false),
                lastSentUtc, scheduleOn, r.PausedReason,
                Problem: DetectProblem(r, enabled, failed, scheduleOn),
                r.UpdatedUtc));
        }

        return problemsOnly ? list.Where(x => x.Problem != null).ToList() : list;
    }

    private static List<OutboundChannel> ChannelsWithStatus(List<QueueStat>? stats, byte status)
        => stats == null
            ? new List<OutboundChannel>()
            : stats.Where(s => s.Status == status)
                   .Select(s => (OutboundChannel)s.Channel)
                   .Distinct()
                   .OrderBy(ch => (byte)ch)
                   .ToList();

    private static string Describe(List<OutboundChannel> channels, bool withInApp)
    {
        var names = new List<string>(4);
        if (withInApp) names.Add("trong app");
        names.AddRange(channels.Select(OutboundChannels.Describe));
        return names.Count == 0 ? "(không kênh nào)" : string.Join("+", names);
    }

    /// <summary>
    /// Vì sao người này chưa nhận được bản tin. Trả null = không có gì bất thường.
    /// Xếp theo thứ tự nguyên nhân GỐC trước: lịch tắt thì mọi thứ phía sau đều vô nghĩa.
    /// </summary>
    private static string? DetectProblem(Row r, List<OutboundChannel> enabled,
        List<OutboundChannel> failed, bool scheduleOn)
    {
        if (!r.Enabled) return null;   // tự tắt thì không phải sự cố

        if (!scheduleOn)
            return string.IsNullOrEmpty(r.PausedReason)
                ? "Đã đăng ký nhưng công ty CHƯA BẬT lịch chạy"
                : $"Lịch chạy đang tạm dừng: {r.PausedReason}";

        // Bật kênh ngoài mà bỏ trống nơi nhận → kênh đó không bao giờ vào được hàng đợi.
        var declared = (r.ChannelEmail ? 1 : 0) + (r.ChannelTelegram ? 1 : 0) + (r.ChannelZalo ? 1 : 0);
        if (declared > enabled.Count)
            return "Có kênh đã bật nhưng bỏ trống nơi nhận (email/chat id/user id)";

        if (failed.Count > 0)
            return $"Kênh gửi hỏng hôm nay: {Describe(failed, withInApp: false)}";

        return null;
    }
}
