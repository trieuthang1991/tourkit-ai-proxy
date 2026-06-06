using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Tour;

/// AI đọc mô tả tự do (vd "Tour Đà Nẵng 3N2Đ, 20 khách, đi 15/7, KH chị Lan 0901...")
/// → trả JSON Draft Tour GIT cho NV prefill form. BUFFERED + tolerant parse + retry 1 lần
/// (mẫu DealScoringService) để dùng được cả reasoning model rẻ.
public class TourBuilderService
{
    private readonly ProviderRegistry _registry;
    private readonly AiResponseCache _cache;
    private readonly ILogger<TourBuilderService> _log;

    private const string SYSTEM =
        "Bạn là chuyên viên điều hành tour Việt Nam, đọc mô tả của Sale và bóc tách thành form tour GIT (Group Inclusive Tour). " +
        "CHỈ trả JSON thuần (bắt đầu '{'), KHÔNG markdown, KHÔNG giải thích ngoài JSON. " +
        "KHÔNG bịa thông tin chưa có — field nào không rõ thì để null/0/[]; ghi điều cần làm rõ vào 'warnings'. Tiếng Việt.";

    public TourBuilderService(ProviderRegistry registry, AiResponseCache cache, ILogger<TourBuilderService> log)
    {
        _registry = registry; _cache = cache; _log = log;
    }

    public async Task<TourBuilderDraft> ParseAsync(TourBuilderRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            throw new InvalidOperationException("Mô tả rỗng");

        var p = _registry.Resolve(req.Provider);
        var key = AiResponseCache.Hash("tour-builder", req.Model, req.Prompt);
        var cached = _cache.TryGet<TourBuilderDraft>(key);
        if (cached != null) return cached;
        Exception? last = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = attempt == 1
                ? BuildPrompt(req.Prompt)
                : BuildPrompt(req.Prompt) + "\n\nLƯU Ý: Lần trước trả SAI định dạng. CHỈ trả ĐÚNG 1 JSON object, không chữ nào ngoài JSON.";
            // Reasoning model (deepseek/minimax) tốn nhiều token "thinking" → cần budget rộng.
            // Lần 1 fail → tăng tiếp lần 2. Quan sát: FINISH=length là nguyên nhân chính cho JSON cụt.
            var cr = new CompleteRequest(
                Prompt: prompt, Provider: req.Provider, Model: req.Model,
                MaxTokens: attempt == 1 ? 4500 : 8000, Temperature: 0.2, System: SYSTEM, ApiKey: req.ApiKey);
            try
            {
                var res = await p.CompleteAsync(cr, ct);
                if (string.IsNullOrWhiteSpace(res.Text))
                    throw new InvalidOperationException($"AI trả rỗng (finish={res.FinishReason})");
                var ok = Parse(res.Text);
                _cache.Save(key, ok);
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                last = ex;
                _log.LogWarning("Bóc tour lần {N} lỗi: {Msg}", attempt, ex.Message);
            }
        }
        throw last ?? new InvalidOperationException("Bóc tour thất bại");
    }

    private static string BuildPrompt(string text) => $@"NHIỆM VỤ: Đọc mô tả Sale gửi dưới đây và trả JSON Draft Tour GIT.

MÔ TẢ CỦA SALE:
{text.Trim()}

QUY TẮC:
1. Chỉ dùng thông tin trong mô tả, KHÔNG bịa
2. Field không rõ → null / 0 / [] và ghi 'warnings'
3. Ngày: chuyển về dạng yyyy-MM-dd (vd 'đi 15/7' → '2026-07-15', dùng năm hiện tại 2026 nếu không nói rõ)
4. Số liệu tiền: bóc nguyên số đồng (vd '5 triệu' → 5000000, '1.2tr' → 1200000)
5. tourType: 'Nội địa' | 'Inbound' (khách nước ngoài đến VN) | 'Outbound' (VN đi nước ngoài). Suy từ điểm đến.
6. expenses (Phần thu) = khoản tiền Sale thu của khách: vé tour, phụ thu, bảo hiểm...
7. services (Dịch vụ điều hành) = chi phí trả NCC: khách sạn, xe, ăn, vé tham quan...

