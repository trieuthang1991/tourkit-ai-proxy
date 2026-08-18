using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Digest;

/// Đã nhắc về một đối tượng bao nhiêu lần, lần cuối khi nào, lúc đó trạng thái ra sao.
public record NotifyMark(int Times, DateTime FirstUtc, DateTime LastUtc, string? StateStamp);

/// <summary>
/// Quyết định "có nên nhắc lại không" — HÀM THUẦN, tách khỏi DB để test được mọi ca biên.
///
/// <para>Tách ra vì đây đúng loại logic sai trong im lặng: nhắc thừa thì người ta tắt tính năng,
/// nhắc thiếu thì việc rơi mất — cả hai đều không có lỗi nào hiện ra để ai đó phát hiện.</para>
/// </summary>
public static class NotifyThrottle
{
    /// <param name="mark">Lần nhắc trước, null = chưa nhắc bao giờ.</param>
    /// <param name="stateNow">Dấu vết trạng thái HIỆN TẠI của đối tượng (vd ngày chăm sóc gần
    /// nhất). Khác với lúc nhắc trước = đã có người xử lý thật → coi như chưa nhắc, đếm lại từ đầu.</param>
    /// <param name="minGapDays">Nhắc rồi thì im bấy nhiêu ngày. 0 = không giới hạn khoảng cách.</param>
    /// <param name="maxTimes">Nhắc tối đa bấy nhiêu lần rồi thôi. 0 = không giới hạn số lần.</param>
    /// <returns>Skip=true kèm <c>Reason</c> để tóm tắt lần chạy nói được VÌ SAO bỏ, thay vì chỉ
    /// đưa ra một con số.</returns>
    public static (bool Skip, string? Reason) Decide(
        NotifyMark? mark, string? stateNow, DateTime nowUtc, int minGapDays, int maxTimes)
    {
        if (mark == null) return (false, null);

        // Trạng thái đổi = đã có người động vào thật → vòng đời mới, quên hết lần nhắc cũ.
        // Kiểm TRƯỚC hai luật kia: khách vừa được chăm mà vẫn tới hạn lần nữa thì phải nhắc được,
        // không thì "đã đủ 3 lần" trở thành án chung thân.
        if (!string.Equals(mark.StateStamp ?? "", stateNow ?? "", StringComparison.Ordinal))
            return (false, null);

        if (maxTimes > 0 && mark.Times >= maxTimes)
            return (true, $"đã nhắc đủ {maxTimes} lần");

        if (minGapDays > 0 && (nowUtc - mark.LastUtc).TotalDays < minGapDays)
            return (true, $"vừa nhắc {(int)(nowUtc - mark.LastUtc).TotalDays} ngày trước");

        return (false, null);
    }
}

/// <summary>
/// Sổ ghi nhắc dùng chung — <c>dbo.NotifyLedger</c>. Xem chú thích dài ở TourkitAiDb.SchemaSql cho
/// lý do tồn tại; tóm tắt: đếm theo ĐỐI TƯỢNG chứ không đếm theo thông báo, để dùng được cả khi
/// một thông báo gộp nhiều đối tượng.
///
/// <para><b>Tác vụ mới cần chặn nhắc lặp thì dùng lớp này, ĐỪNG dựng bảng riêng.</b> Hai chỗ cũ
/// (canh thanh toán đếm qua <c>AgentInsights.AlertKey</c>, deal nguội đếm qua
/// <c>OutboundMails.SourceId</c>) là trường hợp đặc biệt hợp lệ — ở đó mỗi thông báo đúng bằng một
/// đối tượng nên đếm thông báo là đủ. Chúng đang chạy thật và đã kiểm chứng nên cố ý KHÔNG viết
/// lại; nhưng cái mới thì về đây, để đừng có cách thứ tư.</para>
///
/// <para>Mọi thao tác đều theo LÔ: một lượt quét có thể có hàng trăm đối tượng, hỏi từng cái là
/// hàng trăm vòng đi về DB.</para>
/// </summary>
public class NotifyLedgerRepository
{
    private readonly TourkitAiDb _db;
    public NotifyLedgerRepository(TourkitAiDb db) { _db = db; }

    /// Khoá đối tượng, dạng 'loai:id'. Dùng hàm này thay vì tự ghép chuỗi để hai chỗ không đặt
    /// khác nhau ("customer:1" với "Customer_1") rồi đếm nhầm thành hai đối tượng.
    public static string Subject(string kind, object id) => $"{kind}:{id}";

