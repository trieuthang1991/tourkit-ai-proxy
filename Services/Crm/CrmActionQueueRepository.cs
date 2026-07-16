using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Crm;

/// <summary>
/// Hàng đợi hành động CRM (dbo.CrmActionQueue). Proxy CHỈ enqueue + đọc cho monitor.
/// Worker app-side (toutkit-app) drain Pending → POST TourKit.Api → cập nhật Status.
/// Thuần Dapper, KHÔNG cache. Lỗi DB → throw.
/// </summary>
public class CrmActionQueueRepository
{
    private readonly TourkitAiDb _db;
    public CrmActionQueueRepository(TourkitAiDb db) => _db = db;

    /// Enqueue 1 hành động pending (Status=0). Trả Id mới.
    public async Task<long> EnqueueAsync(CrmActionInput a, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.CrmActionQueue (TenantId, Username, Kind, PayloadJson, Status, CreatedUtc)
VALUES (@TenantId, @Username, @Kind, @PayloadJson, 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { a.TenantId, a.Username, a.Kind, a.PayloadJson });
    }

    /// Đọc cho trang theo dõi (lọc Kind/Status/Username, mới nhất trước).
    /// username != null → chỉ hành động do user đó tạo (dùng khi user thiếu quyền Cấu hình hệ thống).
    public async Task<List<CrmActionRow>> ListForMonitorAsync(
        string tenantId, string? kind, int? status, int take, CancellationToken ct = default,
        string? username = null)
    {
        if (take < 1) take = 1; if (take > 500) take = 500;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<CrmActionRow>(@"
SELECT TOP (@take)
    Id, TenantId, Username, Kind, PayloadJson, [Status], ResultJson,
    RetryCount, ErrorMessage, CreatedUtc, ProcessedUtc
FROM dbo.CrmActionQueue
WHERE TenantId = @tenantId
  AND (@kind IS NULL OR Kind = @kind)
  AND (@status IS NULL OR [Status] = @status)
  AND (@username IS NULL OR Username = @username)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take, username });
        return rows.AsList();
    }
}

/// Input enqueue (Id/Status/CreatedUtc do DB sinh).
public record CrmActionInput(string TenantId, string Username, string Kind, string PayloadJson);

/// Read-model 1 dòng (monitor).
public record CrmActionRow(
    long Id, string TenantId, string Username, string Kind, string PayloadJson,
    byte Status, string? ResultJson, int RetryCount, string? ErrorMessage,
    DateTime CreatedUtc, DateTime? ProcessedUtc);

public static class CrmActionStatus
{
    public const byte Pending = 0, Processing = 1, Done = 2, Failed = 3;
}

public static class CrmActionKind
{
    public const string AssignTask = "assign-task";
    public const string CreateAppointment = "create-appointment";
}
