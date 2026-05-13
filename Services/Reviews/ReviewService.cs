using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Reviews;

/// Orchestrate 1 lượt review KH: build prompt → gọi IAiProvider → parse JSON → save.
/// Cache check qua dataFingerprint: nếu KH chưa đổi data → trả review cũ ngay (không gọi AI).
/// Tunable: prompt tour-specific (du lịch / tour operator). Đổi ngành = đổi const SYSTEM_PROMPT.
public class ReviewService
{
    private readonly ReviewRepository _reviews;
    private readonly ProviderRegistry _registry;
    private readonly ILogger<ReviewService> _log;

    private const string SYSTEM_PROMPT =
        "Bạn là trợ lý phân tích khách hàng cho công ty du lịch / tour operator Việt Nam (Tourkit). " +
        "Output ONLY raw JSON theo schema, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Bắt đầu output bằng dấu `{` ngay. KHÔNG suy diễn ngoài dữ liệu. " +
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Đề xuất hành động phải gắn với dữ liệu thực tế.";

    public ReviewService(ReviewRepository reviews, ProviderRegistry registry, ILogger<ReviewService> log)
    {
        _reviews = reviews; _registry = registry; _log = log;
    }

    /// Return review (cached nếu fingerprint không đổi & không forceFresh; gọi AI nếu cần).
    /// `onStage` được gọi lifecycle: "preparing" → "calling" → "chunk" (kèm delta) → "parsing" → null khi done.
    /// Tuple: (review, fromCache).
    public async Task<(CustomerReview review, bool fromCache)> ReviewAsync(
        Customer customer, bool forceFresh = false,
        Func<string, string?, Task>? onStage = null,
        CancellationToken ct = default)
    {
        var fingerprint = ReviewRepository.FingerprintFor(customer);

        if (!forceFresh)
        {
            var existing = _reviews.Get(customer.Id);
            if (existing != null && existing.DataFingerprint == fingerprint)
                return (existing, true);
        }

        async Task Stage(string stage, string? delta = null)
        {
            if (onStage != null) await onStage(stage, delta);
        }

        await Stage("preparing");
        var prompt = BuildPrompt(customer);
        var provider = _registry.Resolve(null);   // dùng default provider

        var req = new CompleteRequest(
            Prompt:      prompt,
            Provider:    null,
            Model:       null,                     // provider tự chọn default model
            MaxTokens:   8000,                     // KH VIP với 8+ tour history → JSON pretty-print ~3-5k tok
            Temperature: 0.4,
            System:      SYSTEM_PROMPT
        );

        await Stage("calling");

        // Dùng CompleteAsync (buffered) thay vì StreamAsync vì:
        // 1. DeepSeek/Kimi reasoning models stream cả `reasoning_content` (chain-of-thought)
        //    lẫn `content` — output bị trộn prose+JSON → parse fail.
        // 2. CompleteAsync chỉ trả final assembled `message.content` sạch.
        // Trade-off: mất live-stream chunk preview, nhưng vẫn có stage indicator
        // (preparing/calling/parsing/done) đủ visibility.
        var result = await provider.CompleteAsync(req, ct);

        if (string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException($"AI trả text rỗng cho KH {customer.Id} (finish={result.FinishReason})");

        await Stage("parsing");
        var parsed = ParseReviewJson(result.Text);

        var review = new CustomerReview(
            Id:                  Guid.NewGuid().ToString("N"),
            CustomerId:          customer.Id,
            Rank:                parsed.Rank ?? "C",
            RankReason:          parsed.RankReason ?? "",
            Alert:               parsed.Alert ?? new ReviewAlert("none", null),
            Portrait:            parsed.Portrait ?? "",
            Strengths:           parsed.Strengths ?? new(),
            Concerns:            parsed.Concerns ?? new(),
            Preferences:         parsed.Preferences ?? "",
            ActionNow:           parsed.ActionNow ?? new ReviewAction("Liên hệ thăm hỏi", "Chưa đủ dữ liệu để đề xuất cụ thể"),
            Action30Days:        parsed.Action30Days ?? new(),
            ProductSuggestions:  parsed.ProductSuggestions ?? new(),
            SummaryLine:         parsed.SummaryLine ?? BuildFallbackSummary(parsed.Rank ?? "C", customer),
            DataFingerprint:     fingerprint,
            AiModel:             result.Model,
            AiProvider:          provider.Id,
            TokensIn:            result.InputTokens,
            TokensOut:           result.OutputTokens,
            GeneratedAt:         DateTime.UtcNow.ToString("o"),
            Feedback:            null
        );

        _reviews.Save(review);
        return (review, false);
    }

    // ─── Prompt builder ───────────────────────────────────────────────────────
    private static string BuildPrompt(Customer c)
    {
        var dataJson = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });

