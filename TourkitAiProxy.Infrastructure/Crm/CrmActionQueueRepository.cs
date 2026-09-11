using Dapper;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Db;

namespace TourkitAiProxy.Infrastructure.Crm;

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
INSERT INTO dbo.CrmActionQueue (TenantId, Username, Kind, PayloadJson, Status, CreatedUtc, Action, ReferId)
VALUES (@TenantId, @Username, @Kind, @PayloadJson, 0, SYSUTCDATETIME(), @Action, @ReferId);
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { a.TenantId, a.Username, a.Kind, a.PayloadJson, a.Action, a.ReferId });
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
    RetryCount, ErrorMessage, CreatedUtc, ProcessedUtc, Action, ReferId
FROM dbo.CrmActionQueue
WHERE TenantId = @tenantId
  AND (@kind IS NULL OR Kind = @kind)
  AND (@status IS NULL OR [Status] = @status)
  AND (@username IS NULL OR Username = @username)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take, username });
        return rows.AsList();
    }

    /// <summary>
    /// Mọi việc đã xếp hàng TỪ MỘT hội thoại, mới nhất trước. Dùng cho khối trạng thái trong hộp
    /// thư chat: người trực bấm nút xong phải thấy việc của mình đang ở đâu.
    ///
    /// <para>KHÔNG trả <c>PayloadJson</c> — nó mang tên và số điện thoại khách, mà khối này hiện
    /// cho mọi người có quyền vào hội thoại đọc được. Muốn xem payload thì vào trang theo dõi,
    /// nơi đã gác quyền quản trị.</para>
    /// </summary>
    public async Task<List<CrmActionRow>> ListByReferAsync(string tenantId, string referId,
        int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1; if (take > 100) take = 100;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<CrmActionRow>(@"
SELECT TOP (@take)
    Id, TenantId, Username, Kind, '' AS PayloadJson, [Status], ResultJson,
    RetryCount, ErrorMessage, CreatedUtc, ProcessedUtc, Action, ReferId
FROM dbo.CrmActionQueue
WHERE TenantId = @tenantId AND ReferId = @referId
ORDER BY Id DESC;",
            new { tenantId, referId, take });
        return rows.AsList();
    }
}

/// <summary>Input enqueue (Id/Status/CreatedUtc do DB sinh).</summary>
/// <param name="Action">Nghiệp vụ phía chat đã đẻ ra việc này, vd "chat-cham-soc". KHÁC Kind:
/// Kind nói gọi API CRM nào và worker phân việc theo nó, Action chỉ để tra cứu. Null với hành
/// động do trợ lý số liệu sinh ra — chúng không thuộc nghiệp vụ chat nào, và đó cũng là lý do
/// tham số này CÓ MẶC ĐỊNH: hai chỗ gọi cũ biên dịch và chạy nguyên trạng.</param>
/// <param name="ReferId">Mã hội thoại, để truy ngược về đoạn chat. Null khi không từ hội thoại.</param>
public record CrmActionInput(string TenantId, string Username, string Kind, string PayloadJson,
    string? Action = null, string? ReferId = null);

/// Read-model 1 dòng (monitor).
public record CrmActionRow(
    long Id, string TenantId, string Username, string Kind, string PayloadJson,
    byte Status, string? ResultJson, int RetryCount, string? ErrorMessage,
    DateTime CreatedUtc, DateTime? ProcessedUtc,
    string? Action = null, string? ReferId = null);

public static class CrmActionStatus
{
    public const byte Pending = 0, Processing = 1, Done = 2, Failed = 3;
}

public static class CrmActionKind
{
    public const string AssignTask = "assign-task";
    public const string CreateAppointment = "create-appointment";

    /// <summary>Cơ hội bán hàng (= BookingTicket). Worker app-side CHƯA có nhánh xử lý loại này —
    /// hợp đồng ở docs/crm-action-contract/README.md §4. Vì thế tính năng dùng nó đứng sau cờ
    /// Features:ChatCoHoi, mặc định TẮT.</summary>
    public const string CreateBookingTicket = "create-booking-ticket";
}

/// <summary>Nghiệp vụ phía chat sinh ra dòng hàng đợi — giá trị cột Action.</summary>
public static class CrmActionNguon
{
    public const string ChamSoc = "chat-cham-soc";
    public const string CoHoi = "chat-co-hoi";
}
