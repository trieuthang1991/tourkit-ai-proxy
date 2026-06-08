// Services/Chat/IAgentRuntime.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Contract cho 1 implementation agent runtime.
/// Hiện tại chỉ co JsonPlannerAgent (single-shot JSON). Phase 2 se them NativeToolUseAgent (Anthropic/OpenAI).
/// </summary>
public interface IAgentRuntime
{
    /// <summary>Provider nay co duoc runtime nay xu ly khong.</summary>
    bool Supports(IAiProvider provider);

    /// <summary>Chay agent buffered, tra ket qua cuoi cung.</summary>
    Task<AgentResult> RunAsync(AgentInput input, CancellationToken ct);

    /// <summary>Chay agent streaming, phat event qua emit.</summary>
    Task StreamAsync(AgentInput input, Func<object, Task> emit, CancellationToken ct);
}

/// <summary>
/// Du lieu dau vao cho 1 luot agent.
/// Cache lookup/save va truncate input da xu ly truoc o ChatAgentService.
/// </summary>
public record AgentInput(
    IAiProvider Provider,
    string? Model,
    string? ApiKey,
    List<ChatTurn> History,    // history day du (chua truncate truoc khi truyen vao)
    string SessionId,
    string TenantId,
    string Username,
    TraceCollector? Trace = null);  // No-op neu null hoac Enabled=false

/// <summary>
/// Ket qua tra ve tu 1 luot agent.
/// ChatAgentService convert sang ChatResult de gui frontend.
/// </summary>
public record AgentResult(
    string Reply,
    string ToolName,
    object? Params,
    ChatData? Data,
    long LatencyMs,
    int InputTokens,
    int OutputTokens,
    string? Warning,
    int Iterations);
