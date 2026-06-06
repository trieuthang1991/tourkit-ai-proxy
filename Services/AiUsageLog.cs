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

    public AiUsageLog(IWebHostEnvironment env, ILogger<AiUsageLog> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ai-usage.jsonl");
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

    // Đọc N dòng cuối — đủ cho dashboard. Toàn bộ file giữ ≤MaxRows.
    public List<Entry> Read(int max = 1000)
    {
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
