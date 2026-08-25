using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// dbo.AgentInsights — bảng tin việc cần biết. <c>Username=''</c> = tenant-wide (cả công ty thấy).
/// Mọi truy vấn đều lọc TenantId — không có đường nào đọc chéo công ty khác.
/// </summary>
public class InsightRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<InsightRepository> _log;

    public InsightRepository(TourkitAiDb db, ILogger<InsightRepository> log) { _db = db; _log = log; }

    /// <summary>
    /// Thêm 1 dòng. Có <c>AlertKey</c> mà key đó đã xuất hiện trong 24h (cùng tenant) → BỎ QUA, trả null.
    /// Chống nhắc đi nhắc lại cùng một việc mỗi lần workflow chạy (mỗi giờ một lần).
    /// </summary>
    public async Task<long?> InsertAsync(AgentInsight i, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (!string.IsNullOrEmpty(i.AlertKey))
        {
            var dup = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @TenantId AND AlertKey = @AlertKey
  AND CreatedUtc > DATEADD(HOUR, -24, SYSUTCDATETIME())",
                new { i.TenantId, i.AlertKey });
            if (dup > 0) return null;
        }
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.AgentInsights (TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc)
VALUES (@TenantId, @Username, @Kind, @Severity, @Title, @Body, @DataJson, @AlertKey, 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { i.TenantId, i.Username, i.Kind, i.Severity, i.Title, i.Body, i.DataJson, i.AlertKey });
    }

    /// <summary>
    /// Đếm xem mỗi <c>AlertKey</c> đã được ghi bao nhiêu lần (trong phạm vi bảng còn giữ).
    /// Dùng để CHẶN NHẮC MÃI: khoá chống trùng chỉ chặn trong 24h, nên một việc chưa xong sẽ sinh
    /// một dòng MỖI NGÀY cho tới khi hết hạn — nhắc 30 ngày liên tiếp thì người ta tắt tính năng
    /// chứ không phải đi làm việc đó.
    /// <para>Hỏi theo LÔ, không hỏi từng khoá: một lượt quét có thể có hàng trăm tour.</para>
    /// </summary>
    public async Task<Dictionary<string, int>> CountByAlertKeysAsync(
        string tenant, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (keys.Count == 0) return result;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(string AlertKey, int N)>(@"
SELECT AlertKey, COUNT(1) AS N FROM dbo.AgentInsights
WHERE TenantId = @tenant AND AlertKey IN @keys
GROUP BY AlertKey", new { tenant, keys });
        foreach (var r in rows) result[r.AlertKey] = r.N;
        return result;
    }

    /// <summary>
    /// DTO trung gian có setter, KHÔNG đọc thẳng vào record <see cref="AgentInsight"/>.
    /// Lý do (lỗi thật gặp 12/08, đúng cái đã cắn ở <see cref="DigestSubscriptionRepository"/>):
    /// Dapper khớp constructor theo ĐÚNG kiểu cột — <c>Severity</c> là TINYINT nên nó tìm
    /// constructor nhận <c>byte</c>, mà record khai <c>int</c> → ném "A parameterless default
    /// constructor or one matching signature ... is required".
    /// <para>Lỗi này nằm im từ lúc tạo bảng vì workflow chỉ INSERT; chỉ nổ khi có chỗ ĐỌC đầu tiên.
    /// Thêm cột TINYINT vào record dùng <c>int</c> là phải kèm DTO như đây.</para>
    /// </summary>
    private sealed class InsightRow
    {
        public long Id { get; set; }
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Severity { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string? DataJson { get; set; }
        public string? AlertKey { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedUtc { get; set; }

        public AgentInsight ToModel() => new(
            Id, TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead,
            // Dapper đọc DATETIME2 ra Kind=Unspecified → thiếu 'Z' khi trả client → frontend lệch 7h.
            DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc));
    }

    /// Feed của 1 người: dòng của chính họ, cộng dòng cấp công ty nếu được phép xem.
    /// <param name="companyWide">Được xem dòng cấp công ty (<c>Username=''</c>) không — tức có
    /// quyền <c>CH_HT_XEM</c>. Cảnh báo không có người phụ trách (vd doanh thu bất thường) là số
    /// của cả công ty, không phải việc của từng nhân viên; để mặc định <c>false</c> nên chỗ nào
    /// quên truyền thì THIẾU dòng chứ không LỘ dòng.</param>
    public async Task<List<AgentInsight>> ListAsync(string tenant, string username, string? kind,
        bool unreadOnly, int offset, int limit, CancellationToken ct = default,
        bool companyWide = false)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<InsightRow>(@"
SELECT Id, TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc
FROM dbo.AgentInsights
WHERE TenantId = @tenant AND (Username = @username OR (@companyWide = 1 AND Username = ''))
  AND (@kind IS NULL OR Kind = @kind)
  AND (@unreadOnly = 0 OR IsRead = 0)
ORDER BY CreatedUtc DESC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY",
            new { tenant, username, kind, unreadOnly = unreadOnly ? 1 : 0, offset,
                  limit = Math.Clamp(limit, 1, 100), companyWide = companyWide ? 1 : 0 });
        return rows.Select(r => r.ToModel()).ToList();
    }

    /// <summary>
    /// Hôm nay (theo ngày VN, đổi sang khoảng UTC) đã có bản tin loại này cho người này chưa —
    /// chốt chống dựng/gửi trùng của pipeline queue (thay LastSentLocalDate cũ).
    /// </summary>
    public async Task<bool> ExistsTodayAsync(string tenant, string username, string kind,
        DateTime todayVn, CancellationToken ct = default)
    {
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(todayVn.Date, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));
        await using var c = await _db.OpenAsync(ct);
        // Mọi dòng ở đây đều là bản tin THẬT: "Gửi thử" CỐ Ý không ghi vào bảng này (xem
        // DigestEndpoints) nên không có gì phải lọc ra. Nếu sau này có chỗ nào ghi bản nháp/thử vào
        // đây thì PHẢI loại nó khỏi câu đếm này — không thì người bấm thử buổi trưa sẽ mất bản tin
        // thật sáng mai, vì mốc chống trùng tưởng đã chuẩn bị xong rồi.
        return await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @tenant AND Username = @username AND Kind = @kind
  AND CreatedUtc >= @fromUtc AND CreatedUtc < DATEADD(DAY, 1, @fromUtc)",
            new { tenant, username, kind, fromUtc }) > 0;
    }

    /// <summary>
    /// Đọc 1 dòng theo Id (kẹp tenant) — dùng để đối soát bản tin đã dựng theo SourceId của dòng
    /// hàng đợi. (Worker gửi KHÔNG gọi cái này: nội dung đã nằm sẵn trong dòng hàng đợi.)
    /// </summary>
    public async Task<AgentInsight?> GetAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<InsightRow>(@"
SELECT Id, TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc
FROM dbo.AgentInsights WHERE Id = @id AND TenantId = @tenant", new { id, tenant });
        return row?.ToModel();
    }

    /// <param name="kind">Lọc theo loại (vd "payment-alert"). null = đếm mọi loại (badge chuông).</param>
    /// <param name="companyWide">Được xem dòng cấp công ty (<c>Username=''</c>) không — tức có
    /// quyền <c>CH_HT_XEM</c>. Cảnh báo không có người phụ trách (vd doanh thu bất thường) là số
    /// của cả công ty, không phải việc của từng nhân viên; để mặc định <c>false</c> nên chỗ nào
    /// quên truyền thì THIẾU dòng chứ không LỘ dòng.</param>
    public async Task<int> UnreadCountAsync(string tenant, string username, CancellationToken ct = default,
        string? kind = null, bool companyWide = false)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @tenant AND (Username = @username OR (@companyWide = 1 AND Username = ''))
  AND IsRead = 0 AND (@kind IS NULL OR Kind = @kind)",
            new { tenant, username, kind, companyWide = companyWide ? 1 : 0 });
    }

    /// <param name="companyWide">Được xem dòng cấp công ty (<c>Username=''</c>) không — tức có
    /// quyền <c>CH_HT_XEM</c>. Cảnh báo không có người phụ trách (vd doanh thu bất thường) là số
    /// của cả công ty, không phải việc của từng nhân viên; để mặc định <c>false</c> nên chỗ nào
    /// quên truyền thì THIẾU dòng chứ không LỘ dòng.</param>
    public async Task MarkReadAsync(string tenant, string username, long id,
        CancellationToken ct = default, bool companyWide = false)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.AgentInsights SET IsRead = 1
