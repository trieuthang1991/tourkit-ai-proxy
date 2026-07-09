using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Hàng đợi mail OUTBOUND dùng chung (dbo.OutboundMails). Producer (vd DealAutoReviewWorkflow)
/// enqueue dòng Status=0 (pending); một worker riêng (CEO viết) poll → render template → gửi → cập nhật.
///
/// Nội dung gửi theo TEMPLATE: producer chỉ truyền TemplateCode + Params(JSON), KHÔNG soạn HTML.
/// Repo thuần Dapper, KHÔNG cache. Lỗi DB → throw (caller xử lý).
/// </summary>
public class MailQueueRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailQueueRepository> _log;

    public MailQueueRepository(TourkitAiDb db, ILogger<MailQueueRepository> log)
    {
        _db = db; _log = log;
    }

    /// Enqueue 1 mail pending (Status=0). Trả Id mới.
    public async Task<long> EnqueueAsync(OutboundMailInput m, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.OutboundMails
    (TenantId, Kind, SourceId, Username, TemplateCode, ToEmail, ToName, ToUserId, Cc, Subject, [Params], Data, ScheduledUtc, Status, CreatedUtc)
VALUES
    (@TenantId, @Kind, @SourceId, @Username, @TemplateCode, @ToEmail, @ToName, @ToUserId, @Cc, @Subject, @ParamsJson, @Data, @ScheduledUtc, 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new
            {
                m.TenantId, m.Kind, m.SourceId, m.Username, m.TemplateCode,
                m.ToEmail, m.ToName, m.ToUserId, m.Cc, m.Subject,
                ParamsJson = m.Params, m.Data, m.ScheduledUtc
            });
        return id;
    }

    /// Đếm số mail đã ENQUEUE của 1 nghiệp vụ trong N giờ gần nhất + thời điểm mới nhất.
    /// Dùng cho dedup/throttle (maxNotifications + giãn cách).
    ///
    /// Tính MỌI status TRỪ Cancelled(3): mỗi dòng đều là 1 lần alert đã sinh ra, nên phải tính cả
    /// Failed(2)/Skipped(4) — nếu chỉ đếm Pending(0)+Sent(1) thì khi mailer chết (mọi mail rơi vào Failed)
    /// throttle luôn thấy 0 → re-enqueue mỗi tick → hàng đợi phình vô hạn (sự cố 33k mail 2026-07-09).
    /// Cancelled(3) KHÔNG tính (admin chủ động hủy → không tiêu quota alert).
    public async Task<(int Total, DateTime? LastUtc)> CountRecentBySourceAsync(
        string tenantId, string kind, string sourceId, int withinHours, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<(int Total, DateTime? LastUtc)>(@"
SELECT COUNT(*) AS Total, MAX(CreatedUtc) AS LastUtc
FROM dbo.OutboundMails
WHERE TenantId = @tenantId AND Kind = @kind AND SourceId = @sourceId
  AND [Status] <> 3
  AND CreatedUtc >= DATEADD(HOUR, -@withinHours, SYSUTCDATETIME());",
            new { tenantId, kind, sourceId, withinHours });
        var last = row.LastUtc.HasValue ? DateTime.SpecifyKind(row.LastUtc.Value, DateTimeKind.Utc) : (DateTime?)null;
        return (row.Total, last);
    }

    /// Đọc cho trang theo dõi (lọc Kind/Status, mới nhất trước).
    public async Task<List<OutboundMail>> ListForMonitorAsync(
        string tenantId, string? kind, int? status, int take, CancellationToken ct = default)
    {
        if (take < 1) take = 1; if (take > 500) take = 500;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<OutboundMail>(@"
SELECT TOP (@take)
    Id, TenantId, Kind, SourceId, Username, TemplateCode, ToEmail, ToName, ToUserId, Cc,
    Subject, [Params] AS [Params], Data, [Status], RetryCount, ErrorMessage,
    ScheduledUtc, CreatedUtc, ProcessedUtc
FROM dbo.OutboundMails
WHERE TenantId = @tenantId
  AND (@kind IS NULL OR Kind = @kind)
  AND (@status IS NULL OR [Status] = @status)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take });
        return rows.AsList();
    }

    /// CROSS-TENANT (admin): đọc hàng đợi mọi tenant, lọc tùy chọn theo tenant/kind/status.
    public async Task<List<OutboundMail>> ListForAdminAsync(
        string? tenantId, string? kind, int? status, int take, CancellationToken ct = default)
    {
        if (take < 1) take = 1; if (take > 500) take = 500;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<OutboundMail>(@"
SELECT TOP (@take)
    Id, TenantId, Kind, SourceId, Username, TemplateCode, ToEmail, ToName, ToUserId, Cc,
    Subject, [Params] AS [Params], Data, [Status], RetryCount, ErrorMessage,
    ScheduledUtc, CreatedUtc, ProcessedUtc
FROM dbo.OutboundMails
WHERE (@tenantId IS NULL OR TenantId = @tenantId)
  AND (@kind     IS NULL OR Kind     = @kind)
  AND (@status   IS NULL OR [Status] = @status)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take });
        return rows.AsList();
    }

    /// CROSS-TENANT (admin): đếm theo Status (0..4) cho dải filter, áp cùng filter tenant/kind.
    public async Task<Dictionary<int, int>> CountByStatusForAdminAsync(
        string? tenantId, string? kind, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(int Status, int Cnt)>(@"
SELECT [Status] AS [Status], COUNT(*) AS Cnt
FROM dbo.OutboundMails
WHERE (@tenantId IS NULL OR TenantId = @tenantId)
  AND (@kind     IS NULL OR Kind     = @kind)
GROUP BY [Status];",
            new { tenantId, kind });
        return rows.ToDictionary(r => r.Status, r => r.Cnt);
    }
}

/// Input enqueue 1 mail (Id/Status/CreatedUtc do DB sinh). `Params` = JSON tham số replace vào template.
public record OutboundMailInput(
    string TenantId,
    string Kind,
    string? SourceId = null,
    string? Username = null,
    string? TemplateCode = null,
    string? ToEmail = null,
    string? ToName = null,
    int? ToUserId = null,
    string? Cc = null,
    string? Subject = null,
    string? Params = null,
    string? Data = null,
    DateTime? ScheduledUtc = null);

/// Read-model 1 dòng hàng đợi (cho monitor).
public record OutboundMail(
    long Id,
    string TenantId,
    string Kind,
    string? SourceId,
    string? Username,
    string? TemplateCode,
    string? ToEmail,
    string? ToName,
    int? ToUserId,
    string? Cc,
    string? Subject,
    string? Params,
    string? Data,
    byte Status,
    int RetryCount,
    string? ErrorMessage,
    DateTime? ScheduledUtc,
    DateTime CreatedUtc,
    DateTime? ProcessedUtc);

/// Trạng thái mail (int → CEO dùng enum trong worker).
public static class OutboundMailStatus
{
    public const byte Pending = 0, Sent = 1, Failed = 2, Cancelled = 3, Skipped = 4;
}
