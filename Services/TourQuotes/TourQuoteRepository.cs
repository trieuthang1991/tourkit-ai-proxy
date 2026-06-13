using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using StackExchange.Redis;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.TourQuotes;

/// <summary>
/// 2-tier storage cho tour quote:
///   • DRAFT layer  = Redis hash `tkai:tq-draft:{tenant}:{id}` TTL 24h — autosave từng keystroke
///     (debounce client) KHÔNG đụng DB. Mất Redis = mất draft → user mất chỉ vài giây edit.
///   • COMMIT layer = SQL `dbo.TourQuotes` (per-tenant composite PK) — explicit Save từ user
///     (nút "Lưu báo giá") hoặc auto-commit mỗi N phút từ background task.
///
/// Read: SQL primary + check Redis draft mới hơn → merge với flag `isDraft=true` cho FE biết.
/// Commit: flush Redis → SQL via existing Save (IsSync=0 để worker đồng bộ sang bảng chính).
/// </summary>
public class TourQuoteRepository
{
    private readonly TourkitAiDb _db;
    private readonly RedisProvider _redis;
    private readonly ILogger<TourQuoteRepository> _log;

    // TTL nháp: 24h — đủ cho user về nhà, hôm sau quay lại edit tiếp; sau đó Redis tự dọn.
    private static readonly TimeSpan DRAFT_TTL = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    public TourQuoteRepository(TourkitAiDb db, RedisProvider redis, ILogger<TourQuoteRepository> log)
    { _db = db; _redis = redis; _log = log; }

    private static string DraftKey(string tenantId, string id) => $"tkai:tq-draft:{tenantId}:{id}";

    // ─── DRAFT layer (Redis only — không đụng DB) ──────────────────────────────

