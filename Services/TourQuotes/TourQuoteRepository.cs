using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.TourQuotes;

/// <summary>
/// SQL Server-backed CRUD cho dbo.TourQuotes. Per-tenant scope (composite PK TenantId,Id).
/// KHÔNG fallback file — DB lỗi → throw (Tour Quote là dữ liệu user nhập, mất là mất luôn).
///
/// Pattern: mirror ReviewRepository nhưng KHÔNG legacy migrate (table mới, không có JSON cũ).
/// Worker đồng bộ sang bảng chính sau — IsSync=0 mỗi lần save (DEFAULT + explicit reset trên UPDATE).
/// </summary>
public class TourQuoteRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TourQuoteRepository> _log;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    public TourQuoteRepository(TourkitAiDb db, ILogger<TourQuoteRepository> log)
    { _db = db; _log = log; }

    /// Upsert. Id null/blank → server sinh GUID-N. Returns id thực sự dùng (để FE biết khi tạo mới).
    public string Save(SaveTourQuoteRequest req, string tenantId, string? createdBy)
    {
        var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id!;
        var nowIso = DateTime.UtcNow.ToString("o");
        var dataJson = JsonSerializer.Serialize(req.Data, _jsonOpts);

        using var c = _db.Open();
        // MERGE upsert — TenantId + Id composite PK. IsSync=0 ở cả 2 branch (re-edit → worker sync lại).
        // CreatedAt giữ nguyên khi UPDATE (chỉ set trên INSERT); UpdatedAt luôn refresh.
        c.Execute(@"
MERGE dbo.TourQuotes AS T
USING (SELECT @TenantId AS TenantId, @Id AS Id) AS S
   ON T.TenantId = S.TenantId AND T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    Title=@Title, CustomerName=@CustomerName, CustomerPhone=@CustomerPhone,
    MarketName=@MarketName, TourType=@TourType, StartDate=@StartDate, EndDate=@EndDate,
    AdultCount=@AdultCount, ChildCount=@ChildCount,
    TotalNet=@TotalNet, TotalRevenue=@TotalRevenue, Profit=@Profit, MarginPercent=@MarginPercent,
    DataJson=@DataJson, UpdatedAt=@Now, IsSync=0
WHEN NOT MATCHED THEN INSERT
    (TenantId, Id, Title, CustomerName, CustomerPhone, MarketName, TourType,
     StartDate, EndDate, AdultCount, ChildCount,
     TotalNet, TotalRevenue, Profit, MarginPercent, DataJson,
     CreatedBy, CreatedAt, UpdatedAt, IsSync)
VALUES
    (@TenantId, @Id, @Title, @CustomerName, @CustomerPhone, @MarketName, @TourType,
     @StartDate, @EndDate, @AdultCount, @ChildCount,
     @TotalNet, @TotalRevenue, @Profit, @MarginPercent, @DataJson,
     @CreatedBy, @Now, @Now, 0);",
            new
            {
                TenantId      = tenantId,
                Id            = id,
                Title         = req.Title,
                CustomerName  = req.CustomerName,
                CustomerPhone = req.CustomerPhone,
                MarketName    = req.MarketName,
                TourType      = req.TourType,
                StartDate     = req.StartDate,
                EndDate       = req.EndDate,
                AdultCount    = req.AdultCount,
                ChildCount    = req.ChildCount,
                TotalNet      = req.TotalNet,
                TotalRevenue  = req.TotalRevenue,
                Profit        = req.Profit,
                MarginPercent = req.MarginPercent,
                DataJson      = dataJson,
                CreatedBy     = createdBy,
                Now           = nowIso
            });
        return id;
    }

    /// Lấy 1 báo giá đầy đủ (kèm DataJson hydrate thành JsonElement).
    public TourQuote? Get(string tenantId, string id)
    {
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<QuoteRow>(@"
SELECT Id, Title, CustomerName, CustomerPhone, MarketName, TourType,
       StartDate, EndDate, AdultCount, ChildCount,
       TotalNet, TotalRevenue, Profit, MarginPercent, DataJson,
       CreatedBy, CreatedAt, UpdatedAt
FROM dbo.TourQuotes WHERE TenantId = @t AND Id = @id",
            new { t = tenantId, id });
        return row == null ? null : Hydrate(row);
    }

    /// List paginated + filter `search` (substring title/customer/phone, case-insensitive nhờ collation default).
    /// Trả (items, total) — total = sau filter, để FE phân trang chính xác.
    public (List<TourQuoteListItem> Items, int Total) List(string tenantId, int pageIndex, int pageSize, string? search)
    {
        if (pageIndex < 1) pageIndex = 1;
        var offset = (pageIndex - 1) * pageSize;

        using var c = _db.Open();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var like = hasSearch ? "%" + search!.Trim() + "%" : null;

        // Total
        var total = c.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM dbo.TourQuotes
WHERE TenantId = @t
  AND (@hasSearch = 0 OR Title LIKE @q OR CustomerName LIKE @q OR CustomerPhone LIKE @q)",
            new { t = tenantId, hasSearch = hasSearch ? 1 : 0, q = like });

        // Page rows (SQL Server 2012+ OFFSET/FETCH)
        var rows = c.Query<ListRow>(@"
SELECT Id, Title, CustomerName, CustomerPhone, MarketName, StartDate, EndDate,
       AdultCount, ChildCount, TotalNet, TotalRevenue, Profit, MarginPercent,
       CreatedBy, CreatedAt, UpdatedAt
FROM dbo.TourQuotes
WHERE TenantId = @t
  AND (@hasSearch = 0 OR Title LIKE @q OR CustomerName LIKE @q OR CustomerPhone LIKE @q)
ORDER BY UpdatedAt DESC
OFFSET @off ROWS FETCH NEXT @sz ROWS ONLY",
            new { t = tenantId, hasSearch = hasSearch ? 1 : 0, q = like, off = offset, sz = pageSize });

        var items = rows.Select(r => new TourQuoteListItem(
            Id: r.Id, Title: r.Title,
            CustomerName: r.CustomerName, CustomerPhone: r.CustomerPhone,
            MarketName: r.MarketName, StartDate: r.StartDate, EndDate: r.EndDate,
            AdultCount: r.AdultCount, ChildCount: r.ChildCount,
            TotalNet: r.TotalNet, TotalRevenue: r.TotalRevenue, Profit: r.Profit,
            MarginPercent: r.MarginPercent, CreatedBy: r.CreatedBy,
            CreatedAt: r.CreatedAt.ToString("o"), UpdatedAt: r.UpdatedAt.ToString("o"))).ToList();
        return (items, total);
    }

    public bool Delete(string tenantId, string id)
    {
        using var c = _db.Open();
        var rows = c.Execute("DELETE FROM dbo.TourQuotes WHERE TenantId = @t AND Id = @id",
            new { t = tenantId, id });
        return rows > 0;
    }

    // ─── Hydration helpers ─────────────────────────────────────────────────────
    private TourQuote? Hydrate(QuoteRow r)
    {
        JsonElement data;
        try { using var doc = JsonDocument.Parse(string.IsNullOrEmpty(r.DataJson) ? "{}" : r.DataJson); data = doc.RootElement.Clone(); }
        catch (Exception ex) { _log.LogWarning(ex, "[TourQuoteRepo] DataJson parse fail id={Id}", r.Id); using var doc = JsonDocument.Parse("{}"); data = doc.RootElement.Clone(); }
        return new TourQuote(
            Id: r.Id, Title: r.Title,
            CustomerName: r.CustomerName, CustomerPhone: r.CustomerPhone,
            MarketName: r.MarketName, TourType: r.TourType,
            StartDate: r.StartDate, EndDate: r.EndDate,
            AdultCount: r.AdultCount, ChildCount: r.ChildCount,
            TotalNet: r.TotalNet, TotalRevenue: r.TotalRevenue, Profit: r.Profit,
            MarginPercent: r.MarginPercent, Data: data,
            CreatedBy: r.CreatedBy,
            CreatedAt: r.CreatedAt.ToString("o"), UpdatedAt: r.UpdatedAt.ToString("o"));
    }

    private sealed class QuoteRow
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? MarketName { get; set; }
        public string? TourType { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public int AdultCount { get; set; }
        public int ChildCount { get; set; }
        public long TotalNet { get; set; }
        public long TotalRevenue { get; set; }
        public long Profit { get; set; }
        public double? MarginPercent { get; set; }
        public string DataJson { get; set; } = "{}";
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
    private sealed class ListRow
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? MarketName { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public int AdultCount { get; set; }
        public int ChildCount { get; set; }
        public long TotalNet { get; set; }
        public long TotalRevenue { get; set; }
        public long Profit { get; set; }
        public double? MarginPercent { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
