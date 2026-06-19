using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Deals;

/// AI chấm khả năng THẮNG DEAL của 1 cơ hội từ HỒ SƠ TEXT (detail + hành động Sale).
///
/// Dual-path scoring (mirror VisaScoringService):
/// - Provider Anthropic + có key  → NATIVE function-calling (submit_deal_score schema enforce)
/// - Mọi provider khác            → JSON-prompt + tolerant parse + retry 1 lần (legacy)
///
/// Cache prompt-hash 24h dùng chung 2 path.
public class DealScoringService
{
    private readonly ProviderRegistry _registry;
    private readonly AiResponseCache _cache;
    private readonly NativeToolScorer _native;
    private readonly AiModelRegistry _modelRegistry;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<DealScoringService> _log;

    private const string SystemJsonPrompt =
        "Bạn là trưởng phòng kinh doanh giàu kinh nghiệm, đánh giá khả năng CHỐT (thắng deal) của cơ hội bán hàng tour. " +
        "Căn cứ HÀNH ĐỘNG của Sale: mức độ tương tác, lần chăm sóc gần nhất, tiến triển qua các trạng thái, " +
        "phản hồi của khách, giá trị deal và độ trễ. CHỈ trả JSON thuần (bắt đầu '{'), KHÔNG markdown, KHÔNG giải thích ngoài JSON. Tiếng Việt.";

    private const string SystemNativeTool =
        "Bạn là trưởng phòng kinh doanh giàu kinh nghiệm, đánh giá khả năng CHỐT của cơ hội bán tour. " +
        "Căn cứ hành động Sale: tương tác, chăm sóc, tiến triển, phản hồi khách, giá trị deal, độ trễ. " +
        "Gọi tool submit_deal_score với kết quả. Tiếng Việt.";

    public DealScoringService(ProviderRegistry registry, AiResponseCache cache,
        NativeToolScorer native, AiModelRegistry modelRegistry,
        IWorkflowTraceAccessor trace, ILogger<DealScoringService> log)
    {
        _registry = registry; _cache = cache; _native = native;
        _modelRegistry = modelRegistry; _trace = trace; _log = log;
    }

