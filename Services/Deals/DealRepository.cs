using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Deals;

/// <summary>
/// SQL Server-backed cache cho Deal AI scoring — pattern giống ReviewRepository.
///   • dbo.DealScores: 1 row per (TenantId, DealId), Fingerprint check stale, IsSync flag cho worker.
///   • Board snapshot per tenant: lưu file (lightweight, không phổ biến lưu DB).
/// FALLBACK: DB lỗi → file cũ data/deal-cache.json (dual-mode trong transition).
/// MIGRATION: file → DB tự chạy 1 lần lúc khởi động nếu DB rỗng.
/// </summary>
public class DealRepository
{
    public record CachedScore(string Fingerprint, DealScore Score, string SavedAt);

    private readonly TourkitAiDb _db;
    private readonly ILogger<DealRepository> _log;
    private readonly string _legacyPath;
    private readonly object _legacyLock = new();
    // Board snapshot vẫn file (mỗi tenant 1 board, đè liên tục — không cần DB)
    private Dictionary<string, DealBoard> _boards = new();
    private Dictionary<string, CachedScore> _legacyScoresCache = new();
    private bool _legacyLoaded;

    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    private record Snapshot(
        [property: JsonPropertyName("scores")] Dictionary<string, CachedScore> Scores,
        [property: JsonPropertyName("boards")] Dictionary<string, DealBoard> Boards);

