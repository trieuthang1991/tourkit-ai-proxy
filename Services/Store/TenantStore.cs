using System.Text.Json;
using StackExchange.Redis;
using TourkitAiProxy.Services.Cache;

namespace TourkitAiProxy.Services.Store;

/// Store BỀN VỮNG theo tenant — KHÁC ChatCache (cache có TTL). Dùng cho dữ liệu cần giữ lâu:
/// nháp tour, review. Redis nếu có (`tkai:{collection}:{tenant}` hash, field=id) — chia sẻ + sống
/// qua restart; không thì file-backed `data/{collection}.json` (`{tenant:{id:obj}}`). KHÔNG in-memory.
public class TenantStore
{
    private readonly RedisProvider _redis;
    private readonly string _dataDir;
    private readonly ILogger<TenantStore> _log;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TenantStore(RedisProvider redis, IWebHostEnvironment env, ILogger<TenantStore> log)
    {
        _redis = redis; _log = log;
        _dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(_dataDir);
    }

    private string RedisKey(string collection, string tenant) => $"tkai:{collection}:{tenant}";
    private string FilePath(string collection) => Path.Combine(_dataDir, $"{collection}.json");

    public void Set<T>(string collection, string tenant, string id, T value)
    {
        var json = JsonSerializer.Serialize(value, _json);
        if (_redis.Available)
        {
            _redis.Db!.HashSet(RedisKey(collection, tenant), id, json);
            return;
        }
        lock (_fileLock)
        {
            var all = LoadFile(collection);
            if (!all.TryGetValue(tenant, out var byId)) { byId = new(); all[tenant] = byId; }
            byId[id] = JsonSerializer.Deserialize<JsonElement>(json);
            SaveFile(collection, all);
        }
    }

    public T? Get<T>(string collection, string tenant, string id)
    {
        if (_redis.Available)
        {
            var v = _redis.Db!.HashGet(RedisKey(collection, tenant), id);
            return v.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(v!, _json);
        }
        lock (_fileLock)
        {
            var all = LoadFile(collection);
            if (all.TryGetValue(tenant, out var byId) && byId.TryGetValue(id, out var el))
                return el.Deserialize<T>(_json);
            return default;
        }
    }

    public bool Has(string collection, string tenant, string id)
    {
        if (_redis.Available) return _redis.Db!.HashExists(RedisKey(collection, tenant), id);
        lock (_fileLock)
        {
            var all = LoadFile(collection);
            return all.TryGetValue(tenant, out var byId) && byId.ContainsKey(id);
        }
    }

    public List<T> List<T>(string collection, string tenant)
    {
        var result = new List<T>();
        if (_redis.Available)
        {
            foreach (var e in _redis.Db!.HashGetAll(RedisKey(collection, tenant)))
            {
                var v = JsonSerializer.Deserialize<T>(e.Value!, _json);
                if (v != null) result.Add(v);
            }
            return result;
        }
        lock (_fileLock)
        {
            var all = LoadFile(collection);
            if (all.TryGetValue(tenant, out var byId))
                foreach (var el in byId.Values)
                {
                    var v = el.Deserialize<T>(_json);
                    if (v != null) result.Add(v);
                }
            return result;
        }
    }

    public bool Delete(string collection, string tenant, string id)
    {
        if (_redis.Available) return _redis.Db!.HashDelete(RedisKey(collection, tenant), id);
        lock (_fileLock)
        {
            var all = LoadFile(collection);
            if (all.TryGetValue(tenant, out var byId) && byId.Remove(id)) { SaveFile(collection, all); return true; }
            return false;
        }
    }

    // ─── File fallback ───────────────────────────────────────────────────────────
    private Dictionary<string, Dictionary<string, JsonElement>> LoadFile(string collection)
    {
        var path = FilePath(collection);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) { _log.LogError(ex, "Đọc {File} lỗi", path); return new(); }
    }

    private void SaveFile(string collection, Dictionary<string, Dictionary<string, JsonElement>> all)
    {
        try { File.WriteAllText(FilePath(collection), JsonSerializer.Serialize(all, _json)); }
        catch (Exception ex) { _log.LogError(ex, "Ghi {Coll} lỗi", collection); }
    }
}
