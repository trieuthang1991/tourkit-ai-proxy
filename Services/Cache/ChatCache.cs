using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Cache;

/// <summary>
/// Cache cho Chat-Analytics. Dùng REDIS nếu có Redis:ConnectionString (chia sẻ qua nhiều instance +
/// sống qua restart), ngược lại fallback IN-MEMORY. Giá trị lưu dạng JSON (string).
/// Connection string nhận cả dạng "ENC:" (mã hóa Crypton — copy thẳng từ TourKit.Api) → tự giải mã.
/// Key gắn prefix "tkai:" để không đụng key của TourKit trong cùng Redis.
/// </summary>
public class ChatCache
{
    private const string Prefix = "tkai:";
    private readonly IDatabase? _redis;
    private readonly ConnectionMultiplexer? _mux;
    private readonly ConcurrentDictionary<string, (string Json, DateTime Exp)> _mem = new();
    private readonly ILogger<ChatCache> _log;

    public string Backend { get; }

    public ChatCache(IConfiguration cfg, ILogger<ChatCache> log)
    {
        _log = log;
        var conn = cfg["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(conn) && conn.StartsWith("ENC:"))
            conn = Crypton.Decrypt(conn.Substring(4));   // giải mã giống TourKit (cùng Crypton)

        if (string.IsNullOrWhiteSpace(conn))
        {
            Backend = "in-memory (no Redis configured)";
        }
        else
        {
            try
            {
                var options = ConfigurationOptions.Parse(conn);
                options.AbortOnConnectFail = false;       // không throw lúc startup nếu Redis tạm xuống
                var mux = ConnectionMultiplexer.Connect(options);
                _redis = mux.GetDatabase();
                _mux = mux;
                Backend = $"Redis ({mux.GetEndPoints().FirstOrDefault()}, connected={mux.IsConnected})";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Kết nối Redis thất bại → fallback in-memory cache");
                Backend = "in-memory (Redis connect failed)";
            }
        }
        _log.LogInformation("ChatCache backend: {Backend}", Backend);
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        string? json = null;
        if (_redis != null)
        {
            try { var v = _redis.StringGet(Prefix + key); if (v.HasValue) json = v!; }
            catch (Exception ex) { _log.LogWarning(ex, "Redis GET fail ({Key}) — bỏ qua cache", key); }
        }
        else if (_mem.TryGetValue(key, out var e) && e.Exp > DateTime.UtcNow)
        {
            json = e.Json;
        }

        if (string.IsNullOrEmpty(json)) return false;
        try { value = JsonSerializer.Deserialize<T>(json); return value != null; }
        catch { return false; }
    }

    public void Set<T>(string key, T value, TimeSpan ttl)
    {
        string json;
        try { json = JsonSerializer.Serialize(value); } catch { return; }

        if (_redis != null)
        {
            try { _redis.StringSet(Prefix + key, json, ttl); }
            catch (Exception ex) { _log.LogWarning(ex, "Redis SET fail ({Key})", key); }
        }
        else
        {
            _mem[key] = (json, DateTime.UtcNow.Add(ttl));
        }
    }

    /// Xóa toàn bộ cache Chat-Analytics của 1 tenant (cả full-response `r|` lẫn CRM-data `d|`).
    /// Trả về số key đã xóa. Dùng cho nút "Xóa cache" để buộc gọi lại số liệu mới.
    public int ClearTenant(string tenant)
    {
        int n = 0;
        if (_redis != null && _mux != null)
        {
            var pattern = Prefix + "*|" + tenant + "|*";   // khớp tkai:r|{tenant}|… và tkai:d|{tenant}|…
            try
            {
                foreach (var ep in _mux.GetEndPoints())
                {
                    var server = _mux.GetServer(ep);
                    if (!server.IsConnected || server.IsReplica) continue;
                    foreach (var key in server.Keys(pattern: pattern)) { _redis.KeyDelete(key); n++; }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Redis clear cache tenant {T} fail", tenant); }
        }
        else
        {
            var pr = "r|" + tenant + "|";
            var pd = "d|" + tenant + "|";
            foreach (var k in _mem.Keys.ToList())
                if (k.StartsWith(pr) || k.StartsWith(pd)) { _mem.TryRemove(k, out _); n++; }
        }
        _log.LogInformation("Đã xóa {N} cache key cho tenant {T}", n, tenant);
        return n;
    }
}
