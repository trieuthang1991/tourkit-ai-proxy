using Dapper;
using TourkitAiProxy.Infrastructure.Db;

namespace TourkitAiProxy.Infrastructure.Digest;

/// <summary>
/// Ba truy vấn mà bản tin sáng của nhân viên bán hàng cần đọc từ CSDL của proxy.
///
/// <para><b>Vì sao tách ra khỏi <c>SaleBriefWorkflow</c>.</b> Ba câu SQL này trước đây viết thẳng
/// trong luồng workflow — file duy nhất còn vi phạm luật "nghiệp vụ không tự mở kết nối" sau khi
/// tách <c>Infrastructure</c>, và đã phải ghi vào danh sách nợ của
/// <c>RanhGioiTangTests</c>. Nay trả nợ: workflow quyết định <b>lấy gì, lọc thế nào</b>; lớp này
/// chỉ <b>đọc</b>.</para>
///
/// <para>Giữ nguyên câu SQL và kiểu trả về từng dòng — đây là đợt DI CHUYỂN, không phải đợt sửa
/// truy vấn. Trộn hai việc thì lúc bản tin sai số không biết do đâu.</para>
/// </summary>
public class SaleBriefRepository
{
    private readonly TourkitAiDb _db;
    public SaleBriefRepository(TourkitAiDb db) { _db = db; }

    /// Khách đã được AI chấm hạng, kèm mốc chấm (epoch ms) để workflow tự tính số ngày.
    public async Task<IReadOnlyList<(string CustomerId, string Rank, long GeneratedAt)>>
        HangKhachAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(string CustomerId, string Rank, long GeneratedAt)>(@"
SELECT CustomerId, [Rank], GeneratedAt FROM dbo.Reviews
WHERE TenantId = @tenantId AND [Rank] IN ('A','B')", new { tenantId });
        return rows.ToList();
    }

    /// Báo giá của chính người này, lâu chưa cập nhật hơn <paramref name="soNgay"/>.
    public async Task<IReadOnlyList<(string? Title, string? CustomerName, DateTime UpdatedAt)>>
        BaoGiaCuAsync(string tenantId, string user, int soNgay, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(string? Title, string? CustomerName, DateTime UpdatedAt)>(@"
SELECT Title, CustomerName, UpdatedAt FROM dbo.TourQuotes
WHERE TenantId = @tenantId AND CreatedBy = @user
  AND UpdatedAt < DATEADD(DAY, -@days, SYSUTCDATETIME())
ORDER BY UpdatedAt ASC", new { tenantId, user, days = soNgay });
        return rows.ToList();
    }

    /// Mã cơ hội → % khả năng chốt từ điểm AI đã lưu. Chưa chấm thì không có mặt.
    public async Task<IReadOnlyList<(string DealId, int? WinRate)>>
        DiemDealAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(string DealId, int? WinRate)>(
            "SELECT DealId, WinRate FROM dbo.DealScores WHERE TenantId = @tenantId", new { tenantId });
        return rows.ToList();
    }
}
