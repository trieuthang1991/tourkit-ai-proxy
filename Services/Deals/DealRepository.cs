using System.Text.Json;
using System.Text.Json.Serialization;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Deals;

/// Cache file-backed cho Deals: điểm AI theo {tenant}:{dealId}+fingerprint (re-run bỏ qua deal không đổi)
/// + bảng xếp hạng mới nhất theo tenant (mở lại không cần chạy lại). data/deal-cache.json, lock-guarded.
public class DealRepository
{
    public record CachedScore(string Fingerprint, DealScore Score, string SavedAt);
    private record Snapshot(
        [property: JsonPropertyName("scores")] Dictionary<string, CachedScore> Scores,
        [property: JsonPropertyName("boards")] Dictionary<string, DealBoard> Boards);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, CachedScore> _scores = new();
    private Dictionary<string, DealBoard> _boards = new();
    private readonly ILogger<DealRepository> _log;
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DealRepository(IWebHostEnvironment env, ILogger<DealRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "deal-cache.json");
        if (File.Exists(_path))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), _opts);
                if (p != null) { _scores = p.Scores ?? new(); _boards = p.Boards ?? new(); }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Đọc deal-cache.json lỗi — reset"); }
        }
    }

    private static string Key(string tenant, int id) => $"{tenant}:{id}";

    /// Lấy điểm cache nếu fingerprint khớp (deal chưa đổi). Null → cần chấm lại.
    public DealScore? GetScore(string tenant, int id, string fingerprint)
    {
        lock (_lock)
            return _scores.TryGetValue(Key(tenant, id), out var c) && c.Fingerprint == fingerprint ? c.Score : null;
    }

    public void SaveScore(string tenant, int id, string fingerprint, DealScore score)
    {
        lock (_lock)
        {
            _scores[Key(tenant, id)] = new CachedScore(fingerprint, score, DateTime.UtcNow.ToString("o"));
            Persist();
        }
    }

    public DealBoard? GetBoard(string tenant)
    {
        lock (_lock) return _boards.TryGetValue(tenant, out var b) ? b : null;
    }

    public void SaveBoard(string tenant, DealBoard board)
    {
        lock (_lock) { _boards[tenant] = board; Persist(); }
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new Snapshot(_scores, _boards), _opts)); }
        catch (Exception ex) { _log.LogError(ex, "Ghi deal-cache.json lỗi"); }
    }
}
