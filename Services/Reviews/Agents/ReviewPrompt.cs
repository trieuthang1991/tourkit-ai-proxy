// Services/Reviews/Agents/ReviewPrompt.cs
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews.Agents;

/// <summary>
/// Shared prompt + JSON schema + tolerant parser cho 2 agent (Json + Native).
/// SYSTEM_PROMPT, ranking criteria, output JSON shape duy nhất 1 nguồn — đổi ngành = đổi ở đây.
/// </summary>
public static class ReviewPrompt
{
    /// <summary>
    /// System prompt cho JSON-prompt agent: chỉ thị KHÔNG markdown, ngữ cảnh ngành du lịch.
    /// Native-tool agent KHÔNG cần phần "Output ONLY raw JSON" vì schema tự enforce, dùng <see cref="SystemForNativeTool"/>.
    /// </summary>
    public const string SystemForJsonPrompt =
        "Bạn là trợ lý phân tích khách hàng cho công ty du lịch / tour operator Việt Nam (Tourkit). " +
        "Output ONLY raw JSON theo schema, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Bắt đầu output bằng dấu `{` ngay. KHÔNG suy diễn ngoài dữ liệu. " +
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Đề xuất hành động phải gắn với dữ liệu thực tế.";

    /// <summary>
    /// System prompt cho native-tool agent: schema do tool enforce, chỉ cần hướng dẫn cách viết.
    /// Bỏ phần "Output JSON" vì AI phải gọi tool submit_customer_review thay vì in JSON ra text.
    /// </summary>
    public const string SystemForNativeTool =
        "Bạn là trợ lý phân tích khách hàng cho công ty du lịch / tour operator Việt Nam (Tourkit). " +
        "Phân tích dữ liệu khách hàng → gọi tool submit_customer_review với kết quả. " +
        "KHÔNG suy diễn ngoài dữ liệu; thiếu data thì ghi 'Chưa đủ dữ liệu để đánh giá'. " +
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Mọi đề xuất phải gắn với data cụ thể " +
        "(vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây'). " +
        "BẮT BUỘC gọi submit_customer_review đúng 1 lần — KHÔNG trả text giải thích thêm.";

    /// <summary>
    /// Build user prompt chứa data + ranking criteria + JSON schema (dùng cho JSON-prompt agent).
    /// </summary>
    public static string BuildUserPromptForJson(Customer c)
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

{RankingCriteria}

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

    /// <summary>
    /// Build user prompt cho native-tool agent: data + ranking criteria, KHÔNG có JSON shape (schema lo).
    /// </summary>
    public static string BuildUserPromptForNative(Customer c)
    {
        var dataJson = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });
        return $@"NHIỆM VỤ: Phân tích khách hàng tour dưới đây và GỌI TOOL submit_customer_review với kết quả.

DỮ LIỆU KHÁCH HÀNG:
{dataJson}