    /// <summary>
    /// Kích thước lô cho mệnh đề <c>IN</c>. Dapper bung mỗi phần tử thành MỘT tham số, mà SQL Server
    /// chỉ nhận tối đa <b>2100 tham số</b> một lệnh — quá ngưỡng là ném lỗi, không phải chạy chậm.
    /// Hôm nay mỗi lượt quét chỉ vài chục đối tượng nên không ai chạm tới; nhưng ngưỡng này là loại
    /// chỉ nổ khi công ty có nhiều dữ liệu, tức là đúng lúc không được phép hỏng. Để 1000 cho thoáng.
    /// </summary>
    private const int InChunk = 1000;

    public async Task<Dictionary<string, NotifyMark>> GetAsync(
        string tenantId, string scope, IReadOnlyCollection<string> subjects, CancellationToken ct = default)
    {
        var result = new Dictionary<string, NotifyMark>(StringComparer.Ordinal);
        if (subjects.Count == 0) return result;

        await using var c = await _db.OpenAsync(ct);
        foreach (var chunk in subjects.Chunk(InChunk))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await c.QueryAsync<(string SubjectKey, int Times, DateTime FirstUtc, DateTime LastUtc, string? StateStamp)>(@"
SELECT SubjectKey, Times, FirstUtc, LastUtc, StateStamp
FROM dbo.NotifyLedger
WHERE TenantId = @tenantId AND Scope = @scope AND SubjectKey IN @subjects",
                new { tenantId, scope, subjects = chunk });

            foreach (var r in rows)
                // SpecifyKind: Dapper đọc DATETIME2 ra Kind=Unspecified, để nguyên thì phép trừ với
                // DateTime.UtcNow lệch đúng bằng múi giờ máy chủ (xem docs/datetime-convention.md).
                result[r.SubjectKey] = new NotifyMark(r.Times,
                    DateTime.SpecifyKind(r.FirstUtc, DateTimeKind.Utc),
                    DateTime.SpecifyKind(r.LastUtc, DateTimeKind.Utc),
                    r.StateStamp);
        }
        return result;
    }

    /// <summary>
    /// Ghi nhận vừa nhắc. <c>StateStamp</c> khác lần trước → đếm LẠI TỪ 1 (đối tượng đã sang vòng
    /// đời mới), giống lần nhắc đầu tiên.
    /// </summary>
    public async Task MarkAsync(string tenantId, string scope,
        IReadOnlyCollection<(string Subject, string? StateStamp)> marks, CancellationToken ct = default)
    {
        if (marks.Count == 0) return;
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.NotifyLedger AS t
USING (SELECT @tenantId AS TenantId, @scope AS Scope, @subject AS SubjectKey, @stamp AS StateStamp) AS s
    ON t.TenantId = s.TenantId AND t.Scope = s.Scope AND t.SubjectKey = s.SubjectKey
WHEN MATCHED THEN UPDATE SET
    -- ISNULL để so được cả khi một bên null: NULL <> NULL trong SQL là UNKNOWN, không phải false,
    -- nên thiếu ISNULL thì đối tượng không có dấu vết trạng thái sẽ reset bộ đếm MỖI LẦN nhắc.
    Times      = CASE WHEN ISNULL(t.StateStamp, N'') = ISNULL(s.StateStamp, N'') THEN t.Times + 1 ELSE 1 END,
    FirstUtc   = CASE WHEN ISNULL(t.StateStamp, N'') = ISNULL(s.StateStamp, N'') THEN t.FirstUtc ELSE SYSUTCDATETIME() END,
    LastUtc    = SYSUTCDATETIME(),
    StateStamp = s.StateStamp
WHEN NOT MATCHED THEN
    INSERT (TenantId, Scope, SubjectKey, Times, FirstUtc, LastUtc, StateStamp)
    VALUES (s.TenantId, s.Scope, s.SubjectKey, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), s.StateStamp);",
            marks.Select(m => new { tenantId, scope, subject = m.Subject, stamp = m.StateStamp }));
    }

    /// Dọn dòng đã lâu không đụng tới. Giữ mặc định 180 ngày — dài hơn mọi chính sách nhắc hiện có
    /// (tính bằng ngày/tuần), nhưng không giữ mãi để bảng khỏi phình theo tuổi hệ thống.
    public async Task<int> PruneAsync(int keepDays = 180, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM dbo.NotifyLedger WHERE LastUtc < DATEADD(DAY, -@keepDays, SYSUTCDATETIME())",
            new { keepDays });
    }
}
