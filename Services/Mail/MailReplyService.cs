using System.Text.RegularExpressions;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

/// Soạn email bằng AI: (1) TRẢ LỜI 1 email theo ngữ điệu + chỉ thị, (2) SOẠN MỚI từ brief.
/// Stream token ra qua `onDelta` (mẫu ChatAgentService.AskStreamAsync). Chèn chữ ký công ty.
public class MailReplyService
{
    private readonly ProviderRegistry _registry;
    private readonly AiModelRegistry _modelRegistry;
    private readonly MailRepository _repo;
    private readonly MailAccountStore _account;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<MailReplyService> _log;

    private const string SYSTEM =
        "Bạn là nhân viên chăm sóc khách hàng của một công ty du lịch, soạn email bằng tiếng Việt. " +
        "Viết email hoàn chỉnh, đúng ngữ điệu yêu cầu, tự nhiên và chuyên nghiệp. " +
        "BẮT ĐẦU NGAY bằng lời chào — TUYỆT ĐỐI KHÔNG viết phần suy nghĩ/phân tích/giải thích trước hay sau email. " +
        "Bám đúng nội dung email khách; KHÔNG bịa thông tin chưa có (giá cụ thể, lịch trình, khuyến mãi) trừ khi có trong chỉ thị nhân viên. " +
        "CHỈ trả nội dung email (lời chào + thân bài + ký tên), KHÔNG markdown, KHÔNG bọc trong dấu nháy.";

    public MailReplyService(ProviderRegistry registry, AiModelRegistry modelRegistry, MailRepository repo,
        MailAccountStore account, IWorkflowTraceAccessor trace, ILogger<MailReplyService> log)
    {
        _registry = registry; _modelRegistry = modelRegistry; _repo = repo;
        _account = account; _trace = trace; _log = log;
    }

    /// Trả lời 1 email. Stream nháp; trả text đầy đủ; lưu nháp + status dang_xu_ly.
    public async Task<string> DraftStreamAsync(
        string tenantId, string username, MailItem mail, DraftReplyRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("MailReply");
        trace?.SetMeta("mailId", mail.Id);
        trace?.SetMeta("tone", req.Tone);
        trace?.SetMeta("hasInstruction", !string.IsNullOrWhiteSpace(req.Instruction));

        // Resolve qua AiModelRegistry → caller có thể omit provider/model/apiKey, đọc từ Models:MailDraft.
        var resolved = _modelRegistry.Resolve(AiFeature.MailDraft, req.Provider, req.Model);
        req = req with {
            Provider = resolved.Provider,
            Model    = resolved.Model,
            ApiKey   = req.ApiKey ?? resolved.ApiKey
        };

        var text = await RunAsync(
            BuildReplyPrompt(tenantId, username, mail, MailTaxonomy.ToneLabel(req.Tone), req.Instruction),
            req.Provider, req.Model, req.ApiKey, onDelta, ct);

        if (text.Length > 0)
        {
            _repo.SetDraft(tenantId, mail.Id, new MailDraft(req.Tone, req.Instruction, text, DateTime.UtcNow.ToString("o")), status: "dang_xu_ly");
            trace?.Step("save_draft", "ok", 0, $"Lưu nháp + đổi status sang 'dang_xu_ly'");
        }
        return text;
    }

    /// Soạn email MỚI từ brief (người nhận + ý chính). Stream; trả text đầy đủ. KHÔNG lưu repo.
    public Task<string> ComposeNewStreamAsync(
        string tenantId, string username, ComposeDraftRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("MailCompose");
        trace?.SetMeta("to", req.To);
        trace?.SetMeta("subject", req.Subject);
        trace?.SetMeta("tone", req.Tone);

        // Resolve qua AiModelRegistry → caller có thể omit provider/model/apiKey, đọc từ Models:MailCompose.
        var resolved = _modelRegistry.Resolve(AiFeature.MailCompose, req.Provider, req.Model);
        req = req with {
            Provider = resolved.Provider,
            Model    = resolved.Model,
            ApiKey   = req.ApiKey ?? resolved.ApiKey
        };

        return RunAsync(BuildComposePrompt(tenantId, username, req, MailTaxonomy.ToneLabel(req.Tone)),
                    req.Provider, req.Model, req.ApiKey, onDelta, ct);
    }

