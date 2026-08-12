using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Digest;

/// dbo.DigestSubscriptions — sổ người nhận bản tin: ai nhận loại nào, mấy giờ, qua kênh nào.
public class DigestSubscriptionRepository
{
    private readonly TourkitAiDb _db;

    public DigestSubscriptionRepository(TourkitAiDb db) { _db = db; }

    private const string Cols = @"TenantId, Username, BriefType, Enabled, SendHourLocal,
ChannelInApp, ChannelEmail, Email, ChannelTelegram, TelegramChatId, ChannelZalo, ZaloUserId,
LastSentUtc, LastSentLocalDate";

    public async Task<List<DigestSubscription>> ListForUserAsync(string tenant, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<DigestSubscription>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND Username = @username",
            new { tenant, username });
        return rows.ToList();
    }

    /// Danh sách người nhận đang bật của 1 loại bản tin trong 1 công ty. Workflow duyệt cái này
    /// rồi lọc tiếp bằng DigestDue.IsDue để biết ai "đến giờ".
    public async Task<List<DigestSubscription>> ListEnabledAsync(string tenant, string briefType, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<DigestSubscription>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND BriefType = @briefType AND Enabled = 1",
            new { tenant, briefType });
        return rows.ToList();
    }

    /// Upsert. CỐ Ý không đụng LastSentUtc/LastSentLocalDate — người dùng sửa cấu hình
    /// (đổi giờ, bật kênh) KHÔNG được làm bản tin gửi lại lần nữa trong ngày.
    public async Task UpsertAsync(DigestSubscription s, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.DigestSubscriptions AS T
USING (SELECT @TenantId AS TenantId, @Username AS Username, @BriefType AS BriefType) AS S
    ON T.TenantId = S.TenantId AND T.Username = S.Username AND T.BriefType = S.BriefType
WHEN MATCHED THEN UPDATE SET
    Enabled = @Enabled, SendHourLocal = @SendHourLocal,
    ChannelInApp = @ChannelInApp, ChannelEmail = @ChannelEmail, Email = @Email,
    ChannelTelegram = @ChannelTelegram, TelegramChatId = @TelegramChatId,
    ChannelZalo = @ChannelZalo, ZaloUserId = @ZaloUserId, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Username, BriefType, Enabled, SendHourLocal, ChannelInApp, ChannelEmail, Email,
     ChannelTelegram, TelegramChatId, ChannelZalo, ZaloUserId, CreatedUtc, UpdatedUtc)
VALUES
    (@TenantId, @Username, @BriefType, @Enabled, @SendHourLocal, @ChannelInApp, @ChannelEmail, @Email,
     @ChannelTelegram, @TelegramChatId, @ChannelZalo, @ZaloUserId, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new
            {
                s.TenantId, s.Username, s.BriefType, s.Enabled,
                SendHourLocal = DigestSubscription.ClampHour(s.SendHourLocal),
                s.ChannelInApp, s.ChannelEmail, s.Email,
                s.ChannelTelegram, s.TelegramChatId, s.ChannelZalo, s.ZaloUserId
            });
    }

    /// Đánh dấu đã gửi. localDate là NGÀY VIỆT NAM (không phải UTC) — xem DigestDue.
    public async Task MarkSentAsync(string tenant, string username, string briefType,
        DateTime utcNow, DateTime localDate, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.DigestSubscriptions SET LastSentUtc = @utcNow, LastSentLocalDate = @localDate, UpdatedUtc = SYSUTCDATETIME()
WHERE TenantId = @tenant AND Username = @username AND BriefType = @briefType",
            new { tenant, username, briefType, utcNow, localDate = localDate.Date });
    }
}
