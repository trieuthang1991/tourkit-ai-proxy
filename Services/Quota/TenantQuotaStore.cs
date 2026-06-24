using System.Collections.Concurrent;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Quota AI 1000 lượt/tenant. KIẾN TRÚC TỐI ƯU CHO HOT PATH:
///
///   • In-memory <see cref="ConcurrentDictionary"/> = SOURCE OF TRUTH lúc runtime (đọc/ghi nano-giây).
///   • SQL <c>dbo.TenantQuota</c> = persistence (durability + cross-instance convergence).
///   • Mọi mutation trên hot path (<see cref="Consume"/>) CHỈ động in-mem + tích lũy delta;
///     <see cref="QuotaFlushService"/> background flush mỗi 5s thực hiện 1 UPDATE per tenant
///     với delta tổng (thay vì N UPDATE per AI call).
///   • <see cref="TopUp"/> (admin, hiếm) vẫn sync SQL — chấp nhận latency vì không phải hot path.
///   • Trên crash → mất tối đa delta của 5s gần nhất (acceptable cho "tương đối" như user xác nhận).
///
/// Operations:
///   • Snapshot(tenant)    — in-mem peek, 0 SQL roundtrip.
///   • IsAvailable(tenant) — in-mem read, 0 SQL roundtrip.
///   • Consume(tenant)     — atomic in-mem update + tích lũy delta. Return snapshot mới.
///   • TopUp(tenant, add)  — sync SQL UPDATE [Limit] += add (admin op, hiếm).
///   • ListAll()           — in-mem snapshot list (admin dashboard).
///   • FlushPendingAsync() — background gọi mỗi 5s: drain pendingDeltas → 1 UPDATE per tenant.
/// </summary>
public class TenantQuotaStore
{
    public const int DefaultLimit = 1000;
    public const int WarnPercent  = 90;

    // Source of truth in-mem. Key = TenantId.
    private readonly ConcurrentDictionary<string, QuotaState> _map = new(StringComparer.Ordinal);

    // Delta tích lũy chưa flush xuống SQL. Background service drain mỗi 5s.
    // Key = TenantId, Value = số lượt consume PENDING (chưa ghi SQL).
    private readonly ConcurrentDictionary<string, int> _pendingDeltas = new(StringComparer.Ordinal);

    private readonly TenantQuotaRepository _repo;
    private readonly ILogger<TenantQuotaStore> _log;

    public string Backend { get; }

    public TenantQuotaStore(
        IWebHostEnvironment env,
        TenantQuotaRepository repo,
        ILogger<TenantQuotaStore> log)
    {
        _repo = repo; _log = log;

        // Startup load: 1 SELECT toàn bảng → in-mem. Đồng bộ vì chạy đúng 1 lần.
        try
        {
            var all = _repo.LoadAllAsync().GetAwaiter().GetResult();
            foreach (var kv in all) _map[kv.Key] = kv.Value;
            _log.LogInformation("[TenantQuotaStore] Loaded {N} tenants từ SQL vào cache", all.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantQuotaStore] Load SQL fail — cache rỗng, sẽ seed lazy");
        }

        // One-shot migration: file legacy → SQL → rename .migrated.
        TryMigrateLegacyFile(env);

        Backend = $"SQL dbo.TenantQuota (in-mem cache {_map.Count} tenants, batched flush every 5s)";
        _log.LogInformation("TenantQuotaStore backend: {Backend}", Backend);
    }

    /// Peek state. Tenant mới → seed in-mem mặc định (NO SQL — flush sẽ tạo row sau).
    public QuotaSnapshot Snapshot(string tenant)
    {
        var state = _map.GetOrAdd(tenant, _ => new QuotaState(DefaultLimit, 0, DateTime.UtcNow));
        return ToSnapshot(tenant, state);
    }

    /// Hot path read — in-mem only.
    public bool IsAvailable(string tenant, int needed = 1)
    {
        var state = _map.GetOrAdd(tenant, _ => new QuotaState(DefaultLimit, 0, DateTime.UtcNow));
        return state.Used + needed <= state.Limit;
    }

    /// Hot path write — in-mem atomic + delta tích lũy. KHÔNG await SQL.
    /// Background <see cref="QuotaFlushService"/> sẽ flush delta định kỳ (5s).
    public QuotaSnapshot Consume(string tenant, int count = 1)
    {
        var state = _map.AddOrUpdate(tenant,
            _ => new QuotaState(DefaultLimit, count, DateTime.UtcNow),
            (_, old) => old with { Used = old.Used + count, UpdatedAt = DateTime.UtcNow });

        // Tích lũy delta atomic. AddOrUpdate(tenant, count, (k, old) => old + count) — race-safe.
        _pendingDeltas.AddOrUpdate(tenant, count, (_, old) => old + count);

        return ToSnapshot(tenant, state);
    }