    /// Lưu nháp vào Redis. Id null/blank → server sinh GUID-N. KHÔNG ghi DB. TTL 24h.
    /// Fallback: nếu Redis không available → ghi luôn SQL (degraded mode, không có draft).
    public string SaveDraft(SaveTourQuoteRequest req, string tenantId, string? createdBy)
    {
        var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id!;
        if (_redis.Db == null)
        {
            // Redis xuống → degraded: commit thẳng SQL (vẫn an toàn nhưng mất ưu điểm batching).
            _log.LogWarning("[TourQuote] Redis unavailable → SaveDraft fallback commit SQL ngay (id={Id})", id);
            return Save(req, tenantId, createdBy);
        }
        var nowIso = DateTime.UtcNow.ToString("o");
        var draft = new DraftEnvelope(req, createdBy, nowIso);
        var json = JsonSerializer.Serialize(draft, _jsonOpts);
        try
        {
            _redis.Db.StringSet(DraftKey(tenantId, id), json, DRAFT_TTL);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TourQuote] Redis SET draft fail id={Id} → fallback SQL", id);
            return Save(req, tenantId, createdBy);
        }
        return id;
    }

    /// Đọc draft trực tiếp từ Redis (không merge với SQL). Trả null nếu không có hoặc Redis xuống.
    private DraftEnvelope? ReadDraft(string tenantId, string id)
    {
        if (_redis.Db == null) return null;
        try
        {
            var v = _redis.Db.StringGet(DraftKey(tenantId, id));
            if (v.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<DraftEnvelope>((string)v!, _jsonOpts);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[TourQuote] Redis GET draft fail id={Id}", id); return null; }
    }

    /// Xóa draft khỏi Redis (gọi sau khi Commit thành công).
    private void DeleteDraft(string tenantId, string id)
    {
        if (_redis.Db == null) return;
        try { _redis.Db.KeyDelete(DraftKey(tenantId, id)); }
        catch (Exception ex) { _log.LogWarning(ex, "[TourQuote] Redis DEL draft fail id={Id}", id); }
    }

    /// Commit draft từ Redis → SQL. Returns id và TourQuote đã save.
    /// Nếu không có draft trong Redis → throw (caller phải Save thẳng qua SQL).
    public TourQuote? Commit(string tenantId, string id, string? createdBy)
    {
        var draft = ReadDraft(tenantId, id);
        if (draft == null)
        {
            // Không có draft Redis → có thể user reload xong commit ngay (draft expired).
            // Đọc SQL hiện tại → trả về (không phá data).
            return Get(tenantId, id);
        }
        // Đảm bảo req có id (server đã sinh nếu lúc SaveDraft chưa có)
        var req = draft.Request with { Id = id };
        Save(req, tenantId, createdBy ?? draft.CreatedBy);
        DeleteDraft(tenantId, id);
        return Get(tenantId, id);
    }

    private record DraftEnvelope(
        [property: JsonPropertyName("request")]   SaveTourQuoteRequest Request,
        [property: JsonPropertyName("createdBy")] string? CreatedBy,
        [property: JsonPropertyName("savedAt")]   string SavedAt
    );

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
        var sqlVer = row == null ? null : Hydrate(row);

        // Check Redis draft — nếu có và mới hơn SQL row (hoặc SQL chưa có) → trả draft với isDraft=true
        var draft = ReadDraft(tenantId, id);
        if (draft != null)
        {
            var draftTime = DateTime.TryParse(draft.SavedAt, out var d) ? d : DateTime.MinValue;
            var sqlTime = sqlVer != null && DateTime.TryParse(sqlVer.UpdatedAt, out var s) ? s : DateTime.MinValue;
            if (draft.Request != null && (sqlVer == null || draftTime > sqlTime))
            {
                // Draft mới hơn → build TourQuote từ draft data (giữ field index từ draft, isDraft=true).
                var req = draft.Request;
                JsonElement data;
                try { data = req.Data; }
                catch { using var doc2 = JsonDocument.Parse("{}"); data = doc2.RootElement.Clone(); }
                return sqlVer == null
                    ? new TourQuote(
                        Id: id, Title: req.Title,
                        CustomerName: req.CustomerName, CustomerPhone: req.CustomerPhone,
                        MarketName: req.MarketName, TourType: req.TourType,
                        StartDate: req.StartDate, EndDate: req.EndDate,
                        AdultCount: req.AdultCount, ChildCount: req.ChildCount,
                        TotalNet: req.TotalNet, TotalRevenue: req.TotalRevenue, Profit: req.Profit,
                        MarginPercent: req.MarginPercent, Data: data,
                        CreatedBy: draft.CreatedBy,
                        CreatedAt: draft.SavedAt, UpdatedAt: draft.SavedAt)
                    : sqlVer with {
                        Title = req.Title, CustomerName = req.CustomerName, CustomerPhone = req.CustomerPhone,
                        MarketName = req.MarketName, TourType = req.TourType,
                        StartDate = req.StartDate, EndDate = req.EndDate,
                        AdultCount = req.AdultCount, ChildCount = req.ChildCount,
                        TotalNet = req.TotalNet, TotalRevenue = req.TotalRevenue, Profit = req.Profit,
                        MarginPercent = req.MarginPercent, Data = data,
                        UpdatedAt = draft.SavedAt,
                    };
            }
        }
        return sqlVer;
    }

    /// FE check trạng thái draft (không phá flow Get cũ). Trả timestamp draft nếu có.
    public string? GetDraftSavedAt(string tenantId, string id)
        => ReadDraft(tenantId, id)?.SavedAt;

    /// Public read — KHÔNG scope tenant. Dùng cho link share /q/{id} mà khách không có session.
    /// Id là Guid 32-hex (Guid.NewGuid().ToString("N")) → đủ khó guess; không có ShareToken riêng.
    /// CHỈ trả về row SQL đã commit — bỏ qua draft Redis (khách không xem nháp).
    public TourQuote? GetPublic(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<QuoteRow>(@"
SELECT TOP 1 Id, Title, CustomerName, CustomerPhone, MarketName, TourType,
       StartDate, EndDate, AdultCount, ChildCount,
       TotalNet, TotalRevenue, Profit, MarginPercent, DataJson,
       CreatedBy, CreatedAt, UpdatedAt
FROM dbo.TourQuotes WHERE Id = @id",
            new { id });
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
