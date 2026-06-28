using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Deals;

/// <summary>
/// Cache Deal AI scoring:
///   • Score cache: SQL Server dbo.DealScores (truth, persistent, has Fingerprint check)
///   • Board snapshot per tenant: Redis (key tkai:deal-board:{tenant}, no expiry)
///   • Fallback khi DB lỗi runtime: Redis hash tkai:deal-scores:{tenant}/{dealId}
///   • Cuối cùng (Redis cũng down): in-memory dict — accept khả năng mất qua restart
/// KHÔNG còn lưu file data/deal-cache.json (theo yêu cầu).
/// </summary>
public class DealRepository
{
    public record CachedScore(string Fingerprint, DealScore Score, string SavedAt);

    private readonly TourkitAiDb _db;
    private readonly RedisStore _redis;
    private readonly ILogger<DealRepository> _log;

    // In-memory final fallback (Redis + DB cả 2 down)
    private readonly Dictionary<string, CachedScore> _memScores = new();
    private readonly Dictionary<string, DealBoard> _memBoards = new();
    private readonly object _memLock = new();

    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    public DealRepository(TourkitAiDb db, RedisStore redis, ILogger<DealRepository> log)
    {
        _db    = db;
        _redis = redis;
        _log   = log;
        _log.LogInformation("DealRepository: scores=DB(dbo.DealScores) + Redis fallback, boards=Redis ({Backend})", redis.Backend);
    }

    /// Khởi động: migrate legacy file (nếu có từ deploy cũ) sang DB + xoá file.
    public async Task InitAsync(CancellationToken ct = default)
    {
        await TryMigrateLegacyFileAsync(ct);
    }

    // ─── DealScores: DB (truth) + Redis (fallback) ───────────────────────────

    public DealScore? GetScore(string tenant, int id, string fingerprint)
    {
        try
        {
            using var c = _db.Open();
            var row = c.QueryFirstOrDefault<DealScoreRow>(
                @"SELECT DataJson, Fingerprint FROM dbo.DealScores
                  WHERE TenantId = @t AND DealId = @id",
                new { t = tenant, id = id.ToString() });
            if (row == null || row.Fingerprint != fingerprint) return null;
            return JsonSerializer.Deserialize<DealScore>(row.DataJson, _opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] DB GetScore lỗi → fallback Redis");
            var cached = RedisGetScore(tenant, id);
            return (cached != null && cached.Fingerprint == fingerprint) ? cached.Score : null;
        }
    }