WHERE Id = @id AND TenantId = @tenant
  AND (Username = @username OR (@companyWide = 1 AND Username = ''))",
            new { id, tenant, username, companyWide = companyWide ? 1 : 0 });
    }

    /// <param name="companyWide">Được xem dòng cấp công ty (<c>Username=''</c>) không — tức có
    /// quyền <c>CH_HT_XEM</c>. Cảnh báo không có người phụ trách (vd doanh thu bất thường) là số
    /// của cả công ty, không phải việc của từng nhân viên; để mặc định <c>false</c> nên chỗ nào
    /// quên truyền thì THIẾU dòng chứ không LỘ dòng.</param>
    public async Task MarkAllReadAsync(string tenant, string username,
        CancellationToken ct = default, bool companyWide = false)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.AgentInsights SET IsRead = 1
WHERE TenantId = @tenant AND (Username = @username OR (@companyWide = 1 AND Username = ''))
  AND IsRead = 0",
            new { tenant, username, companyWide = companyWide ? 1 : 0 });
    }

    /// Xoá dòng cũ hơn keepDays. Gọi cuối mỗi lượt workflow để bảng không phình mãi.
    public async Task<int> PruneAsync(int keepDays, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM dbo.AgentInsights WHERE CreatedUtc < DATEADD(DAY, -@keepDays, SYSUTCDATETIME())",
            new { keepDays });
    }
}