    public async Task<DealScore> ScoreAsync(string profile, string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("DealScoring");
        trace?.SetMeta("profileChars", profile.Length);

        // Resolve qua AiModelRegistry → đảm bảo có provider/model non-empty cho cache key + downstream.
        var resolved = _modelRegistry.Resolve(AiFeature.DealScoring, provider, model);
        provider = resolved.Provider;
        model    = resolved.Model;
        apiKey   = apiKey ?? resolved.ApiKey;

        var p = _registry.Resolve(provider);
        trace?.SetMeta("provider", p.Id);
        trace?.SetMeta("model", model);

        var key = AiResponseCache.Hash("deal-score", model, profile);
        var cacheTimer = trace?.Begin("cache_lookup");
        var cached = _cache.TryGet<DealScore>(key);
        if (cached != null)
        {
            cacheTimer?.Done("ok", $"Cache HIT (24h) → winRate={cached.WinRate}%, level={cached.Level}",
                new() { ["winRate"] = cached.WinRate, ["level"] = cached.Level });
            return cached;
        }
        cacheTimer?.Done("skip", "Cache MISS → gọi AI");

        // ── Dispatch theo provider ────────────────────────────────────────────
        DealScore result;
        if (string.Equals(p.Id, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            trace?.Step("path_dispatch", "ok", 0,
                "Provider anthropic → native function-calling (schema enforce)",
                new() { ["path"] = "native-tool", ["tool"] = "submit_deal_score" });
            result = await ScoreWithNativeToolAsync(profile, model, apiKey, trace, ct);
        }
        else
        {
            trace?.Step("path_dispatch", "ok", 0,
                $"Provider {p.Id} → JSON-prompt fallback (tolerant parse + retry)",
                new() { ["path"] = "json-prompt" });
            result = await ScoreWithJsonPromptAsync(p, profile, provider, model, apiKey, trace, ct);
        }

        _cache.Save(key, result);
        return result;
    }

    // ─── Native function-calling path (Anthropic) ─────────────────────────────────
    private async Task<DealScore> ScoreWithNativeToolAsync(
        string profile, string? model, string? apiKey,
        TraceCollector? trace, CancellationToken ct)
    {
        var schema = BuildDealScoreSchema();
        var userPrompt = BuildPromptNative(profile);

        var res = await _native.RunAsync<DealScore>(
            systemPrompt:     SystemNativeTool,
            userPrompt:       userPrompt,
            toolSchema:       schema,
            terminalToolName: "submit_deal_score",
            parser:           ParseToolInput,
            apiKeyOverride:   apiKey,
            model:            model!,
            maxTokens:        2500,
            trace:            trace,
            ct:               ct);

        return res.Value with { AiModel = res.Model, AiProvider = "anthropic" };
    }

    // ─── JSON-prompt path (fallback) ──────────────────────────────────────────────
    private async Task<DealScore> ScoreWithJsonPromptAsync(
        IAiProvider p, string profile, string? provider, string? model, string? apiKey,
        TraceCollector? trace, CancellationToken ct)
    {
        Exception? last = null;

        // Reasoning model (deepseek/minimax) thỉnh thoảng trả JSON xấu/cụt → retry 1 lần với
        // chỉ thị chặt hơn + token cao hơn. Phục hồi phần lớn deal lẽ ra bị rớt khỏi bảng.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = attempt == 1
                ? BuildPromptJson(profile)
                : BuildPromptJson(profile) + "\n\nLƯU Ý: Lần trước trả SAI định dạng. CHỈ trả ĐÚNG 1 JSON object hợp lệ, không thêm bất kỳ chữ nào ngoài JSON.";
            var req = new CompleteRequest(
                Prompt: prompt, Provider: provider, Model: model,
                MaxTokens: attempt == 1 ? 1800 : 2400, Temperature: 0.3, System: SystemJsonPrompt, ApiKey: apiKey);
            var aiTimer = trace?.Begin($"ai_score_attempt{attempt}");
            try
            {
                var res = await p.CompleteAsync(req, ct);
                if (string.IsNullOrWhiteSpace(res.Text))
                {
                    aiTimer?.Done("fail", $"AI trả rỗng (finish={res.FinishReason})");
                    throw new InvalidOperationException($"AI trả rỗng (finish={res.FinishReason})");
                }
                var ok = ParseRawText(res.Text) with { AiModel = res.Model, AiProvider = p.Id };
                aiTimer?.Done("ok",
                    $"Provider {p.Id} → winRate={ok.WinRate}%, level={ok.Level}, tokens {res.InputTokens}/{res.OutputTokens}, {res.LatencyMs}ms",
                    new() {
                        ["provider"] = p.Id, ["model"] = res.Model,
                        ["promptChars"] = prompt.Length, ["maxTokens"] = req.MaxTokens,
                        ["tokIn"] = res.InputTokens, ["tokOut"] = res.OutputTokens,
                        ["latencyMs"] = res.LatencyMs,
                        ["winRate"] = ok.WinRate, ["level"] = ok.Level
                    });
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                last = ex;
                aiTimer?.Done("fail", $"Attempt {attempt} lỗi: {ex.Message}");
                _log.LogWarning("Chấm deal lần {N} lỗi: {Msg}", attempt, ex.Message);
            }
        }
        throw last ?? new InvalidOperationException("Chấm deal thất bại");
    }

    // ─── Prompt builders ─────────────────────────────────────────────────────────
    private static string BuildPromptJson(string profile) => $@"NHIỆM VỤ: Đánh giá khả năng THẮNG (chốt) cơ hội bán hàng dưới đây, dựa trên hành động của Sale.

HỒ SƠ CƠ HỘI:
{profile}

{CommonRules}

OUTPUT JSON (KHÔNG markdown):
{{
  ""winRate"": 0-100,
  ""level"": ""cao|trung_binh|thap"",
  ""signals"": [""dấu hiệu tích cực 1"", ""...""],
  ""risks"": [""rủi ro 1"", ""...""],
  ""nextAction"": ""hành động cụ thể nên làm tiếp"",
  ""reason"": ""1 câu lý do ưu tiên""
}}
Bắt đầu trả JSON ngay:";

