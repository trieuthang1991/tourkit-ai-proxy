using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Visa;

/// Bước 2 — chấm tỉ lệ đậu/rớt visa từ HỒ SƠ TEXT (bản AI đọc, NV có thể đã sửa).
///
/// Dual-path scoring:
/// - Provider Anthropic + có key  → NATIVE function-calling (schema enforce, 0% leak markdown/thinking)
/// - Mọi provider khác            → JSON-prompt + tolerant parse + retry 1 lần (legacy path)
///
/// Cache prompt-hash 24h dùng chung 2 path. Trace + AiUsageLog cũng dùng chung.
public class VisaScoringService
{
    private readonly ProviderRegistry _registry;
    private readonly AiResponseCache _cache;
    private readonly NativeToolScorer _native;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<VisaScoringService> _log;

    private const string SystemJsonPrompt =
        "Bạn là chuyên gia thẩm định hồ sơ xin visa du lịch, nhiều năm kinh nghiệm đánh giá khả năng đậu/rớt. " +
        "Đánh giá KHÁCH QUAN dựa trên hồ sơ, theo nguyên tắc chung của lãnh sự: chứng minh tài chính, " +
        "ràng buộc về nước (công việc, tài sản, gia đình), lịch sử du lịch, tính nhất quán hồ sơ. " +
        "CHỈ trả JSON thuần (bắt đầu '{'), KHÔNG markdown, KHÔNG giải thích ngoài JSON. Tiếng Việt.";

    private const string SystemNativeTool =
        "Bạn là chuyên gia thẩm định hồ sơ xin visa du lịch, nhiều năm kinh nghiệm đánh giá khả năng đậu/rớt. " +
        "Đánh giá KHÁCH QUAN dựa trên hồ sơ, theo nguyên tắc lãnh sự: chứng minh tài chính, ràng buộc về nước, " +
        "lịch sử du lịch, tính nhất quán hồ sơ. Gọi tool submit_visa_score với kết quả. Tiếng Việt.";

    public VisaScoringService(ProviderRegistry registry, AiResponseCache cache,
        NativeToolScorer native,
        IWorkflowTraceAccessor trace, ILogger<VisaScoringService> log)
    {
        _registry = registry; _cache = cache; _native = native; _trace = trace; _log = log;
    }

