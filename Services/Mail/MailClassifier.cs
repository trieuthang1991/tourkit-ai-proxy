using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

/// Phân loại 1 email vào 6 nhóm + tóm tắt 1 câu.
///
/// Dual-path (mirror Visa/Deal/Tour):
/// - Provider Anthropic + có key  → NATIVE function-calling (submit_mail_classification)
/// - Mọi provider khác            → JSON-prompt + tolerant parse (legacy, fallback)
///
/// Lỗi cả 2 path → ("khac", "") để mail vẫn lưu được, NV xem bằng tay.
public class MailClassifier
{
    private readonly ProviderRegistry _registry;
    private readonly NativeToolScorer _native;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<MailClassifier> _log;

    private const string SystemJsonPrompt =
        "Bạn là bộ phân loại email cho một công ty du lịch. " +
        "Đọc email và CHỌN ĐÚNG 1 nhóm + tóm tắt 1 câu ngắn bằng tiếng Việt. " +
        "Output ONLY raw JSON, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Ký tự đầu tiên BẮT BUỘC là '{'.";

    private const string SystemNativeTool =
        "Bạn là bộ phân loại email cho một công ty du lịch. " +
        "Đọc email và gọi tool submit_mail_classification với 1 nhóm + tóm tắt 1 câu tiếng Việt.";

    public MailClassifier(ProviderRegistry registry,
        NativeToolScorer native,
        IWorkflowTraceAccessor trace, ILogger<MailClassifier> log)
    {
        _registry = registry; _native = native; _trace = trace; _log = log;
    }

    /// Gọi AI phân loại 1 email → (categoryKey đã chuẩn hóa, summary). Lỗi → (khac, "").
    public async Task<(string Category, string Summary)> ClassifyAsync(MailItem mail, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("MailClassifier");
        trace?.SetMeta("mailId", mail.Id);
        trace?.SetMeta("subject", mail.Subject);

        var provider = _registry.Resolve(null);
        trace?.SetMeta("provider", provider.Id);

        try
        {
            if (string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                trace?.Step("path_dispatch", "ok", 0,
                    "Provider anthropic → native function-calling",
                    new() { ["path"] = "native-tool", ["tool"] = "submit_mail_classification" });
                return await ClassifyWithNativeToolAsync(mail, trace, ct);
            }
            trace?.Step("path_dispatch", "ok", 0,
                $"Provider {provider.Id} → JSON-prompt fallback",
                new() { ["path"] = "json-prompt" });
            return await ClassifyWithJsonPromptAsync(provider, mail, trace, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Phân loại email {Id} lỗi → khac", mail.Id);
            return ("khac", "");
        }
    }

    // ─── Native function-calling path (Anthropic) ─────────────────────────────────
    private async Task<(string Category, string Summary)> ClassifyWithNativeToolAsync(
        MailItem mail, TraceCollector? trace, CancellationToken ct)
    {
        var schema = BuildMailClassificationSchema();
        var userPrompt = BuildPromptNative(mail);

        var res = await _native.RunAsync<(string Category, string Summary)>(
            systemPrompt:     SystemNativeTool,
            userPrompt:       userPrompt,
            toolSchema:       schema,
            terminalToolName: "submit_mail_classification",
            parser:           ParseToolInput,
            apiKeyOverride:   null,           // mail classification dùng key server-side (config)
            model:            "claude-haiku-4-5",   // task đơn giản, dùng haiku rẻ + nhanh
            maxTokens:        500,
            trace:            trace,
            ct:               ct);

        return res.Value;
    }

    // ─── JSON-prompt path (fallback) ──────────────────────────────────────────────
    private async Task<(string Category, string Summary)> ClassifyWithJsonPromptAsync(
        IAiProvider provider, MailItem mail, TraceCollector? trace, CancellationToken ct)
    {
        var req = new CompleteRequest(
            Prompt:      BuildPromptJson(mail),
            Provider:    null, Model: null,
            MaxTokens:   1000, Temperature: 0.1,
            System:      SystemJsonPrompt, ApiKey: null);

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

    // ─── Prompt builders ─────────────────────────────────────────────────────────
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

    private static string BuildPromptNative(MailItem mail)
    {
        var cats = string.Join("\n", MailTaxonomy.Categories.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var body = mail.Body.Length > 2000 ? mail.Body[..2000] + " …(cắt)" : mail.Body;
        return $@"PHÂN LOẠI EMAIL SAU và GỌI TOOL submit_mail_classification:

CÁC NHÓM (bắt buộc chọn 1):
{cats}

EMAIL:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {body}

Gọi submit_mail_classification NGAY.";
    }

    // ─── Schema cho native tool ─────────────────────────────────────────────────
    private static JsonElement BuildMailClassificationSchema()
        => NativeToolScorer.BuildAnthropicTool(
            name: "submit_mail_classification",
            description: "Nộp kết quả phân loại email. Gọi DUY NHẤT 1 lần.",
            properties: new
            {
                category = new
                {
                    type = "string",
                    @enum = MailTaxonomy.Categories.Keys.ToArray(),
                    description = "Mã nhóm email (1 trong 6 categories)"
                },
                summary = new
                {
                    type = "string",
                    description = "Tóm tắt 1 câu ngắn tiếng Việt"
                }
            },
            required: new[] { "category", "summary" });

    // ─── Parsers ────────────────────────────────────────────────────────────────
    /// Parse tool_use input → chuẩn hóa qua MailTaxonomy.
    private static (string Category, string Summary) ParseToolInput(JsonElement root)
    {
        var cat = MailTaxonomy.NormalizeCategory(GetStr(root, "category"));
        var sum = GetStr(root, "summary") ?? "";
        return (cat, sum.Trim());
    }

    /// Parse output AI dạng raw text → (category chuẩn hóa, summary). Pure, không I/O → test được.
    /// Giữ signature legacy cho TourkitAiProxy.Tests.MailClassifierTests.
    public static (string Category, string Summary) ParseClassification(string raw)
    {
        var json = LooseJson.ExtractFirstObject(raw);
        if (json == null) return ("khac", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseToolInput(doc.RootElement);
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
