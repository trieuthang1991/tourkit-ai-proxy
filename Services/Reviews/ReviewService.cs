using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews;

/// <summary>
/// Orchestrate 1 lượt review KH: fingerprint cache check → dispatch tới <see cref="IReviewAgent"/> phù hợp → save.
///
/// Strategy pattern (mirror Chat-Agent v2): inject <c>IEnumerable&lt;IReviewAgent&gt;</c>,
/// pick agent đầu tiên <c>Supports(providerId)</c>. Thứ tự ở DI quan trọng:
///   1. NativeToolReviewAgent — Anthropic native function-calling (schema-enforced)
///   2. JsonPromptReviewAgent — fallback cho mọi provider (prompt-JSON + tolerant parse)
///
/// Prompt + parse logic tách ra <see cref="ReviewPrompt"/> để 2 agent dùng chung,
/// tránh drift schema giữa 2 path.
/// </summary>
public class ReviewService
{
    private readonly ReviewRepository _reviews;
    private readonly ProviderRegistry _registry;
    private readonly IEnumerable<IReviewAgent> _agents;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<ReviewService> _log;

    public ReviewService(
        ReviewRepository reviews, ProviderRegistry registry,
        IEnumerable<IReviewAgent> agents,
        IWorkflowTraceAccessor trace, ILogger<ReviewService> log)
    {
        _reviews = reviews; _registry = registry; _agents = agents;
        _trace = trace; _log = log;
    }

    /// Return review (cached nếu fingerprint không đổi & không forceFresh; gọi AI nếu cần).
    /// `onStage` lifecycle: "preparing" → "calling" → "parsing" → null khi done.
    /// Tuple: (review, fromCache).
    public async Task<(CustomerReview review, bool fromCache)> ReviewAsync(
        Customer customer, string tenantId, bool forceFresh = false,
        Func<string, string?, Task>? onStage = null,
        CancellationToken ct = default)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("CustomerReview");
        trace?.SetMeta("customerId", customer.Id);
        trace?.SetMeta("customerName", customer.Name);
        trace?.SetMeta("tenantId", tenantId);
        trace?.SetMeta("forceFresh", forceFresh);

        var fingerprint = ReviewRepository.FingerprintFor(customer);
        var fpTimer = trace?.Begin("fingerprint_check");

        if (!forceFresh)
        {
            var existing = _reviews.Get(tenantId, customer.Id);
            if (existing != null && existing.DataFingerprint == fingerprint)
            {
                fpTimer?.Done("ok",
                    $"Fingerprint khớp ({fingerprint[..8]}…) → trả review cũ, skip AI",
                    new() { ["fingerprint"] = fingerprint, ["cached"] = true });
                return (existing, true);
            }
        }
        fpTimer?.Done("skip",
            forceFresh ? "forceFresh=true → bỏ qua cache, gọi AI mới"
                       : "Fingerprint mới hoặc chưa có review → gọi AI",
            new() { ["fingerprint"] = fingerprint });

        // ── Dispatch tới agent phù hợp ──────────────────────────────────────
        // Provider override hiện chưa expose ở endpoint → dùng default registry.
        // Thêm sau khi cần A/B test giữa 2 path (vd UI cho admin pick provider).
        var defaultProviderId = _registry.Resolve(null).Id;
        var agent = _agents.FirstOrDefault(a => a.Supports(defaultProviderId))
            ?? throw new InvalidOperationException(
                $"Không có IReviewAgent nào hỗ trợ provider '{defaultProviderId}' — " +
                $"đăng ký ít nhất JsonPromptReviewAgent ở Program.cs để fallback.");

        trace?.Step("agent_dispatch", "ok", 0,
            $"Provider mặc định '{defaultProviderId}' → pick {agent.GetType().Name}",
            new() {
                ["provider"] = defaultProviderId,
                ["agent"] = agent.GetType().Name,
                ["candidates"] = _agents.Select(a => a.GetType().Name).ToArray()
            });

        var result = await agent.RunAsync(
            customer, fingerprint, tenantId,
            providerOverride: null, modelOverride: null, apiKeyOverride: null,
            onStage: onStage, trace: trace, ct: ct);

        _reviews.Save(result.Review, tenantId);
        return (result.Review, false);
    }
}