    public CachedScore? PeekCached(string tenant, int id)
    {
        try
        {
            using var c = _db.Open();
            var row = c.QueryFirstOrDefault<DealScoreRow>(
                @"SELECT DataJson, Fingerprint, GeneratedAt FROM dbo.DealScores
                  WHERE TenantId = @t AND DealId = @id",
                new { t = tenant, id = id.ToString() });
            if (row == null) return null;
            var score = JsonSerializer.Deserialize<DealScore>(row.DataJson, _opts);
            if (score == null) return null;
            var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.GeneratedAt).ToString("o");
            return new CachedScore(row.Fingerprint, score, savedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] DB PeekCached lỗi → fallback Redis");
            return RedisGetScore(tenant, id);
        }
    }

    public void SaveScore(string tenant, int id, string fingerprint, DealScore score)
    {
        var dataJson = JsonSerializer.Serialize(score, _opts);
        var genMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool dbOk = false;
        try
        {
            using var c = _db.Open();
            c.Execute(@"
MERGE dbo.DealScores AS T
USING (SELECT @DealId AS DealId, @TenantId AS TenantId) AS S
   ON T.DealId = S.DealId AND T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    WinRate=@WinRate, [Level]=@Level, Fingerprint=@Fingerprint,
    DataJson=@DataJson, AiProvider=@AiProvider, AiModel=@AiModel,
    TokensIn=NULL, TokensOut=NULL, GeneratedAt=@GeneratedAt, IsSync=0
WHEN NOT MATCHED THEN INSERT
    (DealId, TenantId, WinRate, [Level], Fingerprint, DataJson,
     AiProvider, AiModel, TokensIn, TokensOut, GeneratedAt, IsSync)
VALUES
    (@DealId, @TenantId, @WinRate, @Level, @Fingerprint, @DataJson,
     @AiProvider, @AiModel, NULL, NULL, @GeneratedAt, 0);",
                new
                {
                    DealId      = id.ToString(),
                    TenantId    = tenant ?? "",
                    WinRate     = score.WinRate, Level = score.Level,
                    Fingerprint = fingerprint, DataJson = dataJson,
                    AiProvider  = score.AiProvider, AiModel = score.AiModel,
                    GeneratedAt = genMs
                });
            dbOk = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[DealRepo] DB SaveScore lỗi cho deal {Id} → ghi Redis", id);
        }

        // Luôn cập nhật Redis (mirror cho fallback đọc + dùng cross-instance nhanh).
        // Khi DB lỗi: Redis là chỗ duy nhất giữ score, write-through không có DB.
        var cached = new CachedScore(fingerprint, score, DateTime.UtcNow.ToString("o"));
        RedisSaveScore(tenant ?? "", id, cached);

        if (!dbOk)
        {
            // In-memory backup nếu Redis cũng down (RedisSaveScore sẽ trả false trong logs)
            lock (_memLock) _memScores[$"{tenant}:{id}"] = cached;
        }
    }

    // ─── Kiểm soát auto-review (cột AutoReviewCount/IsFinalized/FinalizedReason trên dbo.DealScores) ──

    public record ReviewControl(int AutoReviewCount, bool IsFinalized, string? FinalizedReason);

    /// Đọc trạng thái kiểm soát review của 1 deal. null nếu chưa có row score.
    public ReviewControl? GetReviewControl(string tenant, int id)
    {
        try
        {
            using var c = _db.Open();
            return c.QueryFirstOrDefault<ReviewControl>(
                @"SELECT AutoReviewCount, IsFinalized, FinalizedReason FROM dbo.DealScores
                  WHERE TenantId = @t AND DealId = @id",
                new { t = tenant, id = id.ToString() });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] GetReviewControl lỗi deal {Id}", id);
            return null;
        }
    }

    /// Tăng số lần workflow TỰ chấm deal này (+ thời điểm). Chấm tay KHÔNG gọi method này.
    public void MarkAutoReviewed(string tenant, int id)
    {
        try
        {
            using var c = _db.Open();
            c.Execute(
                @"UPDATE dbo.DealScores
                  SET AutoReviewCount = AutoReviewCount + 1, LastAutoReviewUtc = SYSUTCDATETIME()
                  WHERE TenantId = @t AND DealId = @id",
                new { t = tenant, id = id.ToString() });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] MarkAutoReviewed lỗi deal {Id}", id);
        }
    }

    /// Đánh cờ "đừng tự chấm/nhắc lại nữa". reason: 'manual' | 'status-changed' | 'aged'.
    public void SetFinalized(string tenant, int id, string reason)
    {
        try
        {
            using var c = _db.Open();
            c.Execute(
                @"UPDATE dbo.DealScores
                  SET IsFinalized = 1, FinalizedReason = @reason
                  WHERE TenantId = @t AND DealId = @id",
                new { t = tenant, id = id.ToString(), reason });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] SetFinalized lỗi deal {Id}", id);
        }
    }

    // ─── Board snapshot: Redis primary, in-memory final fallback ────────────

    public DealBoard? GetBoard(string tenant)
    {
        var json = _redis.Get($"deal-board:{tenant}");
        if (json != null)
        {
            try { return JsonSerializer.Deserialize<DealBoard>(json, _opts); }
            catch (Exception ex) { _log.LogWarning(ex, "[DealRepo] Parse board JSON lỗi"); }
        }
        lock (_memLock) return _memBoards.TryGetValue(tenant, out var b) ? b : null;
    }

    public void SaveBoard(string tenant, DealBoard board)
    {
        var json = JsonSerializer.Serialize(board, _opts);
        var ok = _redis.Set($"deal-board:{tenant}", json);   // no expiry
        if (!ok)
        {
            _log.LogWarning("[DealRepo] Lưu board Redis fail (Redis down?) → in-memory");
            lock (_memLock) _memBoards[tenant] = board;
        }
    }

    // ─── Redis score helpers (HASH per tenant cho lookup nhanh) ──────────────

    private CachedScore? RedisGetScore(string tenant, int id)
    {
        var json = _redis.HashGet($"deal-scores:{tenant}", id.ToString());
        if (json == null)
        {
            lock (_memLock) return _memScores.TryGetValue($"{tenant}:{id}", out var c) ? c : null;
        }
        try { return JsonSerializer.Deserialize<CachedScore>(json, _opts); }
        catch { return null; }
    }

    private void RedisSaveScore(string tenant, int id, CachedScore cached)
    {
        var json = JsonSerializer.Serialize(cached, _opts);
        _redis.HashSet($"deal-scores:{tenant}", id.ToString(), json);
    }

    // ─── Migration legacy file → DB + Redis board (1 lần) ───────────────────

    private async Task TryMigrateLegacyFileAsync(CancellationToken ct)
    {
        var legacyPath = "data/deal-cache.json";
        if (!File.Exists(legacyPath)) return;
        try
        {
            var json = await File.ReadAllTextAsync(legacyPath, ct);
            var snap = JsonSerializer.Deserialize<Snapshot>(json, _opts);

            // Scores → DB
            if (snap?.Scores != null && snap.Scores.Count > 0)
            {
                await using var c = await _db.OpenAsync(ct);
                var count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.DealScores");
                if (count == 0)
                {
                    _log.LogInformation("[DealRepo] Migrate {N} score từ file → DB", snap.Scores.Count);
                    foreach (var (key, cached) in snap.Scores)
                    {
                        var idx = key.IndexOf(':');
                        if (idx <= 0) continue;
                        var tenant = key.Substring(0, idx);
                        if (!int.TryParse(key.Substring(idx + 1), out var dealId)) continue;
                        try { SaveScore(tenant, dealId, cached.Fingerprint, cached.Score); } catch { }
                    }
                }
            }

            // Boards → Redis
            if (snap?.Boards != null)
            {
                foreach (var (tenant, board) in snap.Boards)
                    SaveBoard(tenant, board);
                _log.LogInformation("[DealRepo] Migrate {N} board từ file → Redis", snap.Boards.Count);
            }

            // Rename file để đánh dấu đã migrate (giữ làm backup, không xóa)
            File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
            _log.LogInformation("[DealRepo] File legacy renamed → deal-cache.json.migrated");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] Migrate legacy file fail (skip)");
        }
    }

    private record Snapshot(
        [property: JsonPropertyName("scores")] Dictionary<string, CachedScore> Scores,
        [property: JsonPropertyName("boards")] Dictionary<string, DealBoard> Boards);

    private sealed class DealScoreRow
    {
        public string DataJson { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long GeneratedAt { get; set; }
    }
}
