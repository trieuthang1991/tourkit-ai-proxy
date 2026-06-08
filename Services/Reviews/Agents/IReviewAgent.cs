// Services/Reviews/Agents/IReviewAgent.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews.Agents;

/// <summary>
/// Strategy pattern: ReviewService dispatch tới agent phù hợp với provider hiện hành.
/// - Anthropic (sonnet-4-5, haiku-4-5) → NativeToolReviewAgent (schema enforce + multi-step)
/// - OpenAI / OpenCode / 9routes / Anthropic-only-cheap → JsonPromptReviewAgent (prompt JSON + tolerant parse)
/// </summary>
public interface IReviewAgent
{
    /// <summary>True nếu agent xử lý được provider này.</summary>
    bool Supports(string providerId);

    /// <summary>
    /// Sinh review theo customer. Trả CustomerReview hoàn chỉnh + token usage.
    /// </summary>
    Task<ReviewAgentResult> RunAsync(
        Customer customer, string fingerprint, string tenantId,
        string? providerOverride, string? modelOverride, string? apiKeyOverride,
        Func<string, string?, Task>? onStage,
        TraceCollector? trace,
        CancellationToken ct);
}

public record ReviewAgentResult(
    CustomerReview Review,
    string AiProvider,
    string AiModel,
    int TokensIn,
    int TokensOut,
    string? Warning
);
