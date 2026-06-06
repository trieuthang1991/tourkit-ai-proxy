using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Visa;

/// Bước 1 — AI VISION đọc từng file hồ sơ (ảnh/PDF→ảnh) → trích loại giấy tờ + dữ kiện chính.
/// Chạy SONG SONG (cap 4). Cần provider có vision (OpenAI/Anthropic) — provider khác báo lỗi rõ.
public class VisaExtractionService
{
    private const int Concurrency = 4;
    private static readonly HashSet<string> VisionProviders = new(StringComparer.OrdinalIgnoreCase) { "openai", "anthropic" };

    private readonly ProviderRegistry _registry;
    private readonly ILogger<VisaExtractionService> _log;

    private const string SYSTEM =
        "Bạn là chuyên viên đọc hồ sơ xin visa. Xem ảnh giấy tờ và TRÍCH thông tin thành JSON. " +
        "CHỈ trả JSON thuần (bắt đầu bằng '{'), KHÔNG markdown, KHÔNG giải thích. " +
        "KHÔNG bịa thông tin không thấy trong ảnh. Nếu ảnh mờ/không đọc được, đặt readable=false.";

    public VisaExtractionService(ProviderRegistry registry, ILogger<VisaExtractionService> log)
    {
        _registry = registry; _log = log;
    }

    public record UploadFile(string FileName, string DataUrl);

    /// Đọc toàn bộ file → VisaExtraction (gồm per-file + profile gộp) + (applicantName, country) suy ra.
    public async Task<(VisaExtraction Extraction, string? Name, string? Country)> ExtractAsync(
        IReadOnlyList<UploadFile> files, string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var p = _registry.Resolve(provider);
        if (!VisionProviders.Contains(p.Id))
            throw new InvalidOperationException(
                "Tính năng đọc ảnh hồ sơ cần ChatGPT (OpenAI) hoặc Claude (Anthropic). " +
                "Mở 'Cấu hình AI', chọn một trong hai và nhập API key rồi thử lại.");

        var results = new VisaFileExtraction[files.Count];
        var names = new ConcurrentBag<string>();
        var countries = new ConcurrentBag<string>();

        using var sem = new SemaphoreSlim(Concurrency);
        var tasks = files.Select(async (f, idx) =>
        {
            await sem.WaitAsync(ct);
            try { results[idx] = await ExtractOneAsync(p, f, model, apiKey, names, countries, ct); }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        var profile = BuildProfile(results);
        var extraction = new VisaExtraction(profile, results.ToList());
        return (extraction, names.FirstOrDefault(), countries.FirstOrDefault());
    }

    private async Task<VisaFileExtraction> ExtractOneAsync(
        IAiProvider provider, UploadFile f, string? model, string? apiKey,
        ConcurrentBag<string> names, ConcurrentBag<string> countries, CancellationToken ct)
    {
        var req = new CompleteRequest(
            Prompt: PROMPT, Provider: provider.Id, Model: model,
            MaxTokens: 1200, Temperature: 0.1, System: SYSTEM, ApiKey: apiKey,
            Images: new[] { f.DataUrl });

        try
        {
            var res = await provider.CompleteAsync(req, ct);
            using var doc = LooseJson.ParseFirstObject(res.Text);
            var root = doc.RootElement;

            var name = Str(root, "applicantName");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            var country = Str(root, "country");
            if (!string.IsNullOrWhiteSpace(country)) countries.Add(country!);

            var facts = StrList(root, "keyFacts");
            var summary = Str(root, "summary") ?? "";
            if (facts.Count > 0) summary = string.IsNullOrWhiteSpace(summary)
                ? string.Join("; ", facts) : summary + " — " + string.Join("; ", facts);

            return new VisaFileExtraction(
                FileName:     f.FileName,
                DocType:      (Str(root, "docType") ?? "unknown").Trim(),
                DocTypeLabel: Str(root, "docTypeLabel") ?? "Không xác định",
                Summary:      summary.Trim(),
                Readable:     Bool(root, "readable", true),
                Note:         Str(root, "note"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Đọc file {File} lỗi", f.FileName);
            return new VisaFileExtraction(f.FileName, "unknown", "Không đọc được",
                "", false, "AI không đọc được file này: " + ex.Message);
        }
    }

    // Gộp các file → 1 bản hồ sơ text cho NV rà soát + model chấm điểm.
    private static string BuildProfile(IReadOnlyList<VisaFileExtraction> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.Append("• ").Append(f.DocTypeLabel);
            if (!f.Readable) { sb.Append(" (KHÔNG đọc được)\n"); continue; }
            if (!string.IsNullOrWhiteSpace(f.Summary)) sb.Append(": ").Append(f.Summary);
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    private const string PROMPT = @"Đọc ảnh giấy tờ trong hồ sơ xin visa và trả JSON:
{
  ""docType"": ""passport|id_card|bank_statement|salary|employment|business|property|tour_booking|invitation|photo|other|unknown"",
  ""docTypeLabel"": ""tên loại giấy tờ bằng tiếng Việt (vd 'Hộ chiếu', 'Sao kê ngân hàng', 'Hợp đồng lao động')"",
  ""applicantName"": ""họ tên đương đơn nếu thấy (vd trên hộ chiếu), nếu không có để chuỗi rỗng"",
  ""country"": ""nước xin visa nếu suy ra được, nếu không để rỗng"",
  ""readable"": true,
  ""summary"": ""1-2 câu mô tả nội dung chính của giấy tờ này"",
  ""keyFacts"": [""dữ kiện quan trọng cho thẩm định visa: số dư tài khoản, thu nhập/tháng, chức vụ, thời hạn hộ chiếu, lịch sử xuất cảnh...""],
  ""note"": null
}
Bắt đầu trả JSON ngay:";

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
    private static bool Bool(JsonElement el, string name, bool dflt)
        => TryGet(el, name, out var p) ? p.ValueKind switch
            { JsonValueKind.True => true, JsonValueKind.False => false, _ => dflt } : dflt;
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
