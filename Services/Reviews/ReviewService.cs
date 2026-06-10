using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Reviews;

/// <summary>
/// Orchestrate 1 lượt review KH: KHÔNG check cache → luôn gọi AI → save vào DB cho list status badge.
///
/// Model mặc định đọc TỪ APPSETTINGS (`Models:Review:*`) — user không cấu hình ở UI.
/// Per-call override (providerOverride/modelOverride/apiKeyOverride) chỉ dùng cho admin debug/AB,
/// frontend không gửi nữa.
///
/// Strategy pattern: inject IEnumerable&lt;IReviewAgent&gt;, pick agent đầu tiên Supports(providerId).
/// </summary>
public class ReviewService
{
    private readonly ReviewRepository _reviews;
    private readonly ProviderRegistry _registry;
    private readonly ModelDefaults _defaults;
    private readonly IEnumerable<IReviewAgent> _agents;
    private readonly IWorkflowTraceAccessor _trace;
    private readonly ILogger<ReviewService> _log;

    public ReviewService(
        ReviewRepository reviews, ProviderRegistry registry, ModelDefaults defaults,
        IEnumerable<IReviewAgent> agents,
        IWorkflowTraceAccessor trace, ILogger<ReviewService> log)
    {
        _reviews = reviews; _registry = registry; _defaults = defaults; _agents = agents;
        _trace = trace; _log = log;
    }

    /// Gọi AI mới mỗi lần (KHÔNG check cache). Save vào DB sau khi xong.
    /// `forceFresh` param giữ cho back-compat API contract, hiện tại no-op (luôn fresh).
    public async Task<(CustomerReview review, bool fromCache)> ReviewAsync(
        Customer customer, string tenantId, bool forceFresh = false,
        Func<string, string?, Task>? onStage = null,
        string? providerOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        CancellationToken ct = default)
    {
        var trace = _trace.Current;
        trace?.SetWorkflow("CustomerReview");
        trace?.SetMeta("customerId", customer.Id);
        trace?.SetMeta("customerName", customer.Name);
        trace?.SetMeta("tenantId", tenantId);

        var fingerprint = ReviewRepository.FingerprintFor(customer);

        // Resolve provider/model/apiKey từ Models:Review config (override CHO PHÉP nhưng default = appsettings).
        var review = _defaults.Review;
        var resolvedProvider = providerOverride ?? review.Provider;
        var resolvedModel    = modelOverride    ?? review.Model;
        var resolvedApiKey   = apiKeyOverride   ?? review.ApiKey;

        trace?.Step("config_resolved", "ok", 0,
            $"Models:Review từ appsettings → provider={resolvedProvider}, model={resolvedModel}, " +
            $"apiKey={(string.IsNullOrEmpty(resolvedApiKey) ? "(rỗng — fallback ProviderKeyStore)" : "***")}",
            new() {
                ["provider"] = resolvedProvider ?? "",
                ["model"] = resolvedModel ?? "",
                ["hasApiKey"] = !string.IsNullOrEmpty(resolvedApiKey)
            });

        var providerId = _registry.Resolve(resolvedProvider).Id;
        var agent = _agents.FirstOrDefault(a => a.Supports(providerId))
            ?? throw new InvalidOperationException(
                $"Không có IReviewAgent nào hỗ trợ provider '{providerId}'.");

        trace?.Step("agent_dispatch", "ok", 0,
            $"Provider '{providerId}' → pick {agent.GetType().Name}",
            new() {
                ["provider"] = providerId,
                ["agent"] = agent.GetType().Name,
                ["candidates"] = _agents.Select(a => a.GetType().Name).ToArray()
            });

        var result = await agent.RunAsync(
            customer, fingerprint, tenantId,
            providerOverride: resolvedProvider,
            modelOverride:    resolvedModel,
            apiKeyOverride:   resolvedApiKey,
            onStage: onStage, trace: trace, ct: ct);

        // Save DB cho list status badge ("none" → "fresh") — không skip lần sau, chỉ overwrite.
        _reviews.Save(result.Review, tenantId);
        return (result.Review, false);   // fromCache luôn false
    }
}
