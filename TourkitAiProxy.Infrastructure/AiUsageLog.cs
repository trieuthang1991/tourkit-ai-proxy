using System.Text.Json;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Services;

/// Log per-request AI calls: time, feature, sessionId, tenant, model, tokens, latency, cost.
/// Append JSONL vào data/ai-usage.jsonl (giữ tối đa 10k dòng gần nhất, rotate).
/// Cost ước tính theo PriceTable (USD/Mtok) × tỉ giá USD→VND.
/// Mục đích: trả lời "ai/feature nào tiêu bao nhiêu hôm nay" thay vì counter mù.
public class AiUsageLog
{
    private const int MaxRows = 10_000;
    private const double UsdToVnd = 25_000.0;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<AiUsageLog> _log;
    private readonly IServiceProvider _sp;   // lazy resolve AiUsageHistoryRepository — không circular DI

    // Giá USD/Mtok (input/output). Cập nhật theo giá list 2026. Model không khớp → fallback default.
    private static readonly Dictionary<string, (double In, double Out)> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI
        ["gpt-4o-mini"]   = (0.15, 0.60),
        ["gpt-4o"]        = (2.50, 10.00),
        ["gpt-4.1-mini"]  = (0.40, 1.60),
        ["gpt-4.1"]       = (2.00, 8.00),
        // Anthropic
        ["claude-3-5-haiku-latest"] = (0.80, 4.00),
        ["claude-3-5-sonnet-latest"] = (3.00, 15.00),
        ["claude-sonnet-4-5"]       = (3.00, 15.00),
        ["claude-3-opus-latest"]    = (15.00, 75.00),
        ["claude-opus-4-1"]         = (15.00, 75.00),
        // OpenCode (DeepSeek V4 family — giá xấp xỉ retail)
        ["deepseek-v4-flash"] = (0.27, 1.10),
        ["deepseek-v4-pro"]   = (0.27, 1.10),
        ["minimax-m2.5"]      = (0.27, 1.10),
        ["minimax-m2.7"]      = (0.27, 1.10),
    };
    private static readonly (double In, double Out) DefaultPrice = (0.50, 1.50);

    public AiUsageLog(IWebHostEnvironment env, IServiceProvider sp, ILogger<AiUsageLog> log)
    {
        _log = log; _sp = sp;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ai-usage.jsonl");

        // One-shot migration: file legacy → SQL (dedup theo MAX(Ts)) → rename .migrated.
        // Idempotent: file đã .migrated thì no-op. Nếu user deploy bản mới kèm file → import phần Ts mới.
        _ = Task.Run(MigrateLegacyAsync);
    }

    private async Task MigrateLegacyAsync()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var repo = _sp.GetService(typeof(AiUsageHistoryRepository)) as AiUsageHistoryRepository;
            if (repo == null) { _log.LogWarning("[AiUsageLog] Repo chưa register — skip migration"); return; }

            // Đọc file lock-safe (Append cũng dùng cùng _lock).
            string[] lines;
            lock (_lock) { lines = File.ReadAllLines(_path); }
            if (lines.Length == 0) { TryRenameMigrated(); return; }

            var maxTs = await repo.GetMaxTsAsync();
            var cutoff = maxTs ?? DateTime.MinValue;

            var entries = new List<Entry>(capacity: lines.Length);
            foreach (var ln in lines)
            {
                if (string.IsNullOrWhiteSpace(ln)) continue;
                Entry? e;
                try { e = JsonSerializer.Deserialize<Entry>(ln); } catch { continue; }
                if (e == null) continue;
                if (!DateTime.TryParse(e.Timestamp, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var ts)) continue;
                if (ts <= cutoff) continue;
                entries.Add(e);
            }

            if (entries.Count == 0)
            {
                _log.LogInformation("[AiUsageLog] File {N} dòng nhưng tất cả Ts ≤ MAX(Ts) trong SQL — skip, rename .migrated", lines.Length);
                TryRenameMigrated();
                return;
            }

            var n = await repo.BulkInsertAsync(entries);
            _log.LogInformation("[AiUsageLog] Migrated {N}/{Total} rows từ ai-usage.jsonl vào dbo.AiUsageHistory (cutoff Ts > {Cutoff:o})",
                n, lines.Length, cutoff);
            if (n > 0) TryRenameMigrated();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AiUsageLog] Migrate ai-usage.jsonl → SQL lỗi — giữ file để retry");
        }
    }

    private void TryRenameMigrated()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_path)) File.Move(_path, _path + ".migrated", overwrite: true);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[AiUsageLog] Rename .migrated lỗi"); }
    }

    public record Entry(
        [property: JsonPropertyName("ts")]        string Timestamp,    // ISO-8601 UTC
        [property: JsonPropertyName("feature")]   string Feature,       // visa / deals / chat / mail / tour-builder / completions / reviews / unknown
        [property: JsonPropertyName("session")]   string? SessionId,    // 8 char prefix (privacy)
        [property: JsonPropertyName("tenant")]    string? Tenant,
        [property: JsonPropertyName("provider")]  string Provider,
        [property: JsonPropertyName("model")]     string Model,
        [property: JsonPropertyName("inTok")]     int InputTokens,
        [property: JsonPropertyName("outTok")]    int OutputTokens,
        [property: JsonPropertyName("latencyMs")] long LatencyMs,
        [property: JsonPropertyName("costVnd")]   long CostVnd,
        [property: JsonPropertyName("cached")]    bool Cached,
        [property: JsonPropertyName("status")]    string Status);

    /// Ước cost VND theo model + tokens.
    public static long EstimateCostVnd(string model, int inTok, int outTok)
    {
        var (pi, po) = Prices.TryGetValue(model ?? "", out var p) ? p : DefaultPrice;
        var usd = (inTok * pi + outTok * po) / 1_000_000.0;
        return (long)Math.Round(usd * UsdToVnd);
    }

    public void Append(string feature, string? sessionId, string? tenant, string provider, string model,
        int inTok, int outTok, long latencyMs, bool cached = false, string status = "ok")
    {
        var cost = cached ? 0 : EstimateCostVnd(model ?? "", inTok, outTok);
        var entry = new Entry(
            DateTime.UtcNow.ToString("o"), feature ?? "unknown",
            string.IsNullOrEmpty(sessionId) ? null : sessionId[..Math.Min(8, sessionId.Length)],
            tenant, provider, model ?? "?", inTok, outTok, latencyMs, cost, cached, status);

        // SQL: persist cross-restart. Fire-and-forget — không block hot path, không throw.
        if (_sp.GetService(typeof(AiUsageHistoryRepository)) is AiUsageHistoryRepository repo)
            _ = repo.AppendAsync(entry);

        // File: kept as human-readable backup (xem nhanh bằng `tail -f`).
        // Sau khi đã .migrated, file mới sẽ append lại — chỉ giữ entries giữa 2 lần migrate.
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n");
                MaybeRotate();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Append ai-usage.jsonl lỗi"); }
    }

    // Đọc N dòng cuối — đủ cho dashboard. SQL ưu tiên (durable); fallback file nếu SQL fail / rỗng.
    public List<Entry> Read(int max = 1000)
    {
        // SQL path: durable across deploys. Fire blocking await — endpoint là cold path không bị ảnh hưởng.
        if (_sp.GetService(typeof(AiUsageHistoryRepository)) is AiUsageHistoryRepository repo)
        {
            try
            {
                var rows = repo.ReadRecentAsync(max).GetAwaiter().GetResult();
                if (rows.Count > 0) return rows;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Read dbo.AiUsageHistory lỗi — fallback file"); }
        }

        // Fallback: file. Dùng khi DB tạm lỗi hoặc bảng rỗng.
        var list = new List<Entry>();
        if (!File.Exists(_path)) return list;
        lock (_lock)
        {
            try
            {
                var lines = File.ReadAllLines(_path);
                var start = Math.Max(0, lines.Length - max);
                for (int i = start; i < lines.Length; i++)
                {
                    var ln = lines[i];
                    if (string.IsNullOrWhiteSpace(ln)) continue;
                    try { var e = JsonSerializer.Deserialize<Entry>(ln); if (e != null) list.Add(e); } catch { }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Read ai-usage.jsonl lỗi"); }
        }
        return list;
    }

    private void MaybeRotate()
    {
        try
        {
            var info = new FileInfo(_path);
            if (info.Length < 5_000_000) return;   // < 5MB → bỏ qua
            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MaxRows) return;
            File.WriteAllLines(_path, lines.Skip(lines.Length - MaxRows));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Rotate ai-usage.jsonl lỗi"); }
    }
}
