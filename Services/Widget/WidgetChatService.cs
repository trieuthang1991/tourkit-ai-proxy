using System.Text;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Widget;

/// <summary>
/// Chat đơn cho Widget — KHÔNG gọi CRM, KHÔNG cần session TourKit. Chỉ Q&A theo SystemPrompt.
/// Resolve provider = Models:Primary default (Anthropic Haiku trong appsettings). Tenant context
/// được Push trước khi gọi provider → quota check + AI usage log gắn đúng tenant của token.
/// </summary>
public class WidgetChatService
{
    private const int MAX_HISTORY = 12;          // 6 cặp (user/assistant) gần nhất
    private const int MAX_TOKENS = 1024;         // câu trả lời chat ngắn — bot, không dài dòng
    private const double TEMP = 0.5;             // hơi sáng tạo nhưng không lạc đề

    private readonly ProviderRegistry _registry;
    private readonly AiModelRegistry _modelRegistry;
    private readonly WidgetTokenRepository _repo;
    private readonly AiCallContext _ctx;
    private readonly ILogger<WidgetChatService> _log;

    public WidgetChatService(
        ProviderRegistry registry,
        AiModelRegistry modelRegistry,
        WidgetTokenRepository repo,
        AiCallContext ctx,
        ILogger<WidgetChatService> log)
    {
        _registry = registry; _modelRegistry = modelRegistry; _repo = repo; _ctx = ctx; _log = log;
    }

    public record ChatResult(string Reply, string Provider, string Model, long LatencyMs, int InTok, int OutTok);

    public async Task<ChatResult> ChatAsync(WidgetToken token, string message, List<WidgetChatMessage>? history,
        List<string>? images, List<string>? documents, CancellationToken ct)
    {
        var (prompt, system) = BuildPrompt(token, message, history);
        var resolved = _modelRegistry.Resolve(AiFeature.Widget);
        var provider = _registry.Resolve(resolved.Provider);

        using var tenantScope = _ctx.Push("widget", token.TenantId);
        var req = new CompleteRequest(
            Prompt: prompt, Provider: provider.Id, Model: resolved.Model,
            MaxTokens: MAX_TOKENS, Temperature: TEMP, System: system, ApiKey: resolved.ApiKey,
            Images: images, Documents: documents);
        var res = await provider.CompleteAsync(req, ct);

        _ = _repo.IncrementMessagesAsync(token.Token, CancellationToken.None);
        return new ChatResult(res.Text, provider.Id, res.Model, res.LatencyMs, res.InputTokens, res.OutputTokens);
    }

    /// Streaming variant — onDelta cho từng chunk text. Final usage trả qua return value.
    public async Task<ChatResult> ChatStreamAsync(
        WidgetToken token, string message, List<WidgetChatMessage>? history,
        List<string>? images, List<string>? documents,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        var (prompt, system) = BuildPrompt(token, message, history);
        var resolved = _modelRegistry.Resolve(AiFeature.Widget);
        var provider = _registry.Resolve(resolved.Provider);

        using var tenantScope = _ctx.Push("widget", token.TenantId);
        var req = new CompleteRequest(
            Prompt: prompt, Provider: provider.Id, Model: resolved.Model,
            MaxTokens: MAX_TOKENS, Temperature: TEMP, System: system, ApiKey: resolved.ApiKey,
            Images: images, Documents: documents);
        var res = await provider.StreamAsync(req, onDelta, ct);

        _ = _repo.IncrementMessagesAsync(token.Token, CancellationToken.None);
        return new ChatResult(res.Text, provider.Id, res.Model, res.LatencyMs, res.InputTokens, res.OutputTokens);
    }

    // System prompt: tenant config + guardrail (KHÔNG trả số liệu CRM, KHÔNG nhận lệnh ngoài scope).
    // Flatten history thành prompt continuation — đơn giản, support mọi provider.
    private (string prompt, string system) BuildPrompt(WidgetToken token, string message, List<WidgetChatMessage>? history)
    {
        var system = new StringBuilder();
        system.Append(token.SystemPrompt.Trim());
        system.Append("\n\nQUY TẮC TRẢ LỜI:\n");
        system.Append("- Trả lời bằng tiếng Việt, ngắn gọn, lịch sự, gọi khách bằng \"Anh/Chị\".\n");
        system.Append("- Nếu khách hỏi ngoài phạm vi dịch vụ, lịch sự gợi ý liên hệ tư vấn viên.\n");
        system.Append("- KHÔNG bịa số liệu/giá tour cụ thể — nếu không chắc, mời khách để lại liên hệ.\n");
        system.Append("- Trình bày dễ đọc: TÁCH ĐOẠN bằng dòng trống (\\n\\n), dùng \"-\" đầu dòng cho danh sách. KHÔNG dùng markdown (**, ##). KHÔNG dồn 1 khối dính nhau.\n");

        var trimmed = (history ?? new()).TakeLast(MAX_HISTORY).ToList();
        var p = new StringBuilder();
        foreach (var m in trimmed)
        {
            var role = m.Role?.ToLowerInvariant() == "assistant" ? "Bot" : "Khách";
            p.AppendLine($"{role}: {m.Content?.Trim()}");
        }
        p.AppendLine($"Khách: {message.Trim()}");
        p.Append("Bot:");
        return (p.ToString(), system.ToString());
    }
}
