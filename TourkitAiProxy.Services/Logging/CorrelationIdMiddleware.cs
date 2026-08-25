namespace TourkitAiProxy.Services.Logging;

/// <summary>
/// Gán 1 <c>RequestId</c> duy nhất cho MỌI log trong 1 HTTP request → grep 1 lần ra full flow.
/// Header client gửi <c>X-Request-Id</c> được reuse; nếu không có → sinh GUID 12 ký tự.
/// Push vào <see cref="log4net.LogicalThreadContext"/> (async-flow safe) để layout pattern
/// <c>%property{RequestId}</c> in kèm mỗi log. Response cũng echo header để client trace tiếp.
/// Placeholder <c>TenantId</c> đặt rỗng — <see cref="RequestLoggingMiddleware"/> sẽ resolve từ
/// session/query nếu có.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Request-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            id = Guid.NewGuid().ToString("N")[..12];

        context.Items["RequestId"] = id;
        context.Response.Headers[HeaderName] = id;

        log4net.LogicalThreadContext.Properties["RequestId"] = id;
        log4net.LogicalThreadContext.Properties["TenantId"] = "";
        try
        {
            await _next(context);
        }
        finally
        {
            log4net.LogicalThreadContext.Properties.Remove("RequestId");
            log4net.LogicalThreadContext.Properties.Remove("TenantId");
        }
    }
}