    /// Admin: tăng [Limit]. Sync SQL vì là admin op hiếm — chấp nhận ~10ms latency cho audit chắc chắn.
    public QuotaSnapshot TopUp(string tenant, int add)
    {
        if (add <= 0) throw new ArgumentOutOfRangeException(nameof(add), "Số lượt cộng phải > 0");

        // Flush pending delta của tenant này TRƯỚC khi TopUp để SQL có Used chính xác.
        // Nếu skip step này, SQL sẽ trả Limit mới + Used cũ (chưa cộng delta) → in-mem bị overwrite stale.
        if (_pendingDeltas.TryRemove(tenant, out var delta) && delta > 0)
        {
            try { _ = _repo.ConsumeAsync(tenant, delta, DefaultLimit).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TopUp pre-flush tenant {T} delta {D} lỗi — re-queue", tenant, delta);
                _pendingDeltas.AddOrUpdate(tenant, delta, (_, old) => old + delta);
            }
        }

        var state = _repo.TopUpAsync(tenant, add, DefaultLimit).GetAwaiter().GetResult();
        _map[tenant] = state;
        _log.LogInformation("Quota topup tenant {T} +{Add} → limit={L} used={U}", tenant, add, state.Limit, state.Used);
        return ToSnapshot(tenant, state);
    }

    /// Admin: liệt kê toàn bộ tenant (in-mem snapshot — không SQL).
    public List<QuotaSnapshot> ListAll()
        => _map.ToArray()
            .Select(kv => ToSnapshot(kv.Key, kv.Value))
            .OrderByDescending(s => s.Used)
            .ToList();

    /// Gọi bởi <see cref="QuotaFlushService"/> mỗi 5s + lúc shutdown.
    /// Drain pendingDeltas → 1 UPDATE per tenant với delta tổng. Reconcile in-mem theo SQL response.
    public async Task FlushPendingAsync(CancellationToken ct = default)
    {
        if (_pendingDeltas.IsEmpty) return;

        // Snapshot keys hiện tại; mỗi key xử lý atomic riêng.
        var keys = _pendingDeltas.Keys.ToArray();
        foreach (var tenant in keys)
        {
            // Atomic take: xóa entry và lấy giá trị. Nếu race với Consume mới, delta sẽ ở batch sau.
            if (!_pendingDeltas.TryRemove(tenant, out var delta) || delta <= 0) continue;

            try
            {
                var state = await _repo.ConsumeAsync(tenant, delta, DefaultLimit, ct);
                // Reconcile: chỉ overwrite Used+UpdatedAt từ SQL (không đụng Limit để tránh đè TopUp đang chạy).
                _map.AddOrUpdate(tenant,
                    _ => state,
                    (_, old) => old with { Used = state.Used, UpdatedAt = state.UpdatedAt });
            }
            catch (Exception ex)
            {
                // Re-queue delta để retry batch sau (cộng vào delta hiện hành nếu có).
                _pendingDeltas.AddOrUpdate(tenant, delta, (_, old) => old + delta);
                _log.LogWarning(ex, "[TenantQuotaStore] Flush tenant {T} delta {D} fail — re-queue", tenant, delta);
            }
        }
    }

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

    /// One-shot migration file legacy → SQL → rename. Idempotent.
    private void TryMigrateLegacyFile(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "data", "tenant-quota.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, QuotaState>>(json);
            if (legacy == null || legacy.Count == 0)
            {
                File.Move(path, path + ".migrated", overwrite: true);
                return;
            }

            int ok = 0, skip = 0;
            foreach (var kv in legacy)
            {
                if (_map.ContainsKey(kv.Key)) { skip++; continue; }
                try
                {
                    _repo.UpsertAsync(kv.Key, kv.Value).GetAwaiter().GetResult();
                    _map[kv.Key] = kv.Value;
                    ok++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TenantQuotaStore] Migrate tenant {T} fail", kv.Key);
                    skip++;
                }
            }
            File.Move(path, path + ".migrated", overwrite: true);
            _log.LogInformation("[TenantQuotaStore] Migrated {Ok} tenants từ file legacy vào SQL (skip {Skip}), file → .migrated", ok, skip);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TenantQuotaStore] Migrate file legacy lỗi — giữ file nguyên để retry");
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
