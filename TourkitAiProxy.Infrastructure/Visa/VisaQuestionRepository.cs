using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Visa;

/// Per-tenant config câu hỏi wizard /visa. NULL = dùng default embedded ở frontend.
/// CRUD đơn giản (Get/Save/Delete) — payload là JSON string đã validate ở endpoint.
public class VisaQuestionRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<VisaQuestionRepository> _log;

    public VisaQuestionRepository(TourkitAiDb db, ILogger<VisaQuestionRepository> log)
    { _db = db; _log = log; }

    public record VisaQuestionSet(string TenantId, string QuestionsJson, string? UpdatedBy, DateTime UpdatedAt);

    /// Đọc cấu hình tenant. Null = chưa override → frontend dùng default.
    public VisaQuestionSet? Get(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        try
        {
            using var c = _db.Open();
            return c.QueryFirstOrDefault<VisaQuestionSet>(@"
                SELECT TenantId, QuestionsJson, UpdatedBy, UpdatedAt
                FROM dbo.VisaQuestionSets WHERE TenantId = @t",
                new { t = tenantId });
        }
        catch (Exception ex) { _log.LogWarning(ex, "VisaQuestionSets.Get fail"); return null; }
    }

    /// Upsert cấu hình. JSON đã được endpoint validate (parse OK).
    public bool Save(string tenantId, string questionsJson, string? updatedBy)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("tenantId rỗng");
        try
        {
            using var c = _db.Open();
            c.Execute(@"
MERGE dbo.VisaQuestionSets AS T
USING (SELECT @t AS TenantId) AS S
   ON T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET QuestionsJson=@q, UpdatedBy=@u, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, QuestionsJson, UpdatedBy, UpdatedAt)
VALUES (@t, @q, @u, SYSUTCDATETIME());",
                new { t = tenantId, q = questionsJson, u = updatedBy });
            return true;
        }
        catch (Exception ex) { _log.LogError(ex, "VisaQuestionSets.Save fail"); return false; }
    }

    /// Reset về default: xoá row tenant → frontend dùng default embedded.
    public bool Delete(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        try
        {
            using var c = _db.Open();
            return c.Execute("DELETE FROM dbo.VisaQuestionSets WHERE TenantId = @t",
                new { t = tenantId }) > 0;
        }
        catch (Exception ex) { _log.LogWarning(ex, "VisaQuestionSets.Delete fail"); return false; }
    }
}
