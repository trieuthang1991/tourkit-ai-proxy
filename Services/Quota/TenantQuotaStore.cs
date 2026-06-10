using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Quota AI 1000 lượt/tenant (lĩnh 1 lần, không tự reset). Storage:
///   • In-memory ConcurrentDictionary làm source of truth (đọc/ghi nhanh, atomic).
///   • File data/tenant-quota.json — load lúc startup, ghi sau mỗi thay đổi (durability).
///   • Redis (nếu config) — mirror write best-effort cho cross-instance + survive file mất.
///
/// Operations:
///   • Snapshot(tenant)   — peek không thay đổi state, tự seed quota mặc định nếu chưa có.
///   • IsAvailable(tenant) — check còn lượt không (không consume).
///   • Consume(tenant)    — tăng used +1 atomic. Trả snapshot mới.
///   • TopUp(tenant, add) — admin: limit += add. Trả snapshot mới.
///   • ListAll()          — admin: liệt kê toàn bộ tenant.
/// </summary>
public class TenantQuotaStore
{
    public const int DefaultLimit = 1000;
    public const int WarnPercent  = 90;       // >=90% used → cảnh báo
    private const string RedisPrefix = "tkai:quota:";

    private readonly ConcurrentDictionary<string, QuotaState> _map = new();
    private readonly object _fileLock = new();
    private readonly string _filePath;
    private readonly IDatabase? _redis;
    private readonly ILogger<TenantQuotaStore> _log;

    public string Backend { get; }

    public TenantQuotaStore(IWebHostEnvironment env, IConfiguration cfg, ILogger<TenantQuotaStore> log)
    {
        _log = log;

        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "tenant-quota.json");

        // Load file → in-mem.
        LoadFromFile();

        // Try Redis (best-effort, mirror writes only).
        var conn = cfg["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(conn) && conn.StartsWith("ENC:"))
            conn = Crypton.Decrypt(conn.Substring(4));

        if (!string.IsNullOrWhiteSpace(conn))
        {
            try
            {
                var options = ConfigurationOptions.Parse(conn);
                options.AbortOnConnectFail = false;
                var mux = ConnectionMultiplexer.Connect(options);
                _redis = mux.GetDatabase();
                Backend = $"File + Redis ({_map.Count} tenants loaded)";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota: Redis connect fail → file-only");
                Backend = $"File only ({_map.Count} tenants loaded, Redis fail)";
            }
        }
        else
        {
            Backend = $"File only ({_map.Count} tenants loaded, no Redis config)";
        }

        _log.LogInformation("TenantQuotaStore backend: {Backend}", Backend);
    }

    /// Peek state hiện tại (seed mặc định nếu tenant mới). KHÔNG consume.
    public QuotaSnapshot Snapshot(string tenant)
    {
        var state = _map.GetOrAdd(tenant, _ => new QuotaState(DefaultLimit, 0, DateTime.UtcNow));
        return ToSnapshot(tenant, state);
    }

    /// Còn lượt không (đã trừ `needed`)?
    public bool IsAvailable(string tenant, int needed = 1)
    {
        var state = _map.GetOrAdd(tenant, _ => new QuotaState(DefaultLimit, 0, DateTime.UtcNow));
        return state.Used + needed <= state.Limit;
    }

    /// Atomic: tăng used += 1. Trả snapshot mới. KHÔNG check exhausted ở đây — caller check trước bằng IsAvailable.
    /// Lý do tách check ↔ consume: provider gọi check ở đầu (chặn 429), consume ở cuối (chỉ khi gọi thành công).
    public QuotaSnapshot Consume(string tenant, int count = 1)
    {
        var state = _map.AddOrUpdate(tenant,
            _ => new QuotaState(DefaultLimit, count, DateTime.UtcNow),
            (_, old) => old with { Used = old.Used + count, UpdatedAt = DateTime.UtcNow });
        Persist(tenant, state);
        return ToSnapshot(tenant, state);
    }

    /// Admin: tăng limit của tenant (cấp thêm lượt). Trả snapshot mới.
    public QuotaSnapshot TopUp(string tenant, int add)
    {
        if (add <= 0) throw new ArgumentOutOfRangeException(nameof(add), "Số lượt cộng phải > 0");
        var state = _map.AddOrUpdate(tenant,
            _ => new QuotaState(DefaultLimit + add, 0, DateTime.UtcNow),
            (_, old) => old with { Limit = old.Limit + add, UpdatedAt = DateTime.UtcNow });
        Persist(tenant, state);
        _log.LogInformation("Quota topup tenant {T} +{Add} → limit={L} used={U}", tenant, add, state.Limit, state.Used);
        return ToSnapshot(tenant, state);
    }

    /// Admin: liệt kê toàn bộ tenant đã track (cho dashboard giám sát).
    public List<QuotaSnapshot> ListAll()
        => _map.ToArray()
            .Select(kv => ToSnapshot(kv.Key, kv.Value))
            .OrderByDescending(s => s.Used)
            .ToList();

    // ─── helpers ───────────────────────────────────────────────────────────────
    private static QuotaSnapshot ToSnapshot(string tenant, QuotaState s)
    {
        var remaining = Math.Max(0, s.Limit - s.Used);
        var pct = s.Limit > 0 ? (int)Math.Round(100.0 * s.Used / s.Limit) : 0;
        return new QuotaSnapshot(
            Tenant: tenant, Limit: s.Limit, Used: s.Used, Remaining: remaining,
            UsedPct: pct, Warn: pct >= WarnPercent, Exhausted: s.Used >= s.Limit,
            UpdatedAt: s.UpdatedAt);
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, QuotaState>>(json);
            if (data == null) return;
            foreach (var kv in data) _map[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Load tenant-quota.json fail — start empty"); }
    }

    private void Persist(string tenant, QuotaState state)
    {
        // 1. File (durability)
        lock (_fileLock)
        {
            try
            {
                var snapshot = _map.ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { _log.LogWarning(ex, "Persist tenant-quota.json fail (tenant {T})", tenant); }
        }

        // 2. Redis (mirror, cross-instance visibility)
        if (_redis != null)
        {
            try { _redis.StringSet(RedisPrefix + tenant, JsonSerializer.Serialize(state)); }
            catch (Exception ex) { _log.LogWarning(ex, "Redis quota mirror fail (tenant {T})", tenant); }
        }
    }
}

/// Ném khi tenant hết quota — endpoint bắt → trả 429.
public class QuotaExhaustedException : Exception
{
    public string Tenant { get; }
    public int Limit { get; }
    public int Used { get; }
    public QuotaExhaustedException(string tenant, int limit, int used)
        : base($"Tenant '{tenant}' đã dùng {used}/{limit} lượt AI — hết quota.")
    {
        Tenant = tenant; Limit = limit; Used = used;
    }
}