OUTPUT JSON (KHÔNG markdown):
{{
  ""title"": ""tên tour ngắn gọn, vd 'Đà Nẵng - Hội An 3N2Đ'"",
  ""marketName"": ""tên thị trường hoặc null"",
  ""tourType"": ""Nội địa|Inbound|Outbound|null"",
  ""startDate"": ""yyyy-MM-dd hoặc null"",
  ""endDate"": ""yyyy-MM-dd hoặc null"",
  ""adultCount"": 0,
  ""childCount"": 0,
  ""customerName"": ""tên khách đại diện hoặc null"",
  ""customerPhone"": ""SĐT hoặc null"",
  ""customerEmail"": ""email hoặc null"",
  ""note"": ""ghi chú/yêu cầu đặc biệt hoặc null"",
  ""expenses"": [{{ ""title"": ""Vé tour người lớn"", ""unitPrice"": 5000000, ""quantity"": 18, ""vatPercent"": 8 }}],
  ""services"": [{{ ""name"": ""Khách sạn 3 sao"", ""providerName"": ""Khách sạn ABC"", ""quantity"": 10, ""nights"": 2, ""netPrice"": 800000, ""vatPercent"": 8 }}],
  ""warnings"": [""điều cần Sale làm rõ thêm""]
}}
Bắt đầu trả JSON ngay:";

    private TourBuilderDraft Parse(string raw)
    {
        try
        {
            using var doc = LooseJson.ParseFirstObject(raw);
            var root = doc.RootElement;
            return new TourBuilderDraft(
                Title:         Str(root, "title"),
                MarketName:    Str(root, "marketName"),
                TourType:      Str(root, "tourType"),
                StartDate:     Str(root, "startDate"),
                EndDate:       Str(root, "endDate"),
                AdultCount:    IntN(root, "adultCount"),
                ChildCount:    IntN(root, "childCount"),
                CustomerName:  Str(root, "customerName"),
                CustomerPhone: Str(root, "customerPhone"),
                CustomerEmail: Str(root, "customerEmail"),
                Note:          Str(root, "note"),
                Expenses:      ParseExpenses(root),
                Services:      ParseServices(root),
                Warnings:      StrList(root, "warnings"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse tour draft JSON lỗi. Raw: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI trả kết quả không đúng định dạng");
        }
    }

    private static List<TourBuilderExpense> ParseExpenses(JsonElement root)
    {
        var list = new List<TourBuilderExpense>();
        if (!TryGet(root, "expenses", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var title = Str(e, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new TourBuilderExpense(title!, Long(e, "unitPrice"), Math.Max(0, Int(e, "quantity")), Dbl(e, "vatPercent")));
        }
        return list;
    }

    private static List<TourBuilderServiceItem> ParseServices(JsonElement root)
    {
        var list = new List<TourBuilderServiceItem>();
        if (!TryGet(root, "services", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var name = Str(e, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new TourBuilderServiceItem(
                Name: name!, ProviderName: Str(e, "providerName"),
                Quantity: Math.Max(0, Int(e, "quantity")), Nights: Math.Max(0, Int(e, "nights")),
                NetPrice: Long(e, "netPrice"), VatPercent: Dbl(e, "vatPercent")));
        }
        return list;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────
    private static bool TryGet(JsonElement el, string name, out JsonElement v)
    {
        v = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var pr in el.EnumerateObject())
            if (string.Equals(pr.Name, name, StringComparison.OrdinalIgnoreCase)) { v = pr.Value; return true; }
        return false;
    }
    private static string? Str(JsonElement el, string name)
        => TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;
    private static int Int(JsonElement el, string name)
    {
        if (!TryGet(el, name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var dd)) return (int)dd;
        return 0;
    }
    private static int? IntN(JsonElement el, string name) => TryGet(el, name, out _) ? Int(el, name) : (int?)null;
    private static long Long(JsonElement el, string name)
    {
        if (!TryGet(el, name, out var p) || p.ValueKind != JsonValueKind.Number) return 0;
        return p.TryGetInt64(out var n) ? n : (long)p.GetDouble();
    }
    private static double Dbl(JsonElement el, string name)
        => TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
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
