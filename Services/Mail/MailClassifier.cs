using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

/// Phân loại 1 email vào 6 nhóm + tóm tắt 1 câu, qua `Models:Review` (DeepSeek deepseek-chat).
///
/// Task nhẹ chạy nhiều mỗi sync → đi đường JSON-prompt + tolerant parse với model rẻ.
/// KHÔNG dùng Claude/Anthropic (tốn). Đổi provider/model qua appsettings:
///   "Models:Review": { "Provider": "deepseek", "Model": "deepseek-chat", "ApiKey": "sk-..." }
///
/// Lỗi → ("khac", "") để mail vẫn lưu, NV xem bằng tay.
public class MailClassifier
{
    private readonly ProviderRegistry _registry;
    private readonly ModelDefaults _defaults;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<MailClassifier> _log;

    private const string SystemJsonPrompt =
        "Bạn là bộ phân loại email cho một công ty du lịch. " +
        "Đọc email và CHỌN ĐÚNG 1 nhóm + tóm tắt 1 câu ngắn bằng tiếng Việt. " +
        "Output ONLY raw JSON, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Ký tự đầu tiên BẮT BUỘC là '{'.";

    public MailClassifier(ProviderRegistry registry,
        ModelDefaults defaults,
        IWorkflowTraceAccessor trace, ILogger<MailClassifier> log)
    {
        _registry = registry; _defaults = defaults; _trace = trace; _log = log;
    }

    /// Gọi AI phân loại 1 email → (categoryKey đã chuẩn hóa, summary). Lỗi → (khac, "").
    public async Task<(string Category, string Summary)> ClassifyAsync(MailItem mail, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("MailClassifier");
        trace?.SetMeta("mailId", mail.Id);
        trace?.SetMeta("subject", mail.Subject);

        var provider = _registry.Resolve(_defaults.Review.Provider);
        trace?.SetMeta("provider", provider.Id);
        trace?.SetMeta("model", _defaults.Review.Model);

        try
        {
            var req = new CompleteRequest(
                Prompt:      BuildPromptJson(mail),
                Provider:    provider.Id,
                Model:       _defaults.Review.Model,
                MaxTokens:   1000, Temperature: 0.1,
                System:      SystemJsonPrompt,
                ApiKey:      _defaults.Review.ApiKey);

            var aiTimer = trace?.Begin("ai_classify");
            var result = await provider.CompleteAsync(req, ct);
            var parsed = ParseClassification(result.Text);
            aiTimer?.Done("ok",
                $"Provider {provider.Id} → category={parsed.Category}, tokens {result.InputTokens}/{result.OutputTokens}, {result.LatencyMs}ms",
                new() {
                    ["provider"] = provider.Id, ["model"] = result.Model,
                    ["tokIn"] = result.InputTokens, ["tokOut"] = result.OutputTokens,
                    ["latencyMs"] = result.LatencyMs,
                    ["category"] = parsed.Category, ["summary"] = parsed.Summary,
                    ["responseSnippet"] = result.Text.Length > 300 ? result.Text[..300] + "…" : result.Text
                });
            return parsed;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Phân loại email {Id} lỗi → khac", mail.Id);
            return ("khac", "");
        }
    }

    // ─── Prompt builder ──────────────────────────────────────────────────────────
    private static string BuildPromptJson(MailItem mail)
    {
        var cats = string.Join("\n", MailTaxonomy.Categories.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var body = mail.Body.Length > 2000 ? mail.Body[..2000] + " …(cắt)" : mail.Body;
        return $@"PHÂN LOẠI EMAIL SAU vào ĐÚNG 1 nhóm:

CÁC NHÓM:
{cats}

EMAIL:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {body}

OUTPUT JSON (key category dùng ĐÚNG mã nhóm ở trên):
{{ ""category"": ""<mã nhóm>"", ""summary"": ""tóm tắt 1 câu ngắn"" }}

Trả JSON ngay:";
    }

    // ─── Parser ─────────────────────────────────────────────────────────────────
    /// Parse output AI dạng raw text → (category chuẩn hóa, summary). Pure, không I/O → test được.
    /// Giữ signature legacy cho TourkitAiProxy.Tests.MailClassifierTests.
    public static (string Category, string Summary) ParseClassification(string raw)
    {
        var json = LooseJson.ExtractFirstObject(raw);
        if (json == null) return ("khac", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cat = MailTaxonomy.NormalizeCategory(GetStr(root, "category"));
            var sum = GetStr(root, "summary") ?? "";
            return (cat, sum.Trim());
        }
        catch
        {
            return ("khac", "");
        }
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }
}
