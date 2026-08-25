using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Cache;

/// Cache prompt→response cho các tính năng AI tốn kém (Visa extract/score, Deal score, TourBuilder).
/// Key = sha256(feature|model|prompt) → JSON. TTL 24h. Redis nếu có, fallback in-memory.
/// Mục đích: NV chấm cùng hồ sơ 2 lần trong ngày → KHÔNG gọi AI lần 2.
public class AiResponseCache
{
    private const int TtlHours = 24;
    private readonly RedisProvider _redis;
    private readonly ILogger<AiResponseCache> _log;
    private readonly Dictionary<string, (DateTime Exp, string Val)> _mem = new();
    private readonly object _memLock = new();

    public AiResponseCache(RedisProvider redis, ILogger<AiResponseCache> log)
    {
        _redis = redis; _log = log;
    }

    public static string Hash(string feature, string? model, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{feature}|{model ?? "?"}|{content}"));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    /// Trả T cached; null nếu miss.
    public T? TryGet<T>(string key) where T : class
    {
        try
        {
            var raw = ReadRaw(key);
            if (string.IsNullOrEmpty(raw)) return null;
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (Exception ex) { _log.LogWarning(ex, "AiResponseCache get {Key} lỗi", key); return null; }
    }

    public void Save<T>(string key, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            WriteRaw(key, json);
        }
        catch (Exception ex) { _log.LogWarning(ex, "AiResponseCache set {Key} lỗi", key); }
    }

    private string? ReadRaw(string key)
    {
        if (_redis.Available)
        {
            try { var s = _redis.Db!.StringGet("tkai:ai:" + key); return s.HasValue ? (string?)s : null; }
            catch (Exception ex) { _log.LogWarning(ex, "Redis read fall back to mem"); }
        }
        lock (_memLock)
        {
            if (_mem.TryGetValue(key, out var v) && v.Exp > DateTime.UtcNow) return v.Val;
            _mem.Remove(key);
            return null;
        }
    }

    private void WriteRaw(string key, string val)
    {
        if (_redis.Available)
        {
            try { _redis.Db!.StringSet("tkai:ai:" + key, val, TimeSpan.FromHours(TtlHours)); return; }
            catch (Exception ex) { _log.LogWarning(ex, "Redis write fall back to mem"); }
        }
        lock (_memLock) { _mem[key] = (DateTime.UtcNow.AddHours(TtlHours), val); }
    }
}