    private static string BuildPromptNative(string profile) => $@"NHIỆM VỤ: Đánh giá khả năng THẮNG cơ hội bán hàng dưới đây và GỌI TOOL submit_deal_score với kết quả.

HỒ SƠ CƠ HỘI:
{profile}

{CommonRules}

Gọi submit_deal_score NGAY. KHÔNG trả text giải thích ngoài tool.";

    private const string CommonRules = @"QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa
2. winRate = % khả năng chốt thành công (0-100)
3. level: 'cao' (≥60), 'trung_binh' (35-59), 'thap' (<35)
4. signals: dấu hiệu TÍCH CỰC (khách quan tâm, Sale chăm đều, đã báo giá...)
5. risks: rủi ro làm tuột deal (lâu không chăm, khách im, cạnh tranh...)
6. nextAction: 1 hành động CỤ THỂ Sale nên làm tiếp theo NGAY (vd 'Gọi lại trong hôm nay chốt cọc vì khách đã đồng ý giá')
7. reason: 1 câu vì sao nên ưu tiên (hoặc không)
8. Tiếng Việt ngắn gọn, thực dụng";

    // ─── Schema cho native tool ─────────────────────────────────────────────────
    private static JsonElement BuildDealScoreSchema()
        => NativeToolScorer.BuildAnthropicTool(
            name: "submit_deal_score",
            description: "Nộp kết quả chấm khả năng thắng deal. Gọi DUY NHẤT 1 lần khi đã có đủ field.",
            properties: new
            {
                winRate = new
                {
                    type = "integer",
                    minimum = 0, maximum = 100,
                    description = "% khả năng CHỐT thành công (0-100)"
                },
                level = new
                {
                    type = "string",
                    @enum = new[] { "cao", "trung_binh", "thap" },
                    description = "Mức ưu tiên: cao (≥60), trung_binh (35-59), thap (<35)"
                },
                signals = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Dấu hiệu tích cực (khách quan tâm, Sale chăm đều, đã báo giá...)"
                },
                risks = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Rủi ro làm tuột deal (lâu không chăm, khách im, cạnh tranh...)"
                },
                nextAction = new
                {
                    type = "string",
                    description = "1 hành động CỤ THỂ Sale nên làm tiếp NGAY"
                },
                reason = new
                {
                    type = "string",
                    description = "1 câu vì sao nên ưu tiên (hoặc không)"
                }
            },
            required: new[] { "winRate", "level", "signals", "risks", "nextAction", "reason" });

    // ─── Parsers ────────────────────────────────────────────────────────────────
    private DealScore ParseRawText(string raw)
    {
        try
        {
            using var doc = LooseJson.ParseFirstObject(raw);
            return ParseElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse deal score JSON lỗi. Raw: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI trả kết quả không đúng định dạng");
        }
    }

    private static DealScore ParseToolInput(JsonElement root) => ParseElement(root);

    private static DealScore ParseElement(JsonElement root)
    {
        var rate = Math.Clamp(Int(root, "winRate"), 0, 100);
        var level = (Str(root, "level") ?? "").Trim().ToLowerInvariant();
        if (level is not ("cao" or "trung_binh" or "thap"))
            level = rate >= 60 ? "cao" : rate >= 35 ? "trung_binh" : "thap";

        return new DealScore(
            WinRate:    rate,
            Level:      level,
            Signals:    StrList(root, "signals"),
            Risks:      StrList(root, "risks"),
            NextAction: Str(root, "nextAction") ?? "",
            Reason:     Str(root, "reason") ?? "",
            AiModel:    null, AiProvider: null);
    }

    // ─── helpers JSON ────────────────────────────────────────────────────────────
    private static bool TryGet(JsonElement el, string name, out JsonElement v)
    {
        v = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var pr in el.EnumerateObject())
            if (string.Equals(pr.Name, name, StringComparison.OrdinalIgnoreCase)) { v = pr.Value; return true; }
        return false;
    }
    private static string? Str(JsonElement el, string name)
        => TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static int Int(JsonElement el, string name)
    {
        if (!TryGet(el, name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(
                new string(p.GetString()!.Where(char.IsDigit).ToArray()), out var s)) return s;
        return 0;
    }
    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var it in p.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                    list.Add(it.GetString()!);
        return list;
    }
}
