// Services/Reviews/Agents/JsonPromptReviewAgent.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews.Agents;

/// <summary>
/// Fallback agent: prompt-JSON + tolerant parse. Hoạt động cho MỌI provider
/// (OpenCode/9routes/OpenAI/Anthropic) — đây là pattern hiện hành trước khi tách runtime.
///
/// Trade-off so với NativeToolReviewAgent: AI có thể leak thinking/markdown → cần parser tolerant.
/// Bù lại: không phụ thuộc native function-calling API, chạy được trên reasoning models.
/// </summary>
public class JsonPromptReviewAgent : IReviewAgent
{
    private readonly ProviderRegistry _registry;
    private readonly ILogger<JsonPromptReviewAgent> _log;

    public JsonPromptReviewAgent(ProviderRegistry registry, ILogger<JsonPromptReviewAgent> log)
    {
        _registry = registry; _log = log;
    }

    /// Always true — đây là fallback cuối cùng, đăng ký SAU NativeToolReviewAgent ở DI.
    public bool Supports(string providerId) => true;

    public async Task<ReviewAgentResult> RunAsync(
        Customer customer, string fingerprint, string tenantId,
        string? providerOverride, string? modelOverride, string? apiKeyOverride,
        Func<string, string?, Task>? onStage,
        TraceCollector? trace,
        CancellationToken ct)
    {
        async Task Stage(string stage, string? delta = null)
        {
            if (onStage != null) await onStage(stage, delta);
        }

        await Stage("preparing");
        var prompt = ReviewPrompt.BuildUserPromptForJson(customer);
        var provider = _registry.Resolve(providerOverride);
        trace?.SetMeta("agent", "JsonPromptReviewAgent");
        trace?.SetMeta("provider", provider.Id);
        trace?.Step("prepare_prompt", "ok", 0,
            $"Build prompt KH {customer.Name} ({customer.Id}), {prompt.Length:N0} chars",
            new() { ["promptChars"] = prompt.Length, ["systemChars"] = ReviewPrompt.SystemForJsonPrompt.Length });

        var req = new CompleteRequest(
            Prompt:      prompt,
            Provider:    providerOverride,
            Model:       modelOverride,
            ApiKey:      apiKeyOverride,
            MaxTokens:   8000,
            Temperature: 0.4,
            System:      ReviewPrompt.SystemForJsonPrompt
        );

        await Stage("calling");
        var aiTimer = trace?.Begin("ai_complete");

        // Buffered (không stream) vì reasoning models trộn reasoning_content + content,
        // stream sẽ làm parser fail. Stage indicator vẫn cho UI lifecycle.
        var result = await provider.CompleteAsync(req, ct);
        aiTimer?.Done("ok",
            $"Provider {provider.Id} → tokens {result.InputTokens}/{result.OutputTokens}, {result.LatencyMs}ms, {result.Text.Length:N0} chars JSON",
            new() {
                ["provider"] = provider.Id, ["model"] = result.Model,
                ["tokIn"] = result.InputTokens, ["tokOut"] = result.OutputTokens,
                ["latencyMs"] = result.LatencyMs,
                ["responseChars"] = result.Text.Length,
                ["finishReason"] = result.FinishReason,
                ["responseSnippet"] = result.Text.Length > 400 ? result.Text[..400] + "…" : result.Text
            });

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            trace?.Step("ai_complete", "fail", 0, $"AI trả text rỗng (finish={result.FinishReason})");
            throw new InvalidOperationException(
                $"AI trả text rỗng cho KH {customer.Id} (finish={result.FinishReason})");
        }

        await Stage("parsing");
        var parseTimer = trace?.Begin("parse_json");
        var parsed = ReviewPrompt.ParseRawText(result.Text);
        parseTimer?.Done("ok",
            $"Parse JSON thành công → rank={parsed.Rank ?? "?"}, strengths={parsed.Strengths?.Count ?? 0}, concerns={parsed.Concerns?.Count ?? 0}",
            new() {
                ["rank"] = parsed.Rank,
                ["alertHas"] = parsed.Alert != null,
                ["strengthsCount"] = parsed.Strengths?.Count ?? 0,
                ["concernsCount"] = parsed.Concerns?.Count ?? 0
            });

        var review = ReviewPrompt.Compose(parsed, customer, fingerprint,
            aiProvider: provider.Id, aiModel: result.Model,
            tokensIn: result.InputTokens, tokensOut: result.OutputTokens);

        return new ReviewAgentResult(
            Review:      review,
            AiProvider:  provider.Id,
            AiModel:     result.Model,
            TokensIn:    result.InputTokens,
            TokensOut:   result.OutputTokens,
            Warning:     null);
    }
}
