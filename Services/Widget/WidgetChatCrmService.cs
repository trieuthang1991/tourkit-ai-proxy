using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Widget;

/// <summary>
/// Widget chat KHI có CRM liên kết — gọi TourKit /api/ai/* để lấy dữ liệu thật rồi cho AI phân tích.
///
/// Khác /assistant (ChatAgentService): đơn giản hơn, single-shot, KHÔNG agentic loop, KHÔNG market resolver
/// (widget không xử lý câu cao cấp). Khi planner không chọn được tool whitelist → fallback FAQ thường.
///
/// Tool whitelist là defense-in-depth: ngay cả khi AI trả về tool ngoài list, backend reject ở dispatch.
/// </summary>
public class WidgetChatCrmService
{
    // Mặc định nếu admin chưa cấu hình AllowedTools — chỉ 3 tool READ-ONLY safe cho khách hàng public.
    public static readonly string[] DefaultAllowed = new[] { "tours", "list_markets", "booking_tickets" };

    private const int MAX_HISTORY = 10;
    private const int MAX_TOKENS = 1200;
    private const double TEMP = 0.4;

    private readonly ProviderRegistry _registry;
    private readonly WidgetTokenRepository _repo;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly WidgetChatService _faq;     // fallback nếu không có tool phù hợp
    private readonly AiCallContext _ctx;
    private readonly ILogger<WidgetChatCrmService> _log;

    public WidgetChatCrmService(
        ProviderRegistry registry, WidgetTokenRepository repo,
        TkSessionStore sessions, TourKitApiClient api,
        WidgetChatService faq, AiCallContext ctx,
        ILogger<WidgetChatCrmService> log)
    {
        _registry = registry; _repo = repo; _sessions = sessions; _api = api;
        _faq = faq; _ctx = ctx; _log = log;
    }

    public record ChatResult(string Reply, bool UsedCrm, string? ToolName, long LatencyMs);

    public async Task<ChatResult> ChatStreamAsync(
        WidgetToken token, string message, List<WidgetChatMessage>? history,
        List<string>? images, List<string>? documents,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 1. Resolve whitelist ────────────────────────────────────────────────
        var allowed = ParseAllowedTools(token.AllowedTools);
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

        // ── 2. Plan (AI chọn tool) ──────────────────────────────────────────────
        ChatTool? tool = null;
        JsonElement? plannerParams = null;
        try
        {
            (tool, plannerParams) = await PlanAsync(token, message, history, allowed, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Widget CRM] planner fail — fallback FAQ");
        }

        if (tool == null || !allowedSet.Contains(tool.Name))
        {
            // Không tool phù hợp → bot trả lời FAQ chung (đã có guardrail "không bịa data")
            var fr = await _faq.ChatStreamAsync(token, message, history, images, documents, onDelta, ct);
            return new(fr.Reply, false, null, sw.ElapsedMilliseconds);
        }

        // ── 3. Fetch CRM data ───────────────────────────────────────────────────
        JsonElement data = default;
        string? fetchErr = null;
        try
        {
            var path = ChatTools.BuildPath(tool, plannerParams);
            var jwt = await _sessions.GetValidJwtAsync(token.TourKitSessionId!, ct);
            data = await _api.GetAsync(jwt, path, ct);
        }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            // JWT expire giữa chừng — force re-login + retry 1 lần.
            try
            {
                var jwt = await _sessions.ForceReloginAsync(token.TourKitSessionId!, ct);
                var path = ChatTools.BuildPath(tool, plannerParams);
                data = await _api.GetAsync(jwt, path, ct);
            }
            catch (Exception ex2) { fetchErr = ex2.Message; }
        }
        catch (Exception ex) { fetchErr = ex.Message; }

        if (fetchErr != null)
        {
            _log.LogWarning("[Widget CRM] fetch tool={Tool} fail: {Err}", tool.Name, fetchErr);
            var fr = await _faq.ChatStreamAsync(token, message, history, images, documents, onDelta, ct);
            return new(fr.Reply, false, null, sw.ElapsedMilliseconds);
        }

        // ── 4. Analyze stream ───────────────────────────────────────────────────
        var (system, prompt) = BuildAnalysisPrompt(token, message, history, tool, data);
        var provider = _registry.Resolve(null);

        using var tenantScope = _ctx.Push("widget-crm", token.TenantId);
        var req = new CompleteRequest(
            Prompt: prompt, Provider: provider.Id, Model: null,
            MaxTokens: MAX_TOKENS, Temperature: TEMP, System: system,
            Images: images, Documents: documents);
        var res = await provider.StreamAsync(req, onDelta, ct);

