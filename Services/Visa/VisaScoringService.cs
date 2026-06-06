using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Visa;

/// Bước 2 — chấm tỉ lệ đậu/rớt visa từ HỒ SƠ TEXT (bản AI đọc, NV có thể đã sửa).
/// BUFFERED CompleteAsync (mẫu ReviewService) để tránh reasoning model rò 'lời suy nghĩ' vào JSON.
/// Chấm trên text → dùng được provider rẻ (OpenCode) hoặc chính provider vision tùy client chọn.
public class VisaScoringService
{
    private readonly ProviderRegistry _registry;
    private readonly ILogger<VisaScoringService> _log;

    private const string SYSTEM =
        "Bạn là chuyên gia thẩm định hồ sơ xin visa du lịch, nhiều năm kinh nghiệm đánh giá khả năng đậu/rớt. " +
        "Đánh giá KHÁCH QUAN dựa trên hồ sơ, theo nguyên tắc chung của lãnh sự: chứng minh tài chính, " +
        "ràng buộc về nước (công việc, tài sản, gia đình), lịch sử du lịch, tính nhất quán hồ sơ. " +
        "CHỈ trả JSON thuần (bắt đầu '{'), KHÔNG markdown, KHÔNG giải thích ngoài JSON. Tiếng Việt.";

    public VisaScoringService(ProviderRegistry registry, ILogger<VisaScoringService> log)
    {
        _registry = registry; _log = log;
    }

    public async Task<VisaResult> ScoreAsync(
        string profile, string? country, string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var p = _registry.Resolve(provider);
        var req = new CompleteRequest(
            Prompt: BuildPrompt(profile, country), Provider: provider, Model: model,
            MaxTokens: 2500, Temperature: 0.3, System: SYSTEM, ApiKey: apiKey);

        var res = await p.CompleteAsync(req, ct);
        if (string.IsNullOrWhiteSpace(res.Text))
            throw new InvalidOperationException($"AI trả rỗng khi chấm visa (finish={res.FinishReason})");

        var parsed = Parse(res.Text);
        return parsed with { AiModel = res.Model, AiProvider = p.Id };
    }

    private static string BuildPrompt(string profile, string? country)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "(AI tự nhận diện từ hồ sơ)" : country!;
        return $@"NHIỆM VỤ: Thẩm định khả năng ĐẬU/RỚT của bộ hồ sơ xin visa dưới đây.

NƯỚC XIN VISA: {c}

HỒ SƠ (do hệ thống đọc từ giấy tờ đã upload):
{profile}

QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa thông tin
2. passRate là % ước lượng khả năng ĐẬU (0-100)
3. level: 'cao' (≥70), 'trung_binh' (40-69), 'thap' (<40)
4. missingDocs: giấy tờ THƯỜNG CẦN cho loại visa này nhưng hồ sơ CHƯA có/CÒN yếu
5. suggestions: cách cụ thể để TĂNG tỉ lệ đậu
6. Tiếng Việt tự nhiên, ngắn gọn, thực dụng

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

    private VisaResult Parse(string raw)
    {
        try
        {
            using var doc = LooseJson.ParseFirstObject(raw);
            var root = doc.RootElement;

            var rate = Int(root, "passRate");
            rate = Math.Clamp(rate, 0, 100);
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
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse visa score JSON lỗi. Raw: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI trả kết quả không đúng định dạng — thử lại.");
        }
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