    public async Task<VisaResult> ScoreAsync(
        string profile, string? country, string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("VisaScoring");
        trace?.SetMeta("country", country);
        trace?.SetMeta("profileChars", profile.Length);

        var p = _registry.Resolve(provider);
        trace?.SetMeta("provider", p.Id);

        // Cache prompt-hash 24h: NV chấm lại cùng hồ sơ trong ngày → KHÔNG gọi AI.
        var key = AiResponseCache.Hash("visa-score", model, $"{country}|{profile}");
        var cacheTimer = trace?.Begin("cache_lookup");
        var cached = _cache.TryGet<VisaResult>(key);
        if (cached != null)
        {
            cacheTimer?.Done("ok", $"Cache HIT (24h) → trả ngay, skip AI",
                new() { ["cacheKey"] = key[..16] + "…", ["passRate"] = cached.PassRate });
            return cached;
        }
        cacheTimer?.Done("skip", "Cache MISS → gọi AI", new() { ["cacheKey"] = key[..16] + "…" });

        // ── Dispatch theo provider ────────────────────────────────────────────
        VisaResult result;
        if (string.Equals(p.Id, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            trace?.Step("path_dispatch", "ok", 0,
                "Provider anthropic → native function-calling (schema enforce)",
                new() { ["path"] = "native-tool", ["tool"] = "submit_visa_score" });
            result = await ScoreWithNativeToolAsync(profile, country, model, apiKey, trace, ct);
        }
        else
        {
            trace?.Step("path_dispatch", "ok", 0,
                $"Provider {p.Id} → JSON-prompt fallback (tolerant parse + retry)",
                new() { ["path"] = "json-prompt" });
            result = await ScoreWithJsonPromptAsync(p, profile, country, provider, model, apiKey, trace, ct);
        }

        _cache.Save(key, result);
        trace?.Step("cache_save", "ok", 0, "Lưu kết quả vào cache 24h");
        return result;
    }

    // ─── Native function-calling path (Anthropic) ─────────────────────────────────
    private async Task<VisaResult> ScoreWithNativeToolAsync(
        string profile, string? country, string? model, string? apiKey,
        TraceCollector? trace, CancellationToken ct)
    {
        var schema = BuildVisaScoreSchema();
        var userPrompt = BuildPromptNative(profile, country);

        var res = await _native.RunAsync<VisaResult>(
            systemPrompt:     SystemNativeTool,
            userPrompt:       userPrompt,
            toolSchema:       schema,
            terminalToolName: "submit_visa_score",
            parser:           ParseToolInput,
            apiKeyOverride:   apiKey,
            model:            string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-5" : model!,
            maxTokens:        3000,
            trace:            trace,
            ct:               ct);

        return res.Value with { AiModel = res.Model, AiProvider = "anthropic" };
    }

    // ─── JSON-prompt path (fallback cho mọi provider khác) ────────────────────────
    private async Task<VisaResult> ScoreWithJsonPromptAsync(
        IAiProvider p, string profile, string? country,
        string? provider, string? model, string? apiKey,
        TraceCollector? trace, CancellationToken ct)
    {
        Exception? last = null;

        // Retry 1 lần nếu AI trả JSON xấu (reasoning model) — chỉ thị chặt hơn + token cao hơn.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = attempt == 1
                ? BuildPromptJson(profile, country)
                : BuildPromptJson(profile, country) + "\n\nLƯU Ý: Lần trước trả SAI định dạng. CHỈ trả ĐÚNG 1 JSON object hợp lệ, không thêm chữ nào ngoài JSON.";
            var req = new CompleteRequest(
                Prompt: prompt, Provider: provider, Model: model,
                MaxTokens: attempt == 1 ? 2500 : 3200, Temperature: 0.3, System: SystemJsonPrompt, ApiKey: apiKey);
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
                    $"Provider {p.Id} → passRate={ok.PassRate}%, level={ok.Level}, tokens {res.InputTokens}/{res.OutputTokens}, {res.LatencyMs}ms",
                    new() {
                        ["provider"] = p.Id, ["model"] = res.Model,
                        ["promptChars"] = prompt.Length, ["maxTokens"] = req.MaxTokens,
                        ["tokIn"] = res.InputTokens, ["tokOut"] = res.OutputTokens,
                        ["latencyMs"] = res.LatencyMs,
                        ["passRate"] = ok.PassRate, ["level"] = ok.Level
                    });
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                last = ex;
                aiTimer?.Done("fail", $"Attempt {attempt} lỗi: {ex.Message}");
                _log.LogWarning("Chấm visa lần {N} lỗi: {Msg}", attempt, ex.Message);
            }
        }
        throw last ?? new InvalidOperationException("Chấm visa thất bại");
    }

    // ─── Prompt builders ─────────────────────────────────────────────────────────
    private static string BuildPromptJson(string profile, string? country)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "(AI tự nhận diện từ hồ sơ)" : country!;
        return $@"NHIỆM VỤ: Thẩm định khả năng ĐẬU/RỚT của bộ hồ sơ xin visa dưới đây.

NƯỚC XIN VISA: {c}

HỒ SƠ (do hệ thống đọc từ giấy tờ đã upload):
{profile}

{CommonRules}