        return $@"NHIỆM VỤ: Phân tích khách hàng tour dưới đây và trả về JSON review cho Sale/CSKH dùng quyết định chăm sóc.

DỮ LIỆU KHÁCH HÀNG:
{dataJson}

QUY TẮC:
1. CHỈ dựa trên data trên, KHÔNG bịa thông tin không có
2. Nếu thiếu data ở mục nào, ghi 'Chưa đủ dữ liệu để đánh giá'
3. Văn phong cụ thể, hành động được, tiếng Việt tự nhiên
4. Mọi đề xuất phải gắn với data cụ thể (vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây')

TIÊU CHÍ XẾP HẠNG (cho tour operator):
- A: VIP trung thành, ≥5 tour, AOV ≥20tr, mua đều, không khiếu nại
- B: Khách tốt, 2-4 tour, có tiềm năng upsell hoặc thị trường cao cấp
- C: Bình thường, ít tương tác, cần kích hoạt qua promo/newsletter
- D: Rủi ro rời bỏ (>180 ngày không mua, có khiếu nại chưa giải quyết, hoặc huỷ tour gần)

OUTPUT JSON (KHÔNG markdown fences, KHÔNG giải thích thêm):
{{
  ""rank"": ""A|B|C|D"",
  ""rankReason"": ""1 câu ngắn"",
  ""alert"": {{ ""level"": ""high|medium|none"", ""message"": ""trống nếu không có vấn đề"" }},
  ""portrait"": ""Chân dung KH 1-2 câu (tuổi, vùng, tần suất đi tour, phong cách)"",
  ""strengths"": [""điểm tích cực 1"", ""điểm tích cực 2""],
  ""concerns"": [""điểm cần lưu ý 1"", ""điểm cần lưu ý 2""],
  ""preferences"": ""Sở thích du lịch rút từ lịch sử (vd: thích tour biển cao cấp, đi cùng gia đình 4-5 người, AOV 20tr)"",
  ""actionNow"": {{ ""task"": ""Việc cần làm hôm nay, cụ thể"", ""reason"": ""Lý do dựa data nào"" }},
  ""action30Days"": [""ý tưởng chăm sóc 1"", ""ý tưởng 2"", ""ý tưởng 3""],
  ""productSuggestions"": [""tour/dịch vụ phù hợp 1"", ""tour 2""],
  ""summaryLine"": ""1 dòng ≤80 ký tự: Hạng + tình trạng + hành động ưu tiên""
}}

Bắt đầu trả JSON ngay:";
    }

    // ─── JSON parser tolerant — model có thể wrap fences hoặc thêm prose ───────
    private record ParsedReview(
        string? Rank,
        string? RankReason,
        ReviewAlert? Alert,
        string? Portrait,
        List<string>? Strengths,
        List<string>? Concerns,
        string? Preferences,
        ReviewAction? ActionNow,
        List<string>? Action30Days,
        List<string>? ProductSuggestions,
        string? SummaryLine
    );

    private static ParsedReview ParseReviewJson(string raw)
    {
        // 1. Gỡ markdown fences
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

        // 2. Trim đến balanced top-level object
        var start = cleaned.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Output không có JSON object");
        cleaned = cleaned.Substring(start);

        int depth = 0, end = -1; bool inStr = false, esc = false;
        for (int i = 0; i < cleaned.Length; i++)
        {
            var ch = cleaned[i];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end > 0) cleaned = cleaned.Substring(0, end + 1);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            return new ParsedReview(
                Rank:               GetString(root, "rank")?.Trim().ToUpperInvariant().FirstOrDefault().ToString(),
                RankReason:         GetString(root, "rankReason") ?? GetString(root, "rank_reason"),
                Alert:              ParseAlert(root),
                Portrait:           GetString(root, "portrait"),
                Strengths:          GetStringList(root, "strengths"),
                Concerns:           GetStringList(root, "concerns"),
                Preferences:        GetString(root, "preferences"),
                ActionNow:          ParseAction(root, "actionNow") ?? ParseAction(root, "action_now"),
                Action30Days:       GetStringList(root, "action30Days") ?? GetStringList(root, "action_30days"),
                ProductSuggestions: GetStringList(root, "productSuggestions") ?? GetStringList(root, "product_suggestions"),
                SummaryLine:        GetString(root, "summaryLine") ?? GetString(root, "summary_line")
            );
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Parse review JSON failed: {ex.Message}. Raw đầu: {raw[..Math.Min(raw.Length, 200)]}", ex);
        }
    }

    // Case-insensitive property lookup — model có thể trả về camelCase, snake_case, lowercase,
    // tất cả hợp lệ. Walk properties đối chiếu OrdinalIgnoreCase.
    private static bool TryGet(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value; return true;
            }
        }
        return false;
    }

    private static string? GetString(JsonElement el, string name)
        => TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string>? GetStringList(JsonElement el, string name)
    {
        if (!TryGet(el, name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
        return list;
    }

    private static ReviewAlert? ParseAlert(JsonElement root)
    {
        if (!TryGet(root, "alert", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var level = GetString(a, "level") ?? "none";
        var msg = GetString(a, "message");
        return new ReviewAlert(level.ToLowerInvariant(), string.IsNullOrWhiteSpace(msg) ? null : msg);
    }

    private static ReviewAction? ParseAction(JsonElement root, string name)
    {
        if (!TryGet(root, name, out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var task = GetString(a, "task") ?? "";
        var reason = GetString(a, "reason") ?? "";
        if (string.IsNullOrWhiteSpace(task)) return null;
        return new ReviewAction(task, reason);
    }

    private static string BuildFallbackSummary(string rank, Customer c)
        => $"Hạng {rank} · {c.Metrics.TotalTours} tour · {c.Metrics.LastPurchaseDaysAgo?.ToString() ?? "?"}d trước";
}