        _ = _repo.IncrementMessagesAsync(token.Token, CancellationToken.None);
        return new(res.Text, true, tool.Name, sw.ElapsedMilliseconds);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    public static List<string> ParseAllowedTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultAllowed.ToList();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list == null || list.Count == 0) return DefaultAllowed.ToList();
            return list;
        }
        catch { return DefaultAllowed.ToList(); }
    }

    private async Task<(ChatTool? tool, JsonElement? prms)> PlanAsync(
        WidgetToken token, string message, List<WidgetChatMessage>? history,
        List<string> allowed, CancellationToken ct)
    {
        // Filter catalog xuống chỉ những tool được phép — AI không thấy tool cấm.
        var allowedTools = ChatTools.All.Where(t => allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (allowedTools.Count == 0) return (null, null);

        var catalog = new StringBuilder();
        foreach (var t in allowedTools)
        {
            var ps = t.Params.Length == 0 ? "(không tham số)" : string.Join(", ", t.Params);
            catalog.Append("- ").Append(t.Name).Append(": ").Append(t.Description)
                   .Append(" | params: ").Append(ps).Append('\n');
        }

        var sys = "Bạn là planner chọn API CRM cho câu hỏi của khách. " +
                  "TRẢ VỀ ĐÚNG 1 JSON {\"tool\":\"<name>\",\"params\":{...}}. " +
                  "Nếu KHÔNG có API phù hợp (câu hỏi ngoài data CRM, vd thời tiết, chào hỏi, giải thích chung) → " +
                  "TRẢ VỀ {\"tool\":\"none\"}. KHÔNG giải thích.";

        var convo = new StringBuilder();
        if (history != null)
            foreach (var m in history.TakeLast(MAX_HISTORY))
                convo.AppendLine($"{(m.Role == "assistant" ? "Bot" : "Khách")}: {m.Content?.Trim()}");
        convo.AppendLine($"Khách: {message.Trim()}");

        var prompt = $"DANH SÁCH API:\n{catalog}\n\nHỘI THOẠI:\n{convo}\nChọn API phù hợp NHẤT:";

        var provider = _registry.Resolve(null);
        using var tenantScope = _ctx.Push("widget-crm-plan", token.TenantId);
        var req = new CompleteRequest(prompt, provider.Id, null, 400, 0.1, sys);
        var res = await provider.CompleteAsync(req, ct);

        var obj = LooseJson.ExtractFirstObject(res.Text);
        if (obj == null) return (null, null);
        using var doc = JsonDocument.Parse(obj);
        var root = doc.RootElement;
        var toolName = root.TryGetProperty("tool", out var t1) && t1.ValueKind == JsonValueKind.String ? t1.GetString() : null;
        if (string.IsNullOrEmpty(toolName) || string.Equals(toolName, "none", StringComparison.OrdinalIgnoreCase))
            return (null, null);
        var tool = ChatTools.Find(toolName);
        JsonElement? prms = root.TryGetProperty("params", out var p) ? p.Clone() : null;
        return (tool, prms);
    }

    private (string system, string prompt) BuildAnalysisPrompt(
        WidgetToken token, string message, List<WidgetChatMessage>? history,
        ChatTool tool, JsonElement data)
    {
        var sys = new StringBuilder();
        sys.Append(token.SystemPrompt.Trim());
        sys.Append("\n\nQUY TẮC TRẢ LỜI:\n");
        sys.Append("- Trả lời tiếng Việt, gọi khách \"Anh/Chị\", lịch sự, không quá 5-6 câu.\n");
        sys.Append("- Dùng đúng số liệu trong DỮ LIỆU CRM bên dưới — KHÔNG bịa, KHÔNG nội suy.\n");
        sys.Append("- Nếu danh sách dài, kể tối đa 3-5 mục đại diện rồi mời khách để lại thông tin để tư vấn sâu hơn.\n");
        sys.Append("- Đề cập tên tour / mã đặt cụ thể nếu có trong data.\n");
        sys.Append("- Trình bày dễ đọc: TÁCH ĐOẠN bằng dòng trống (\\n\\n), dùng \"-\" đầu dòng cho danh sách. KHÔNG markdown (**, ##). KHÔNG dồn 1 khối dính nhau.\n");

        var convo = new StringBuilder();
        if (history != null)
            foreach (var m in history.TakeLast(MAX_HISTORY))
                convo.AppendLine($"{(m.Role == "assistant" ? "Bot" : "Khách")}: {m.Content?.Trim()}");

        // Compact data summary để prompt không quá dài (giới hạn 4000 chars).
        var summary = CompactJson(data, maxChars: 4000);

        var prompt = new StringBuilder();
        prompt.AppendLine($"DỮ LIỆU CRM (từ {tool.Title}):");
        prompt.AppendLine(summary);
        prompt.AppendLine();
        prompt.AppendLine("HỘI THOẠI:");
        prompt.Append(convo);
        prompt.AppendLine($"Khách: {message.Trim()}");
        prompt.Append("Bot:");
        return (sys.ToString(), prompt.ToString());
    }

    private static string CompactJson(JsonElement el, int maxChars)
    {
        var json = el.ValueKind == JsonValueKind.Undefined ? "{}" :
                   JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = false });
        if (json.Length <= maxChars) return json;

        // Truncate trên items array nếu có (giữ structure {title, total, summary, items[0..n]})
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var keep = new List<JsonElement>();
            int approxLen = 0;
            foreach (var it in items.EnumerateArray())
            {
                var s = it.GetRawText();
                if (approxLen + s.Length > maxChars - 500) break;     // chừa 500 cho metadata wrapper
                keep.Add(it);
                approxLen += s.Length;
            }
            var copy = new Dictionary<string, object?>();
            foreach (var p in el.EnumerateObject())
                if (p.Name != "items") copy[p.Name] = JsonSerializer.Deserialize<object?>(p.Value.GetRawText());
            copy["items"] = keep.Select(it => JsonSerializer.Deserialize<object?>(it.GetRawText())).ToList();
            copy["_truncated"] = $"showing {keep.Count}/{items.GetArrayLength()} items";
            return JsonSerializer.Serialize(copy);
        }
        return json.Substring(0, maxChars) + "...[truncated]";
    }
}
