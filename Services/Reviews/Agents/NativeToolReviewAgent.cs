// Services/Reviews/Agents/NativeToolReviewAgent.cs
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews.Agents;

/// <summary>
/// Native function-calling agent — chỉ chạy với Anthropic (claude-sonnet/haiku/opus).
/// Dùng <see cref="AnthropicToolsClient"/> với 1 terminal tool 'submit_customer_review' có schema đầy đủ
/// → AI BẮT BUỘC trả structured object hợp schema, KHÔNG leak markdown/thinking.
///
/// So với JsonPromptReviewAgent:
/// - Schema enforce: rank chỉ A/B/C/D, alert.level chỉ high/medium/none, type validation cho từng field
/// - 0% chance reasoning leak (vd Kimi/DeepSeek thường rò chain-of-thought vào content)
/// - Retry rate ~0 vì AI không thể trả format xấu
/// - Trade-off: chỉ chạy được trên provider Anthropic. Provider khác fallback JsonPromptReviewAgent.
/// </summary>
public class NativeToolReviewAgent : IReviewAgent
{
    private readonly AnthropicToolsClient _tools;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly ILogger<NativeToolReviewAgent> _log;

    private const string DefaultModel = "claude-sonnet-4-5";
    private const int MaxTokens = 4000;

    public NativeToolReviewAgent(
        AnthropicToolsClient tools, ProviderKeyStore keys,
        AiUsageLog usage, AiCallContext ctx,
        ILogger<NativeToolReviewAgent> log)
    {
        _tools = tools; _keys = keys; _usage = usage; _ctx = ctx; _log = log;
    }

    /// Chỉ xử lý khi provider là Anthropic. Provider khác fallback JsonPromptReviewAgent.
    public bool Supports(string providerId)
        => string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase);

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
        var userPrompt = ReviewPrompt.BuildUserPromptForNative(customer);
        var schema = ReviewPrompt.BuildSubmitReviewToolSchema();
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride! : DefaultModel;
        var apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride) ? apiKeyOverride! : _keys.Get("anthropic");

        trace?.SetMeta("agent", "NativeToolReviewAgent");
        trace?.SetMeta("provider", "anthropic");
        trace?.SetMeta("model", model);
        trace?.Step("prepare_prompt", "ok", 0,
            $"Build prompt KH {customer.Name} ({customer.Id}), {userPrompt.Length:N0} chars + 1 terminal tool schema",
            new() {
                ["promptChars"] = userPrompt.Length,
                ["systemChars"] = ReviewPrompt.SystemForNativeTool.Length,
                ["toolName"] = "submit_customer_review"
            });

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key trống — cần config Providers:Anthropic:ApiKey hoặc client gửi qua apiKey.");

        await Stage("calling");

        // AnthropicToolsClient tự ghi trace cho mỗi iteration + tool dispatch.
        // Vì agent này SINGLE-shot (chỉ 1 terminal tool, không có tool nào khác trong catalog),
        // loop sẽ kết thúc sau iter 1 — AI gọi submit_customer_review → AnthropicToolsClient break.
        var result = await _tools.RunAsync(
            systemPrompt:      ReviewPrompt.SystemForNativeTool,
            userPrompt:        userPrompt,
            tools:             new[] { schema },
            terminalToolName:  "submit_customer_review",
            toolHandler:       NoIntermediateTools,   // không có tool trung gian
            apiKey:            apiKey,
            model:             model,
            maxTokens:         MaxTokens,
            trace:             trace,
            ct:                ct);

        if (result.TerminalInput == null)
        {
            var warn = result.Warning ?? "AI không gọi submit_customer_review";
            trace?.Step("terminal_missing", "fail", 0, warn);
            throw new InvalidOperationException(
                $"NativeTool review thất bại cho KH {customer.Id}: {warn}");
        }

        await Stage("parsing");
        var parseTimer = trace?.Begin("parse_tool_input");
        var parsed = ReviewPrompt.ParseElement(result.TerminalInput.Value);
        parseTimer?.Done("ok",
            $"Parse tool input → rank={parsed.Rank ?? "?"}, strengths={parsed.Strengths?.Count ?? 0}, concerns={parsed.Concerns?.Count ?? 0}",
            new() {
                ["rank"] = parsed.Rank,
                ["alertHas"] = parsed.Alert != null,
                ["strengthsCount"] = parsed.Strengths?.Count ?? 0,
                ["concernsCount"] = parsed.Concerns?.Count ?? 0,
                ["iterations"] = result.Iterations
            });

        var review = ReviewPrompt.Compose(parsed, customer, fingerprint,
            aiProvider: "anthropic", aiModel: result.Model,
            tokensIn: result.TokensIn, tokensOut: result.TokensOut);

        // Ghi usage log riêng (provider không gọi qua IAiProvider.CompleteAsync nên _usage không tự track)
        var callCtx = _ctx.Resolve();
        _usage.Append(callCtx.Feature, callCtx.SessionId, callCtx.Tenant,
            "anthropic", result.Model, result.TokensIn, result.TokensOut, result.LatencyMs);

        return new ReviewAgentResult(
            Review:      review,
            AiProvider:  "anthropic",
            AiModel:     result.Model,
            TokensIn:    result.TokensIn,
            TokensOut:   result.TokensOut,
            Warning:     result.Warning);
    }

    /// <summary>
    /// Không có tool trung gian — Review là single-shot: prompt → submit_customer_review.
    /// Nếu AI gọi tool nào khác (lý ra không thể vì catalog chỉ có 1) → trả error.
    /// </summary>
    private static Task<string> NoIntermediateTools(string name, JsonElement input, CancellationToken ct)
        => Task.FromResult($"{{\"error\":\"Tool '{name}' không phải terminal — agent chỉ chấp nhận submit_customer_review\"}}");
}
