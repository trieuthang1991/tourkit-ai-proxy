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

    /// Khởi động: chạy schema init + migrate file JSON cũ nếu DB rỗng. Gọi từ Program.cs sau khi build.
    public async Task InitAsync(CancellationToken ct = default)
    {
        await _db.InitAsync(ct);
        await TryMigrateFromJsonAsync(ct);
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

    /// Overload cũ không có tenantId — giữ cho code cũ chưa thread (vd batch endpoint).
    /// Tra DB không filter TenantId, lấy bản gần nhất. Khuyến nghị dùng overload có tenantId.
    [Obsolete("Dùng Get(tenantId, customerId) thay vì — overload này không scope theo tenant")]
    public CustomerReview? Get(string customerId) => Get("", customerId);

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
    public void Save(CustomerReview review, string tenantId)
    {
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
            // SQL Server MERGE upsert (composite PK TenantId+CustomerId; TenantId='' khi không biết).
            c.Execute(@"
MERGE dbo.Reviews AS T
USING (SELECT @CustomerId AS CustomerId, @TenantId AS TenantId) AS S
   ON T.CustomerId = S.CustomerId AND T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    [Rank]=@Rank, AlertLevel=@AlertLevel, Fingerprint=@Fingerprint,
    DataJson=@DataJson, AiProvider=@AiProvider, AiModel=@AiModel,
    TokensIn=@TokensIn, TokensOut=@TokensOut, GeneratedAt=@GeneratedAt,
    FeedbackJson=@FeedbackJson
WHEN NOT MATCHED THEN INSERT
    (CustomerId, TenantId, [Rank], AlertLevel, Fingerprint, DataJson,
     AiProvider, AiModel, TokensIn, TokensOut, GeneratedAt, FeedbackJson)
VALUES
    (@CustomerId, @TenantId, @Rank, @AlertLevel, @Fingerprint, @DataJson,
     @AiProvider, @AiModel, @TokensIn, @TokensOut, @GeneratedAt, @FeedbackJson);",
                new
                {
                    CustomerId   = review.CustomerId,
                    TenantId     = tenantId ?? "",
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
        catch (Exception ex)
        {
            _log.LogError(ex, "[ReviewRepo] DB save lỗi cho KH {Id} → fallback file", review.CustomerId);
            LegacySave(review);
        }
    }

    /// Overload không có tenantId — back-compat cho code cũ. Lưu với TenantId="".
    [Obsolete("Dùng Save(review, tenantId) thay vì — overload này lưu TenantId='' không scope theo tenant")]
    public void Save(CustomerReview review) => Save(review, "");

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

            var rows = c.Execute(
                @"UPDATE dbo.Reviews SET FeedbackJson = @fb, DataJson = @dj
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

    /// Overload back-compat — KHÔNG scope theo tenant.
    [Obsolete("Dùng SetFeedback(tenantId, customerId, fb) — overload này tìm cross-tenant")]
    public bool SetFeedback(string customerId, ReviewFeedback fb) => SetFeedback("", customerId, fb);

    // ─── Migration JSON → DB (chạy 1 lần lúc khởi động nếu DB rỗng) ──────────────

    private async Task TryMigrateFromJsonAsync(CancellationToken ct)
    {
        if (!File.Exists(_legacyPath)) return;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Reviews");
            if (count > 0)
            {
                _log.LogInformation("[ReviewRepo] DB đã có {N} review, skip migrate", count);
                return;
            }

            var json = await File.ReadAllTextAsync(_legacyPath, ct);
            var map = JsonSerializer.Deserialize<Dictionary<string, CustomerReview>>(json) ?? new();
            if (map.Count == 0) return;

            _log.LogInformation("[ReviewRepo] DB rỗng, migrate {N} review từ {Path}", map.Count, _legacyPath);
            int ok = 0;
            foreach (var (_, review) in map)
            {
                try { Save(review); ok++; }
                catch (Exception ex) { _log.LogWarning(ex, "Migrate KH {Id} fail", review.CustomerId); }
            }
            _log.LogInformation("[ReviewRepo] Migrate xong {Ok}/{Total} review", ok, map.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ReviewRepo] Migrate JSON fail — bỏ qua, giữ file cũ");
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
