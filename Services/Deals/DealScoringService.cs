using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Deals;

/// AI chấm khả năng THẮNG DEAL của 1 cơ hội từ HỒ SƠ TEXT (detail + hành động Sale).
/// BUFFERED (mẫu ReviewService) tránh reasoning model rò 'lời suy nghĩ' vào JSON. Chấm trên text → provider rẻ OK.
public class DealScoringService
{
    private readonly ProviderRegistry _registry;
    private readonly ILogger<DealScoringService> _log;

    private const string SYSTEM =
        "Bạn là trưởng phòng kinh doanh giàu kinh nghiệm, đánh giá khả năng CHỐT (thắng deal) của cơ hội bán hàng tour. " +
        "Căn cứ HÀNH ĐỘNG của Sale: mức độ tương tác, lần chăm sóc gần nhất, tiến triển qua các trạng thái, " +
        "phản hồi của khách, giá trị deal và độ trễ. CHỈ trả JSON thuần (bắt đầu '{'), KHÔNG markdown, KHÔNG giải thích ngoài JSON. Tiếng Việt.";

    public DealScoringService(ProviderRegistry registry, ILogger<DealScoringService> log)
    {
        _registry = registry; _log = log;
    }

    public async Task<DealScore> ScoreAsync(string profile, string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var p = _registry.Resolve(provider);
        var req = new CompleteRequest(
            Prompt: BuildPrompt(profile), Provider: provider, Model: model,
            MaxTokens: 1600, Temperature: 0.3, System: SYSTEM, ApiKey: apiKey);

        var res = await p.CompleteAsync(req, ct);
        if (string.IsNullOrWhiteSpace(res.Text))
            throw new InvalidOperationException($"AI trả rỗng khi chấm deal (finish={res.FinishReason})");

        return Parse(res.Text) with { AiModel = res.Model, AiProvider = p.Id };
    }

    private static string BuildPrompt(string profile) => $@"NHIỆM VỤ: Đánh giá khả năng THẮNG (chốt) cơ hội bán hàng dưới đây, dựa trên hành động của Sale.

HỒ SƠ CƠ HỘI:
{profile}

QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa
2. winRate = % khả năng chốt thành công (0-100)
3. level: 'cao' (≥60), 'trung_binh' (35-59), 'thap' (<35)
4. signals: dấu hiệu TÍCH CỰC (khách quan tâm, Sale chăm đều, đã báo giá...)
5. risks: rủi ro làm tuột deal (lâu không chăm, khách im, cạnh tranh...)
6. nextAction: 1 hành động CỤ THỂ Sale nên làm tiếp theo NGAY (vd 'Gọi lại trong hôm nay chốt cọc vì khách đã đồng ý giá')
7. reason: 1 câu vì sao nên ưu tiên (hoặc không)
8. Tiếng Việt ngắn gọn, thực dụng

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

    private DealScore Parse(string raw)
    {
        try
        {
            using var doc = LooseJson.ParseFirstObject(raw);
            var root = doc.RootElement;

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
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse deal score JSON lỗi. Raw: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI trả kết quả không đúng định dạng");
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