QUY TẮC:
1. CHỈ dựa trên data trên, KHÔNG bịa thông tin không có
2. Nếu thiếu data ở mục nào, ghi 'Chưa đủ dữ liệu để đánh giá'
3. Văn phong cụ thể, hành động được, tiếng Việt tự nhiên
4. Mọi đề xuất phải gắn với data cụ thể (vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây')

{RankingCriteria}

Gọi submit_customer_review NGAY với 11 field. KHÔNG trả text giải thích.";
    }

    /// <summary>Schema JSON cho terminal tool submit_customer_review (native-tool agent).</summary>
    public static JsonElement BuildSubmitReviewToolSchema()
    {
        // Anthropic tools format: {name, description, input_schema}
        var schema = new
        {
            name = "submit_customer_review",
            description = "Nộp kết quả phân tích khách hàng. Gọi DUY NHẤT 1 lần khi đã có đủ 11 field.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    rank = new
                    {
                        type = "string",
                        @enum = new[] { "A", "B", "C", "D" },
                        description = "Hạng KH: A=VIP trung thành, B=tốt có tiềm năng, C=bình thường, D=rủi ro rời bỏ"
                    },
                    rankReason = new { type = "string", description = "1 câu ngắn giải thích vì sao hạng đó" },
                    alert = new
                    {
                        type = "object",
                        properties = new
                        {
                            level = new
                            {
                                type = "string",
                                @enum = new[] { "high", "medium", "none" },
                                description = "Mức cảnh báo cho Sale/CSKH"
                            },
                            message = new { type = "string", description = "Mô tả vấn đề (rỗng nếu level=none)" }
                        },
                        required = new[] { "level" }
                    },
                    portrait = new { type = "string", description = "Chân dung KH 1-2 câu (tuổi, vùng, phong cách)" },
                    strengths = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 điểm tích cực của KH"
                    },
                    concerns = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 điểm cần lưu ý / rủi ro"
                    },
                    preferences = new { type = "string", description = "Sở thích du lịch rút từ lịch sử" },
                    actionNow = new
                    {
                        type = "object",
                        properties = new
                        {
                            task = new { type = "string", description = "Việc cần làm hôm nay, cụ thể" },
                            reason = new { type = "string", description = "Lý do dựa data nào" }
                        },
                        required = new[] { "task", "reason" }
                    },
                    action30Days = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 ý tưởng chăm sóc trong 30 ngày tới"
                    },
                    productSuggestions = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "1-3 tour/dịch vụ phù hợp"
                    },
                    summaryLine = new
                    {
                        type = "string",
                        description = "1 dòng ≤80 ký tự: Hạng + tình trạng + hành động ưu tiên"
                    }
                },
                required = new[]
                {
                    "rank", "rankReason", "alert", "portrait", "strengths", "concerns",
                    "preferences", "actionNow", "action30Days", "productSuggestions", "summaryLine"
                }
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private const string RankingCriteria = @"TIÊU CHÍ XẾP HẠNG (cho tour operator):
- A: VIP trung thành, ≥5 tour, AOV ≥20tr, mua đều, không khiếu nại
- B: Khách tốt, 2-4 tour, có tiềm năng upsell hoặc thị trường cao cấp
- C: Bình thường, ít tương tác, cần kích hoạt qua promo/newsletter
- D: Rủi ro rời bỏ (>180 ngày không mua, có khiếu nại chưa giải quyết, hoặc huỷ tour gần)";

    // ─── JSON parser tolerant (dùng chung 2 agent — JsonAgent từ raw text, NativeAgent từ tool input) ───
    public record ParsedReview(
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

    /// <summary>
    /// Parse raw text từ AI (có thể có markdown fences, prose lẫn JSON).
    /// Gỡ fences → trim đến balanced top-level object → parse case-insensitive.
    /// </summary>
    public static ParsedReview ParseRawText(string raw)
    {
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();
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

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            return ParseElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Parse review JSON failed: {ex.Message}. Raw đầu: {raw[..Math.Min(raw.Length, 200)]}", ex);
        }
    }

    /// <summary>
    /// Parse JsonElement (vd input của tool_use block từ native agent — đã structured rồi).
    /// </summary>
    public static ParsedReview ParseElement(JsonElement root)
    {
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

    /// <summary>
    /// Compose CustomerReview từ ParsedReview + meta — dùng chung 2 agent để tránh drift schema.
    /// </summary>
    public static CustomerReview Compose(
        ParsedReview parsed, Customer customer, string fingerprint,
        string aiProvider, string aiModel, int tokensIn, int tokensOut)
    {
        return new CustomerReview(
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
            AiModel:             aiModel,
            AiProvider:          aiProvider,
            TokensIn:            tokensIn,
            TokensOut:           tokensOut,
            GeneratedAt:         DateTime.UtcNow.ToString("o"),
            Feedback:            null
        );
    }

    // ─── private helpers ────────────────────────────────────────────────────
    private static bool TryGet(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            { value = p.Value; return true; }
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