    // ─── Lõi gọi provider + stream ───────────────────────────────────────────────
    private async Task<string> RunAsync(string prompt, string? provider, string? model, string? apiKey,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        var trace = _trace.Current;
        var p = _registry.Resolve(provider);
        var req = new CompleteRequest(prompt, provider, model, 2000, 0.6, SYSTEM, apiKey);

        var aiTimer = trace?.Begin("ai_draft");
        // BUFFERED (không stream): reasoning model (minimax/deepseek) khi stream hay rò 'lời suy nghĩ'
        // (reasoning_content) lẫn vào nháp. CompleteAsync chỉ trả message.content sạch (mẫu ReviewService).
        var result = await p.CompleteAsync(req, ct);
        var text = CleanDraft(result.Text);
        aiTimer?.Done("ok",
            $"Provider {p.Id} → soạn email {text.Length} chars, tokens {result.InputTokens}/{result.OutputTokens}, {result.LatencyMs}ms",
            new() {
                ["provider"] = p.Id, ["model"] = result.Model,
                ["promptChars"] = prompt.Length, ["systemChars"] = SYSTEM.Length,
                ["tokIn"] = result.InputTokens, ["tokOut"] = result.OutputTokens,
                ["latencyMs"] = result.LatencyMs,
                ["replyChars"] = text.Length,
                ["replySnippet"] = text.Length > 300 ? text[..300] + "…" : text
            });
        if (text.Length > 0) await onDelta(text);   // đẩy 1 lần để UI hiện ngay
        return text;
    }

    // Làm sạch nháp: bỏ fence markdown / nháy bao ngoài, trim.
    private static string CleanDraft(string raw)
    {
        var s = (raw ?? "").Trim();
        s = Regex.Replace(s, "^```[a-zA-Z]*\\s*", "");
        s = Regex.Replace(s, "\\s*```$", "");
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1];
        return s.Trim();
    }

    // Chỉ thị ký tên: nếu user đã đặt chữ ký riêng → ký đúng tên đó; nếu chưa →
    // ký trung tính "Trân trọng," KHÔNG được bịa tên công ty/thương hiệu.
    private string SignatureLine(string tenantId, string username) =>
        _account.HasSignature(tenantId, username)
            ? $"KÝ TÊN CUỐI EMAIL ĐÚNG BẰNG: {_account.Signature(tenantId, username)}"
            : "KẾT THÚC EMAIL bằng lời chào \"Trân trọng,\" rồi xuống dòng — TUYỆT ĐỐI KHÔNG tự bịa tên công ty, thương hiệu hay tên người ký.";

    private string BuildReplyPrompt(string tenantId, string username, MailItem mail, string toneLabel, string? instruction)
    {
        var instr = string.IsNullOrWhiteSpace(instruction) ? "(không có)" : instruction!.Trim();
        return $@"EMAIL CỦA KHÁCH:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {mail.Body}

NGỮ ĐIỆU YÊU CẦU: {toneLabel}
CHỈ THỊ THÊM CỦA NHÂN VIÊN: {instr}
{SignatureLine(tenantId, username)}

Soạn email trả lời hoàn chỉnh:";
    }

    private string BuildComposePrompt(string tenantId, string username, ComposeDraftRequest req, string toneLabel)
    {
        var subj = string.IsNullOrWhiteSpace(req.Subject) ? "(tự đề xuất tiêu đề phù hợp trong thân bài nếu cần)" : req.Subject!.Trim();
        return $@"SOẠN EMAIL MỚI gửi khách hàng.
Người nhận: {req.To}
Tiêu đề dự kiến: {subj}
Ý CHÍNH CẦN TRUYỀN ĐẠT: {req.Brief}

NGỮ ĐIỆU: {toneLabel}
{SignatureLine(tenantId, username)}

Soạn nội dung email hoàn chỉnh:";
    }
}
