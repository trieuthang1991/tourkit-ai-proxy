// Services/Cache/RedisStore.cs
using StackExchange.Redis;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Cache;

/// <summary>
/// Generic Redis store dùng chung cho mọi feature cần persist nhẹ (board snapshot, fallback,
/// rate-limit counter, lock, ...) — KHÔNG bind vào use case cụ thể như ChatCache.
///
/// Đọc Redis:ConnectionString từ config (giải mã ENC: tự động giống ChatCache).
/// Key prefix `tkai:` để không đụng key TourKit chia sẻ Redis.
/// Tất cả method an toàn khi Redis null (return null/false) → caller tự fallback.
/// </summary>
public class RedisStore
{
    private const string Prefix = "tkai:";
    private readonly IDatabase? _db;
    private readonly ILogger<RedisStore> _log;

    public bool Available => _db != null;
    public string Backend { get; }

    public RedisStore(IConfiguration cfg, ILogger<RedisStore> log)
    {
        _log = log;
        var conn = cfg["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(conn) && conn.StartsWith("ENC:"))
            conn = Crypton.Decrypt(conn.Substring(4));

        if (string.IsNullOrWhiteSpace(conn))
        {
            Backend = "disabled (no Redis configured)";
        }
        else
        {
            try
            {
                var options = ConfigurationOptions.Parse(conn);
                options.AbortOnConnectFail = false;
                var mux = ConnectionMultiplexer.Connect(options);
                _db = mux.GetDatabase();
                Backend = $"Redis ({mux.GetEndPoints().FirstOrDefault()}, connected={mux.IsConnected})";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Kết nối Redis thất bại → RedisStore disabled");
                Backend = "disabled (connect failed)";
            }
        }
        _log.LogInformation("RedisStore backend: {Backend}", Backend);
    }

    /// Get raw string. Return null nếu Redis null / key không có / lỗi.
    public string? Get(string key)
    {
        if (_db == null) return null;
        try
        {
            var v = _db.StringGet(Prefix + key);
            return v.IsNullOrEmpty ? null : v.ToString();
        }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] Get {Key} lỗi", key); return null; }
    }

    /// Set raw string. expiry null = không expire (persist).
    public bool Set(string key, string value, TimeSpan? expiry = null)
    {
        if (_db == null) return false;
        try
        {
            return expiry.HasValue
                ? _db.StringSet(Prefix + key, value, expiry.Value)
                : _db.StringSet(Prefix + key, value);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] Set {Key} lỗi", key); return false; }
    }

    /// Delete key.
    public bool Delete(string key)
    {
        if (_db == null) return false;
        try { return _db.KeyDelete(Prefix + key); }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] Delete {Key} lỗi", key); return false; }
    }

    /// HGET cho composite key — useful khi cần lookup nhiều entity cùng tenant.
    public string? HashGet(string hashKey, string field)
    {
        if (_db == null) return null;
        try
        {
            var v = _db.HashGet(Prefix + hashKey, field);
            return v.IsNullOrEmpty ? null : v.ToString();
        }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] HashGet {K}/{F} lỗi", hashKey, field); return null; }
    }

    /// HSET. Multi-field write nếu cần dùng `_db.HashSetAsync` với entries.
    public bool HashSet(string hashKey, string field, string value)
    {
        if (_db == null) return false;
        try { _db.HashSet(Prefix + hashKey, field, value); return true; }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] HashSet {K}/{F} lỗi", hashKey, field); return false; }
    }

    public bool HashDelete(string hashKey, string field)
    {
        if (_db == null) return false;
        try { return _db.HashDelete(Prefix + hashKey, field); }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] HashDel {K}/{F} lỗi", hashKey, field); return false; }
    }

    /// <summary>
    /// Atomic SET NX + EX (distributed lock primitive). Trả true nếu KEY vừa được tạo (caller thắng lock).
    /// Trả false nếu KEY đã tồn tại (instance khác đang giữ) hoặc Redis lỗi/tắt.
    ///
    /// <para><b>Fail-closed pattern</b>: khi Redis xuống → trả false → caller SKIP hành động. An toàn cho
    /// workflow chống double-run cross-instance (chấp nhận bỏ 1-2 chu kỳ khi Redis xuống ngắn hạn).</para>
    /// </summary>
    public bool SetIfNotExists(string key, string value, TimeSpan expiry)
    {
        if (_db == null) return false;
        try { return _db.StringSet(Prefix + key, value, expiry, When.NotExists); }
        catch (Exception ex) { _log.LogWarning(ex, "[redis] SetNX {Key} lỗi", key); return false; }
    }
}
