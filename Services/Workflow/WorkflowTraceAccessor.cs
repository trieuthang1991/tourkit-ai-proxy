// Services/Workflow/WorkflowTraceAccessor.cs
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Ambient accessor cho TraceCollector của request hiện tại.
/// Service nào cần ghi trace chỉ cần inject IWorkflowTraceAccessor + gọi Current?.Step(...).
/// Khi debug=false (default), Current trả null → mọi Step call là no-op zero-overhead.
///
/// Dùng AsyncLocal&lt;TraceCollector&gt; thay vì HttpContext.Items để trace flow qua
/// Task.Run / fire-and-forget background work (vd BatchService, DealBatchService).
/// AsyncLocal được capture ExecutionContext lúc Task.Run() → background task vẫn thấy
/// cùng TraceCollector dù HttpContext đã chết.
/// </summary>
public interface IWorkflowTraceAccessor
{
    /// <summary>TraceCollector của async-flow hiện tại, null nếu debug=off.</summary>
    TraceCollector? Current { get; }
}

public class WorkflowTraceAccessor : IWorkflowTraceAccessor
{
    private const string Key = "_workflow_trace";
    // AsyncLocal: per-async-flow. Mỗi request middleware set value, async work bên trong
    // (kể cả Task.Run fire-and-forget) thấy cùng giá trị qua ExecutionContext capture.
    private static readonly AsyncLocal<TraceCollector?> _current = new();
    private readonly IHttpContextAccessor _http;

    public WorkflowTraceAccessor(IHttpContextAccessor http) => _http = http;

    /// <summary>
    /// Ưu tiên AsyncLocal (chính). Fallback HttpContext.Items để tương thích middleware end-of-request log
    /// (sau khi response gửi, AsyncLocal có thể đã rơi khỏi context).
    /// </summary>
    public TraceCollector? Current => _current.Value ?? _http.HttpContext?.Items[Key] as TraceCollector;

    /// Middleware gọi để set TraceCollector cho request hiện tại.
    /// Set CẢ HAI (AsyncLocal + HttpContext.Items) để:
    ///  - AsyncLocal flow qua background work (Task.Run)
    ///  - HttpContext.Items để middleware truy lại được sau _next() return
    public static void AttachTo(HttpContext ctx, TraceCollector trace)
    {
        ctx.Items[Key] = trace;
        _current.Value = trace;
    }
}
