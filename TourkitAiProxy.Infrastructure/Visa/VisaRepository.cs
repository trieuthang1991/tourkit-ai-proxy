using System.Text.Json;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// SQL Server-backed store: (TenantId, assessmentId) → VisaAssessment. Persist dbo.VisaAssessments.
/// Mọi method nhận tenantId — query scoped theo tenant. Cross-tenant access trả null/false.
/// </summary>
public class VisaRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<VisaRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VisaRepository(TourkitAiDb db, ILogger<VisaRepository> log)
    {
        _db = db; _log = log;
    }

    public VisaAssessment? Get(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(id)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<VisaRow>(
            @"SELECT * FROM dbo.VisaAssessments WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id });
        return row == null ? null : Hydrate(row);
    }

    public List<VisaAssessment> All(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return new();
        using var c = _db.Open();
        var rows = c.Query<VisaRow>(
            @"SELECT * FROM dbo.VisaAssessments WHERE TenantId=@t ORDER BY CreatedAt DESC",
            new { t = tenantId }).ToList();
        return rows.Select(Hydrate).Where(a => a != null).ToList()!;
    }

    public void Save(string tenantId, VisaAssessment a)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));
        using var c = _db.Open();
        var extJson = JsonSerializer.Serialize(a.Extraction, _jsonOpts);
        var resJson = a.Result == null ? null : JsonSerializer.Serialize(a.Result, _jsonOpts);
        c.Execute(@"
MERGE dbo.VisaAssessments AS T
USING (SELECT @t AS TenantId, @id AS Id) AS S
   ON T.TenantId = S.TenantId AND T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    ApplicantName=@an, Country=@co, Status=@st,
    ExtractionJson=@ext, ResultJson=@res,
    FileCount=@fc, FilesPurged=@fp, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Id, ApplicantName, Country, Status, ExtractionJson, ResultJson,
     FileCount, FilesPurged, CreatedAt, UpdatedAt)
VALUES
    (@t, @id, @an, @co, @st, @ext, @res, @fc, @fp, @cre, SYSUTCDATETIME());",
            new
            {
                t = tenantId, id = a.Id,
                an = a.ApplicantName, co = a.Country, st = a.Status,
                ext = extJson, res = resJson,
                fc = a.FileCount, fp = a.FilesPurged,
                cre = DateTime.TryParse(a.CreatedAt, out var dt) ? dt : DateTime.UtcNow
            });
    }

    public bool Delete(string tenantId, string id)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        using var c = _db.Open();
        return c.Execute(
            "DELETE FROM dbo.VisaAssessments WHERE TenantId=@t AND Id=@id",
            new { t = tenantId, id }) > 0;
    }

    // ─── Hydration ────────────────────────────────────────────────────────
    private VisaAssessment? Hydrate(VisaRow row)
    {
        try
        {
            var ext = JsonSerializer.Deserialize<VisaExtraction>(row.ExtractionJson, _jsonOpts)
                ?? throw new InvalidOperationException("Extraction null");
            VisaResult? res = string.IsNullOrEmpty(row.ResultJson)
                ? null
                : JsonSerializer.Deserialize<VisaResult>(row.ResultJson, _jsonOpts);
            return new VisaAssessment(
                Id: row.Id,
                ApplicantName: row.ApplicantName ?? "",
                Country: row.Country,
                Status: row.Status,
                Extraction: ext,
                Result: res,
                FileCount: row.FileCount,
                FilesPurged: row.FilesPurged,
                CreatedAt: row.CreatedAt.ToString("o"),
                UpdatedAt: row.UpdatedAt.ToString("o"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[VisaRepo] Hydrate row {Id} fail", row.Id);
            return null;
        }
    }

    /// <summary>Dapper row mapper cho dbo.VisaAssessments — bind theo NAME, không phải ordinal.</summary>
    private sealed class VisaRow
    {
        public string TenantId { get; set; } = "";
        public string Id { get; set; } = "";
        public string? ApplicantName { get; set; }
        public string? Country { get; set; }
        public string Status { get; set; } = "extracted";
        public string ExtractionJson { get; set; } = "{}";
        public string? ResultJson { get; set; }
        public int FileCount { get; set; }
        public bool FilesPurged { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
