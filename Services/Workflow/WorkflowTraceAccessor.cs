// Services/Workflow/WorkflowTraceAccessor.cs
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Ambient accessor cho TraceCollector của request hiện tại.
/// Service nào cần ghi trace chỉ cần inject IWorkflowTraceAccessor + gọi Current?.Step(...).
/// Khi debug=false (default), Current trả null → mọi Step call là no-op zero-overhead.
/// </summary>
public interface IWorkflowTraceAccessor
{
    /// <summary>TraceCollector của request hiện tại, null nếu debug=off hoặc ngoài HTTP context.</summary>
    TraceCollector? Current { get; }
}

public class WorkflowTraceAccessor : IWorkflowTraceAccessor
{
    private const string Key = "_workflow_trace";
    private readonly IHttpContextAccessor _http;

    public WorkflowTraceAccessor(IHttpContextAccessor http) => _http = http;

    public TraceCollector? Current => _http.HttpContext?.Items[Key] as TraceCollector;

    /// Middleware gọi để set TraceCollector cho request hiện tại.
    public static void AttachTo(HttpContext ctx, TraceCollector trace)
        => ctx.Items[Key] = trace;
}