OUTPUT JSON (KHÔNG markdown):
{{
  ""passRate"": 0-100,
  ""level"": ""cao|trung_binh|thap"",
  ""strengths"": [""điểm mạnh 1"", ""điểm mạnh 2""],
  ""weaknesses"": [""điểm yếu/rủi ro 1"", ""điểm yếu 2""],
  ""missingDocs"": [""giấy tờ cần bổ sung 1"", ""giấy tờ 2""],
  ""suggestions"": [""đề xuất cải thiện 1"", ""đề xuất 2""],
  ""summary"": ""1-2 câu kết luận tổng quan""
}}
Bắt đầu trả JSON ngay:";
    }

    private static string BuildPromptNative(string profile, string? country)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "(AI tự nhận diện từ hồ sơ)" : country!;
        return $@"NHIỆM VỤ: Thẩm định khả năng ĐẬU/RỚT visa và GỌI TOOL submit_visa_score với kết quả.

NƯỚC XIN VISA: {c}

HỒ SƠ:
{profile}

{CommonRules}

Gọi submit_visa_score NGAY. KHÔNG trả text giải thích ngoài tool.";
    }

    private const string CommonRules = @"QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa thông tin
2. passRate là % ước lượng khả năng ĐẬU (0-100)
3. level: 'cao' (≥70), 'trung_binh' (40-69), 'thap' (<40)
4. missingDocs: giấy tờ THƯỜNG CẦN cho loại visa này nhưng hồ sơ CHƯA có/CÒN yếu
5. suggestions: cách cụ thể để TĂNG tỉ lệ đậu
6. Tiếng Việt tự nhiên, ngắn gọn, thực dụng";

    // ─── Schema cho native tool ─────────────────────────────────────────────────
    private static JsonElement BuildVisaScoreSchema()
        => NativeToolScorer.BuildAnthropicTool(
            name: "submit_visa_score",
            description: "Nộp kết quả thẩm định hồ sơ visa. Gọi DUY NHẤT 1 lần khi đã có đủ field.",
            properties: new
            {
                passRate = new
                {
                    type = "integer",
                    minimum = 0, maximum = 100,
                    description = "% khả năng ĐẬU visa (0-100)"
                },
                level = new
                {
                    type = "string",
                    @enum = new[] { "cao", "trung_binh", "thap" },
                    description = "Mức độ: cao (≥70), trung_binh (40-69), thap (<40)"
                },
                strengths = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Điểm mạnh trong hồ sơ"
                },
                weaknesses = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Điểm yếu / rủi ro"
                },
                missingDocs = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Giấy tờ thường cần nhưng hồ sơ chưa có / còn yếu"
                },
                suggestions = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Đề xuất cải thiện cụ thể để tăng tỉ lệ đậu"
                },
                summary = new
                {
                    type = "string",
                    description = "1-2 câu kết luận tổng quan"
                }
            },
            required: new[] { "passRate", "level", "strengths", "weaknesses", "missingDocs", "suggestions", "summary" });

    // ─── Parsers ────────────────────────────────────────────────────────────────
    private VisaResult ParseRawText(string raw)
    {
        try
        {
            using var doc = LooseJson.ParseFirstObject(raw);
            return ParseElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse visa score JSON lỗi. Raw: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI trả kết quả không đúng định dạng — thử lại.");
        }
    }

    private static VisaResult ParseToolInput(JsonElement root) => ParseElement(root);

    private static VisaResult ParseElement(JsonElement root)
    {
        var rate = Math.Clamp(Int(root, "passRate"), 0, 100);
        var level = (Str(root, "level") ?? "").Trim().ToLowerInvariant();
        if (level is not ("cao" or "trung_binh" or "thap"))
            level = rate >= 70 ? "cao" : rate >= 40 ? "trung_binh" : "thap";

        return new VisaResult(
            PassRate:    rate,
            Level:       level,
            Strengths:   StrList(root, "strengths"),
            Weaknesses:  StrList(root, "weaknesses"),
            MissingDocs: StrList(root, "missingDocs"),
            Suggestions: StrList(root, "suggestions"),
            Summary:     Str(root, "summary") ?? "",
            AiModel:     null, AiProvider: null);
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
