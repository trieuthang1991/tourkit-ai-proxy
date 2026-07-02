using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Reviews;

/// <summary>
/// SQL Server-backed store: customerId → CustomerReview.
/// Persist trong bảng dbo.Reviews của DB PushDb (dùng chung instance với TourKit/PushNotification).
/// Full review object được serialize vào cột DataJson; các field cần truy vấn (Rank, AlertLevel, Fingerprint,
/// AiProvider, AiModel, TokensIn/Out, GeneratedAt) được duplicate ra cột để index.
///
/// FALLBACK: nếu DB không sẵn sàng (connection fail / table missing), tự động dùng file repo cũ
/// (data/reviews.json) — không gãy production khi triển khai dần.
///
/// MIGRATION: lần đầu khởi động nếu DB rỗng + file JSON cũ tồn tại → import 1 lần, log số bản ghi.
/// </summary>
public class ReviewRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<ReviewRepository> _log;
    // Fallback file repo cho trường hợp DB lỗi runtime
    private readonly string _legacyPath;
    private readonly object _legacyLock = new();
    private Dictionary<string, CustomerReview>? _legacyCache;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    public ReviewRepository(TourkitAiDb db, IWebHostEnvironment env, ILogger<ReviewRepository> log)
    {
        _db   = db;
        _log  = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _legacyPath = Path.Combine(dir, "reviews.json");
    }

    /// Khởi động: chạy schema init. Gọi từ Program.cs sau khi build.
    public async Task InitAsync(CancellationToken ct = default)
    {
        await _db.InitAsync(ct);
    }

    /// Backfill TenantId cho rows legacy (migrated từ JSON cũ có TenantId="").
    /// Gọi 1 lần thủ công khi muốn dọn — không tự chạy vì cần biết tenantId.
    /// Trả số rows đã update.
    public async Task<int> BackfillTenantIdAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return 0;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            // Nếu đã có row (tenantId, customerId) thật → bỏ qua (avoid PK violation), chỉ update row có TenantId=''
            // mà KHÔNG có row tương ứng (tenantId, customerId) khác.
            var rows = await c.ExecuteAsync(@"
UPDATE r SET TenantId = @t
FROM dbo.Reviews r
WHERE r.TenantId = ''
  AND NOT EXISTS (
    SELECT 1 FROM dbo.Reviews x WHERE x.TenantId = @t AND x.CustomerId = r.CustomerId
  );", new { t = tenantId });
            _log.LogInformation("[ReviewRepo] Backfill TenantId={Tenant}: {N} rows updated", tenantId, rows);
            return rows;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ReviewRepo] Backfill TenantId fail");
            return 0;
        }
    }

    /// Lấy review của 1 KH theo TenantId + CustomerId (DB), fallback file khi DB lỗi.
    /// Nếu không tìm thấy theo tenant chính xác → thử bản TenantId="" (legacy migrated rows từ JSON cũ
    /// chưa có tenant scope). Hành vi này là transitional: sau khi backfill tenantId, fallback sẽ ít gặp.
    public CustomerReview? Get(string tenantId, string customerId)
    {
        try
        {
            using var c = _db.Open();
            // Match chính xác (TenantId, CustomerId) trước
            var row = c.QueryFirstOrDefault<ReviewRow>(
                @"SELECT TOP 1 DataJson, FeedbackJson FROM dbo.Reviews
                  WHERE TenantId = @t AND CustomerId = @id
                  ORDER BY GeneratedAt DESC",
                new { t = tenantId, id = customerId });
            if (row != null) return Hydrate(row);

            // Fallback legacy: rows migrated từ JSON cũ có TenantId="" (transitional)
            if (!string.IsNullOrEmpty(tenantId))
            {
                row = c.QueryFirstOrDefault<ReviewRow>(
                    @"SELECT TOP 1 DataJson, FeedbackJson FROM dbo.Reviews
                      WHERE TenantId = '' AND CustomerId = @id
                      ORDER BY GeneratedAt DESC",
                    new { id = customerId });
                if (row != null)
                {
                    _log.LogInformation("[ReviewRepo] Get KH {Id}: dùng row legacy TenantId='' (chưa backfill)", customerId);
                    return Hydrate(row);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ReviewRepo] DB lỗi → fallback file");
            return LegacyGet(customerId);
        }
    }

    /// Slim projection cho bulk pre-fetch (workflow loop): CHỈ 3 cột (không hydrate DataJson nặng).
    /// Đủ tín hiệu cho customer-auto-review quyết định: skippedFresh (theo GeneratedAt) + skippedUnchanged (theo Fingerprint).
    public record ReviewSlim(string CustomerId, string Fingerprint, long GeneratedAtMs);

    /// Bulk fetch review dạng slim cho tenant + N customerId (1 SELECT thay N × Get()).
    /// KHÔNG legacy fallback: query hit thẳng PK (TenantId, CustomerId) → mỗi KH ≤ 1 row, không cần dedup.
    /// KH có row legacy TenantId='' chưa backfill sẽ bị coi là "chưa review" → workflow review lại 1 lần
    /// (bounded), rồi từ chu kỳ sau tự nhận ra qua tenant row mới. Nếu cần dọn: POST /reviews/admin/backfill-tenant.
    /// ids rỗng → dict rỗng, không query DB. DB lỗi → log warning + dict rỗng.
    /// Lấy danh sách CustomerId ĐẾN HẠN review lại — cho DB-driven Pass 2 của customer-auto-review workflow.
    /// GeneratedAt < cutoff (cutoff = now - reReviewDays) → review đã quá tuổi cần chấm lại.
    /// ORDER BY GeneratedAt ASC → ưu tiên review tồn đọng LÂU NHẤT trước.
    /// take: cap số row (tránh 1 lượt scan quá lâu). Trả list rỗng nếu tenantId rỗng / DB lỗi.
    public List<string> GetDueForReReview(string tenantId, long cutoffMs, int take)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return new List<string>();
        if (take < 1) return new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var c = _db.Open();
            var ids = c.Query<string>(
                @"SELECT TOP (@take) CustomerId
                  FROM dbo.Reviews
                  WHERE TenantId = @t AND GeneratedAt < @cutoff
                  ORDER BY GeneratedAt ASC",
                new { t = tenantId, cutoff = cutoffMs, take }).AsList();
            sw.Stop();
            _log.LogDebug("[ReviewRepo] GetDueForReReview tenant={T} cutoff={C} take={K} → {N} ids ({Ms}ms)",
                tenantId, cutoffMs, take, ids.Count, sw.ElapsedMilliseconds);
            return ids;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "[ReviewRepo] GetDueForReReview tenant={T} ({Ms}ms) lỗi — trả list rỗng",
                tenantId, sw.ElapsedMilliseconds);
            return new List<string>();
        }
    }

    public Dictionary<string, ReviewSlim> GetBulkSlim(string tenantId, IEnumerable<string> customerIds)
    {
        // GUARD: tenantId rỗng = sai cấu hình → throw sớm thay vì query nhầm row legacy TenantId=''.
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId bắt buộc — bulk slim không hỗ trợ legacy TenantId=''", nameof(tenantId));
        var idList = customerIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var map = new Dictionary<string, ReviewSlim>();
        if (idList.Count == 0) return map;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var c = _db.Open();
            var rows = c.Query<ReviewSlim>(
                @"SELECT CustomerId, Fingerprint, GeneratedAt AS GeneratedAtMs
                  FROM dbo.Reviews
                  WHERE TenantId = @t AND CustomerId IN @ids",
                new { t = tenantId, ids = idList });
            foreach (var r in rows)
                map[r.CustomerId] = r;
            sw.Stop();
            _log.LogDebug("[ReviewRepo] GetBulkSlim tenant={T} ids={N} → rows={M} ({Ms}ms)",
                tenantId, idList.Count, map.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "[ReviewRepo] GetBulkSlim tenant={T} ids={N} ({Ms}ms) lỗi — trả dict rỗng",
                tenantId, idList.Count, sw.ElapsedMilliseconds);
        }
        return map;
    }

    /// Trả TẤT CẢ review (limit cứng 5000 — review service dùng list-status cho /customers list).
    public IReadOnlyDictionary<string, CustomerReview> All()
    {
        try
        {
            using var c = _db.Open();
            var rows = c.Query<ReviewRow>(
                @"SELECT DataJson, FeedbackJson FROM dbo.Reviews ORDER BY GeneratedAt DESC");
            var map = new Dictionary<string, CustomerReview>();
            foreach (var r in rows)
            {
                var rev = Hydrate(r);
                if (rev != null && !map.ContainsKey(rev.CustomerId))
                    map[rev.CustomerId] = rev;
            }
            return map;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ReviewRepo] DB lỗi (All) → fallback file");
            return LegacyAll();
        }
    }

    /// Upsert review với TenantId. Lưu cả full DataJson + duplicate column cho index.
    /// GUARD: tenantId BẮT BUỘC — chặn insert row TenantId='' làm hỏng data model multi-tenant.
    public void Save(CustomerReview review, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId bắt buộc — không được lưu review với TenantId=''", nameof(tenantId));
        try
        {
            var dataJson = JsonSerializer.Serialize(review, _jsonOpts);
            var feedbackJson = review.Feedback != null
                ? JsonSerializer.Serialize(review.Feedback, _jsonOpts) : null;
            var alertLevel = review.Alert?.Level;
            // GeneratedAt: convert string ISO → unix ms (BIGINT trong schema)
            long genMs = DateTime.TryParse(review.GeneratedAt, out var dt)
                ? new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var c = _db.Open();
            // SQL Server MERGE upsert (composite PK TenantId+CustomerId).
            // IsSync=0 ở CẢ INSERT lẫn UPDATE: review mới hoặc re-review = data đổi → worker phải sync lại.
            c.Execute(@"
MERGE dbo.Reviews AS T
USING (SELECT @CustomerId AS CustomerId, @TenantId AS TenantId) AS S
   ON T.CustomerId = S.CustomerId AND T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    [Rank]=@Rank, AlertLevel=@AlertLevel, Fingerprint=@Fingerprint,
    DataJson=@DataJson, AiProvider=@AiProvider, AiModel=@AiModel,
    TokensIn=@TokensIn, TokensOut=@TokensOut, GeneratedAt=@GeneratedAt,
    FeedbackJson=@FeedbackJson, IsSync=0
WHEN NOT MATCHED THEN INSERT
    (CustomerId, TenantId, [Rank], AlertLevel, Fingerprint, DataJson,
     AiProvider, AiModel, TokensIn, TokensOut, GeneratedAt, FeedbackJson, IsSync)
VALUES
    (@CustomerId, @TenantId, @Rank, @AlertLevel, @Fingerprint, @DataJson,
     @AiProvider, @AiModel, @TokensIn, @TokensOut, @GeneratedAt, @FeedbackJson, 0);",
                new
                {
                    CustomerId   = review.CustomerId,
                    TenantId     = tenantId,
                    Rank         = review.Rank,
                    AlertLevel   = alertLevel,
                    Fingerprint  = review.DataFingerprint,
                    DataJson     = dataJson,
                    AiProvider   = review.AiProvider,
                    AiModel      = review.AiModel,
                    TokensIn     = review.TokensIn,
                    TokensOut    = review.TokensOut,
                    GeneratedAt  = genMs,
                    FeedbackJson = feedbackJson
                });
        }
        catch (ArgumentException) { throw; }   // guard là bug logic, không nuốt vào file fallback
        catch (Exception ex)
        {
            _log.LogError(ex, "[ReviewRepo] DB save lỗi cho KH {Id} → fallback file", review.CustomerId);
            LegacySave(review);
        }
    }

    /// Cập nhật feedback của review (TenantId + CustomerId). Trả true nếu KH tồn tại.
    public bool SetFeedback(string tenantId, string customerId, ReviewFeedback fb)
    {
        try
        {
            var feedbackJson = JsonSerializer.Serialize(fb, _jsonOpts);
            using var c = _db.Open();
            // Tìm row theo (tenant, customer) hoặc fallback legacy (tenant="")
            var existing = c.QueryFirstOrDefault<(string DataJson, string MatchedTenant)>(
                @"SELECT TOP 1 DataJson, TenantId AS MatchedTenant FROM dbo.Reviews
                  WHERE (TenantId = @t OR TenantId = '') AND CustomerId = @id
                  ORDER BY (CASE WHEN TenantId = @t THEN 0 ELSE 1 END), GeneratedAt DESC",
                new { t = tenantId, id = customerId });
            if (string.IsNullOrEmpty(existing.DataJson)) return false;

            var rev = JsonSerializer.Deserialize<CustomerReview>(existing.DataJson, _jsonOpts);
            if (rev == null) return false;
            var updated = rev with { Feedback = fb };
            var dataJson = JsonSerializer.Serialize(updated, _jsonOpts);

            // Feedback đổi cũng reset IsSync=0 — worker đồng bộ phiên bản review có feedback mới.
            var rows = c.Execute(
                @"UPDATE dbo.Reviews SET FeedbackJson = @fb, DataJson = @dj, IsSync = 0
                  WHERE TenantId = @t AND CustomerId = @id",
                new { fb = feedbackJson, dj = dataJson, t = existing.MatchedTenant, id = customerId });
            return rows > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ReviewRepo] DB SetFeedback lỗi → fallback file");
            return LegacySetFeedback(customerId, fb);
        }
    }

    // ─── Fallback file (đọc/ghi reviews.json khi DB không khả dụng) ──────────────

    private Dictionary<string, CustomerReview> LegacyLoad()
    {
        if (_legacyCache != null) return _legacyCache;
        lock (_legacyLock)
        {
            if (_legacyCache != null) return _legacyCache;
            if (!File.Exists(_legacyPath)) return _legacyCache = new();
            try
            {
                var json = File.ReadAllText(_legacyPath);
                _legacyCache = JsonSerializer.Deserialize<Dictionary<string, CustomerReview>>(json) ?? new();
            }
            catch { _legacyCache = new(); }
            return _legacyCache;
        }
    }

    private CustomerReview? LegacyGet(string id)
        => LegacyLoad().TryGetValue(id, out var r) ? r : null;

    private IReadOnlyDictionary<string, CustomerReview> LegacyAll()
        => new Dictionary<string, CustomerReview>(LegacyLoad());

    private void LegacySave(CustomerReview r)
    {
        lock (_legacyLock)
        {
            var map = LegacyLoad();
            map[r.CustomerId] = r;
            try { File.WriteAllText(_legacyPath, JsonSerializer.Serialize(map, _jsonOpts)); }
            catch (Exception ex) { _log.LogError(ex, "Legacy file write fail"); }
        }
    }

    private bool LegacySetFeedback(string id, ReviewFeedback fb)
    {
        lock (_legacyLock)
        {
            var map = LegacyLoad();
            if (!map.TryGetValue(id, out var existing)) return false;
            map[id] = existing with { Feedback = fb };
            try { File.WriteAllText(_legacyPath, JsonSerializer.Serialize(map, _jsonOpts)); }
            catch (Exception ex) { _log.LogError(ex, "Legacy file write fail"); return false; }
            return true;
        }
    }

    // ─── Hydration helpers ─────────────────────────────────────────────────────

    private CustomerReview? Hydrate(ReviewRow row)
    {
        if (string.IsNullOrEmpty(row.DataJson)) return null;
        try
        {
            var rev = JsonSerializer.Deserialize<CustomerReview>(row.DataJson, _jsonOpts);
            if (rev != null && row.FeedbackJson != null)
            {
                var fb = JsonSerializer.Deserialize<ReviewFeedback>(row.FeedbackJson, _jsonOpts);
                if (fb != null) rev = rev with { Feedback = fb };
            }
            return rev;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ReviewRepo] Hydrate JSON fail");
            return null;
        }
    }

    private sealed class ReviewRow
    {
        public string DataJson { get; set; } = "";
        public string? FeedbackJson { get; set; }
    }

    // ─── Static helper giữ nguyên (ReviewService gọi để check stale fingerprint) ─

    /// SHA256 hex của customer JSON canonical — đổi tức là data đã thay đổi → review stale.
    /// Dùng JsonSerializer options nhất quán nên cùng customer cho cùng fingerprint qua các process.
    public static string FingerprintFor(Customer c)
    {
        var json = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = false });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 32);
    }
}
