using System.Collections.Concurrent;
using System.Security.Cryptography;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure;

namespace TourkitAiProxy.Services.Security;

/// <summary>
/// Kho "code 1-lần" cho SSO CRM → Trav-ai: register-code GHI → exchange ĐỌC+XOÁ.
/// MIRROR KojiCRM/Auth/SsoCodeStore.cs bên CRM — 2 chế độ chọn bằng cờ "Sso:ForceInMemory"
/// (giống hệt web "SsoForceInMemory"):
///   - true / thiếu (MẶC ĐỊNH) → InMemory (ConcurrentDictionary + TTL). An toàn khi proxy chạy 1 process.
///   - false → Redis (RedisStore), chia sẻ giữa nhiều instance sau load-balancer.
///             Redis chưa cấu hình / lỗi → tự fallback InMemory (kèm cảnh báo).
///
/// Vì sao cần chọn: code sinh ở register-code (request A, server-to-server) nhưng dùng ở exchange
/// (request B, browser mở). Nhiều instance mà InMemory → B rơi instance khác → không tra ra code →
/// SSO hỏng ngẫu nhiên. 1 process thì InMemory chạy tốt, khỏi cần Redis.
/// </summary>
public sealed class SsoCodeStore
{
    private const string KeyPrefix = "sso:code:";

    private readonly RedisStore _redis;
    private readonly ILogger<SsoCodeStore> _log;
    private readonly bool _useRedis;

    private sealed class Entry
    {
        public required string Payload { get; init; }
        public DateTime ExpiresUtc { get; init; }
    }
    private readonly ConcurrentDictionary<string, Entry> _mem = new();

    public SsoCodeStore(RedisStore redis, IConfiguration cfg, ILogger<SsoCodeStore> log)
    {
        _redis = redis;
        _log = log;
        // Thiếu / không parse được → true (InMemory) — mặc định an toàn cho proxy 1 process.
        var forceInMemory = !bool.TryParse(cfg["Sso:ForceInMemory"], out var f) || f;
        _useRedis = !forceInMemory && redis.Available;
        if (!forceInMemory && !redis.Available)
            _log.LogWarning("Sso:ForceInMemory=false nhưng Redis không sẵn sàng → SsoCodeStore fallback InMemory.");
        _log.LogInformation("SsoCodeStore backend: {Backend}", _useRedis ? "Redis" : "InMemory");
    }

    /// <summary>Lưu code kèm TTL. Trả false nếu store thất bại (Redis down).</summary>
    public bool Save(string code, string payload, TimeSpan ttl)
    {
        if (_useRedis) return _redis.Set(KeyPrefix + code, payload, ttl);
        PruneExpired();
        _mem[code] = new Entry { Payload = payload, ExpiresUtc = DateTime.UtcNow.Add(ttl) };
        return true;
    }

    /// <summary>Đọc + XOÁ (one-time, chống replay). Trả null nếu không có / hết hạn / đã dùng.</summary>
    public string? TakeOnce(string code)
    {
        if (_useRedis)
        {
            var v = _redis.Get(KeyPrefix + code);
            if (!string.IsNullOrEmpty(v)) _redis.Delete(KeyPrefix + code);   // one-time (mirror web: Get rồi Remove)
            return string.IsNullOrEmpty(v) ? null : v;
        }
        if (!_mem.TryRemove(code, out var e)) return null;          // không có / đã dùng
        return DateTime.UtcNow > e.ExpiresUtc ? null : e.Payload;   // hết hạn
    }

    /// <summary>Code random 256-bit hex lowercase — khớp SsoController.GenCode bên CRM.</summary>
    public string GenCode() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// Dọn code quá hạn không ai dùng tới (chỉ InMemory). Code đã dùng thì TakeOnce gỡ rồi.
    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _mem)
            if (now > kv.Value.ExpiresUtc) _mem.TryRemove(kv.Key, out _);
    }
}
