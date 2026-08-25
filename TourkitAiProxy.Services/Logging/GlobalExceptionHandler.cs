using Microsoft.AspNetCore.Diagnostics;

namespace TourkitAiProxy.Services.Logging;

/// <summary>
/// Handler cuối cùng cho exception KHÔNG được endpoint bắt.
///  • Log ERROR có full stack + method/path + requestId → dễ tra từ file log.
///  • Trả JSON gọn cho client (không leak stack): <c>{ error, detail, requestId }</c>.
///  • KHÔNG double-response nếu status đã ghi 1 phần.
///
/// Wire ở Program.cs: <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c> + <c>UseExceptionHandler()</c>.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _log;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) => _log = log;

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path + ctx.Request.QueryString;
        var reqId = ctx.Items.TryGetValue("RequestId", out var r) ? r?.ToString() : null;

        _log.LogError(ex, "UNHANDLED {Method} {Path} → 500: {Type}: {Msg}",
            method, path, ex.GetType().Name, ex.Message);

        if (ctx.Response.HasStarted) return false;

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "Internal server error",
            detail = ex.Message,
            type = ex.GetType().Name,
            requestId = reqId
        }, ct);
        return true;
    }
}