    public DealRepository(TourkitAiDb db, IWebHostEnvironment env, ILogger<DealRepository> log)
    {
        _db   = db;
        _log  = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _legacyPath = Path.Combine(dir, "deal-cache.json");

        // Load boards từ file (boards giữ file — không migrate sang DB).
        if (File.Exists(_legacyPath))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_legacyPath), _opts);
                if (p?.Boards != null) _boards = p.Boards;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Đọc deal-cache.json (boards) lỗi"); }
        }
    }

    /// Khởi động: ensure schema (TourkitAiDb gọi) + migrate scores từ file nếu DB rỗng.
    public async Task InitAsync(CancellationToken ct = default)
    {
        await TryMigrateScoresFromFileAsync(ct);
    }

    // ─── DealScores: DB (PushDb.dbo.DealScores) ──────────────────────────────

    /// Lấy điểm cache nếu fingerprint khớp (deal chưa đổi). Null → cần chấm lại.
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
            _log.LogWarning(ex, "[DealRepo] DB GetScore lỗi → fallback file");
            var legacy = LegacyPeek(tenant, id);
            return (legacy != null && legacy.Fingerprint == fingerprint) ? legacy.Score : null;
        }
    }

    /// Đã có cache (KHÔNG check fingerprint) — dùng cho FE tô "đã chấm/chưa chấm".
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
            _log.LogWarning(ex, "[DealRepo] DB PeekCached lỗi → fallback file");
            return LegacyPeek(tenant, id);
        }
    }

    /// Upsert score.
    public void SaveScore(string tenant, int id, string fingerprint, DealScore score)
    {
        try
        {
            var dataJson = JsonSerializer.Serialize(score, _opts);
            var genMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var c = _db.Open();
            c.Execute(@"
MERGE dbo.DealScores AS T
USING (SELECT @DealId AS DealId, @TenantId AS TenantId) AS S
   ON T.DealId = S.DealId AND T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    WinRate=@WinRate, [Level]=@Level, Fingerprint=@Fingerprint,
    DataJson=@DataJson, AiProvider=@AiProvider, AiModel=@AiModel,
    TokensIn=NULL, TokensOut=NULL, GeneratedAt=@GeneratedAt,
    IsSync=0
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
                    WinRate     = score.WinRate,
                    Level       = score.Level,
                    Fingerprint = fingerprint,
                    DataJson    = dataJson,
                    AiProvider  = score.AiProvider,
                    AiModel     = score.AiModel,
                    GeneratedAt = genMs
                });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[DealRepo] DB SaveScore lỗi cho deal {Id} → fallback file", id);
            LegacySave(tenant, id, fingerprint, score);
        }
    }

    // ─── Board snapshot: file (không cần DB) ─────────────────────────────────

    public DealBoard? GetBoard(string tenant)
    {
        lock (_legacyLock) return _boards.TryGetValue(tenant, out var b) ? b : null;
    }

    public void SaveBoard(string tenant, DealBoard board)
    {
        lock (_legacyLock) { _boards[tenant] = board; PersistBoards(); }
    }

    private void PersistBoards()
    {
        try { File.WriteAllText(_legacyPath, JsonSerializer.Serialize(new Snapshot(_legacyScoresCache, _boards), _opts)); }
        catch (Exception ex) { _log.LogError(ex, "[DealRepo] Ghi boards file lỗi"); }
    }

    // ─── Migration JSON → DB (chạy 1 lần lúc khởi động) ──────────────────────

    private async Task TryMigrateScoresFromFileAsync(CancellationToken ct)
    {
        if (!File.Exists(_legacyPath)) return;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var count = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.DealScores");
            if (count > 0)
            {
                _log.LogInformation("[DealRepo] DB đã có {N} score, skip migrate", count);
                return;
            }

            var json = await File.ReadAllTextAsync(_legacyPath, ct);
            var snap = JsonSerializer.Deserialize<Snapshot>(json, _opts);
            if (snap?.Scores == null || snap.Scores.Count == 0) return;

            _log.LogInformation("[DealRepo] DB rỗng, migrate {N} score từ {Path}", snap.Scores.Count, _legacyPath);
            int ok = 0;
            foreach (var (key, cached) in snap.Scores)
            {
                // Key format "tenant:id" — split
                var idx = key.IndexOf(':');
                if (idx <= 0) continue;
                var tenant = key.Substring(0, idx);
                if (!int.TryParse(key.Substring(idx + 1), out var dealId)) continue;
                try { SaveScore(tenant, dealId, cached.Fingerprint, cached.Score); ok++; }
                catch (Exception ex) { _log.LogWarning(ex, "Migrate score key {K} fail", key); }
            }
            _log.LogInformation("[DealRepo] Migrate xong {Ok}/{Total} score", ok, snap.Scores.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealRepo] Migrate JSON fail — bỏ qua, giữ file cũ");
        }
    }

    // ─── Fallback file (đọc/ghi deal-cache.json khi DB lỗi runtime) ──────────

    private CachedScore? LegacyPeek(string tenant, int id)
    {
        EnsureLegacyLoaded();
        lock (_legacyLock)
            return _legacyScoresCache.TryGetValue($"{tenant}:{id}", out var c) ? c : null;
    }

    private void LegacySave(string tenant, int id, string fingerprint, DealScore score)
    {
        EnsureLegacyLoaded();
        lock (_legacyLock)
        {
            _legacyScoresCache[$"{tenant}:{id}"] = new CachedScore(fingerprint, score, DateTime.UtcNow.ToString("o"));
            try { File.WriteAllText(_legacyPath, JsonSerializer.Serialize(new Snapshot(_legacyScoresCache, _boards), _opts)); }
            catch (Exception ex) { _log.LogError(ex, "Legacy write lỗi"); }
        }
    }

    private void EnsureLegacyLoaded()
    {
        if (_legacyLoaded) return;
        lock (_legacyLock)
        {
            if (_legacyLoaded) return;
            try
            {
                if (File.Exists(_legacyPath))
                {
                    var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_legacyPath), _opts);
                    if (snap?.Scores != null) _legacyScoresCache = snap.Scores;
                }
            }
            catch { _legacyScoresCache = new(); }
            _legacyLoaded = true;
        }
    }

    private sealed class DealScoreRow
    {
        public string DataJson { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long GeneratedAt { get; set; }
    }
}
