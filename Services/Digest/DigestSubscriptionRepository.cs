using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Digest;

/// dbo.DigestSubscriptions — sổ người nhận bản tin: ai nhận loại nào, mấy giờ, qua kênh nào.
public class DigestSubscriptionRepository
{
    private readonly TourkitAiDb _db;

    public DigestSubscriptionRepository(TourkitAiDb db) { _db = db; }

    private const string Cols = @"TenantId, Username, BriefType, Enabled, SendHourLocal,
ChannelInApp, ChannelEmail, Email, ChannelTelegram, TelegramChatId, ChannelZalo, ZaloPhone,
LastSentUtc, LastSentLocalDate";

    /// <summary>
    /// DTO trung gian có setter, KHÔNG đọc thẳng vào record <see cref="DigestSubscription"/>.
    /// Lý do (lỗi thật gặp 12/08): Dapper khớp constructor theo ĐÚNG kiểu cột — <c>SendHourLocal</c>
    /// là TINYINT nên nó tìm constructor nhận <c>byte</c>, mà record khai <c>int</c> → ném
    /// "A parameterless default constructor or one matching signature ... is required".
    /// DTO có setter thì Dapper tự ép kiểu, và đây cũng là lối <c>TkSessionRepository</c> đang dùng.
    /// </summary>
    private sealed class SubRow
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
        public string? ZaloPhone { get; set; }
        public DateTime? LastSentUtc { get; set; }
        public DateTime? LastSentLocalDate { get; set; }

        public DigestSubscription ToModel() => new(
            TenantId, Username, BriefType, Enabled, SendHourLocal,
            ChannelInApp, ChannelEmail, Email,
            ChannelTelegram, TelegramChatId, ChannelZalo, ZaloPhone,
            LastSentUtc, LastSentLocalDate);
    }

    public async Task<List<DigestSubscription>> ListForUserAsync(string tenant, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<SubRow>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND Username = @username",
            new { tenant, username });
        return rows.Select(r => r.ToModel()).ToList();
    }

    /// Danh sách người nhận đang bật của 1 loại bản tin trong 1 công ty. Workflow duyệt cái này
    /// rồi lọc tiếp bằng DigestDue.IsDue để biết ai "đến giờ".
    public async Task<List<DigestSubscription>> ListEnabledAsync(string tenant, string briefType, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<SubRow>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND BriefType = @briefType AND Enabled = 1",
            new { tenant, briefType });
        return rows.Select(r => r.ToModel()).ToList();
    }

    /// Upsert. Mỗi người CHỈ 1 dòng (PK TenantId+Username) — khớp theo 2 cột này, KHÔNG còn
    /// BriefType trong ON: đổi loại bản tin là UPDATE ngay cột BriefType trên chính dòng đó,
    /// giờ + kênh đã khai giữ nguyên. CỐ Ý không đụng LastSentUtc/LastSentLocalDate — người dùng
    /// sửa cấu hình (đổi giờ, bật kênh, đổi loại) KHÔNG được làm bản tin gửi lại lần nữa trong ngày.
    public async Task UpsertAsync(DigestSubscription s, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.DigestSubscriptions AS T
USING (SELECT @TenantId AS TenantId, @Username AS Username) AS S
    ON T.TenantId = S.TenantId AND T.Username = S.Username
WHEN MATCHED THEN UPDATE SET
    BriefType = @BriefType, Enabled = @Enabled, SendHourLocal = @SendHourLocal,
    ChannelInApp = @ChannelInApp, ChannelEmail = @ChannelEmail, Email = @Email,
    ChannelTelegram = @ChannelTelegram, TelegramChatId = @TelegramChatId,
    ChannelZalo = @ChannelZalo, ZaloPhone = @ZaloPhone, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Username, BriefType, Enabled, SendHourLocal, ChannelInApp, ChannelEmail, Email,
     ChannelTelegram, TelegramChatId, ChannelZalo, ZaloPhone, CreatedUtc, UpdatedUtc)
VALUES
    (@TenantId, @Username, @BriefType, @Enabled, @SendHourLocal, @ChannelInApp, @ChannelEmail, @Email,
     @ChannelTelegram, @TelegramChatId, @ChannelZalo, @ZaloPhone, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new
            {
                s.TenantId, s.Username, s.BriefType, s.Enabled,
                SendHourLocal = DigestSubscription.ClampHour(s.SendHourLocal),
                s.ChannelInApp, s.ChannelEmail, s.Email,
                s.ChannelTelegram, s.TelegramChatId, s.ChannelZalo, s.ZaloPhone
            });
    }

    // MarkSentAsync đã GỠ (13/08). Bản tin không còn "gửi xong thì đánh dấu lên bản đăng ký":
    // workflow chỉ chuẩn bị nội dung, việc gửi giao cho hàng đợi nên trạng thái giao nằm ở đó.
    // Chống dựng trùng trong ngày đọc thẳng Bảng tin (InsightRepository.ExistsTodayAsync).
}
