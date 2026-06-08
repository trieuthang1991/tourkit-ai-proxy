// Services/Workflow/WorkflowTraceMiddleware.cs
using Microsoft.AspNetCore.Http;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Middleware: detect ?debug=1 query param HOẶC X-Debug header → tạo TraceCollector cho request.
/// Service nào cần ghi trace inject IWorkflowTraceAccessor.Current?.Step(...).
/// Endpoint cuối cùng tự attach trace vào response (vd { result, _trace: accessor.Current?.Build() }).
/// </summary>
public class WorkflowTraceMiddleware
{
    private readonly RequestDelegate _next;
    public WorkflowTraceMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        bool debug = IsDebugRequested(ctx);
        if (debug)
        {
            var trace = new TraceCollector(enabled: true);
            WorkflowTraceAccessor.AttachTo(ctx, trace);
        }
        await _next(ctx);
    }

    private static bool IsDebugRequested(HttpContext ctx)
    {
        // ?debug=1 hoặc ?debug=true
        if (ctx.Request.Query.TryGetValue("debug", out var q))
        {
            var v = q.ToString().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
        }
        // X-Debug: 1 header
        if (ctx.Request.Headers.TryGetValue("X-Debug", out var h))
        {
            var v = h.ToString().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
        }
        return false;
    }
}
