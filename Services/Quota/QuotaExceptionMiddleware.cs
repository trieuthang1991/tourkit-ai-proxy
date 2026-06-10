using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Quota;

/// Middleware bắt <see cref="QuotaExhaustedException"/> ở bất kỳ endpoint nào gọi AI provider
/// → trả 429 JSON với snapshot quota, không leak stack trace.
public class QuotaExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<QuotaExceptionMiddleware> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public QuotaExceptionMiddleware(RequestDelegate next, ILogger<QuotaExceptionMiddleware> log)
    {
        _next = next; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx, TenantQuotaStore store)
    {
        try { await _next(ctx); }
        catch (QuotaExhaustedException ex)
        {
            _log.LogInformation("Quota exhausted: tenant={Tenant} used={Used}/{Limit}", ex.Tenant, ex.Used, ex.Limit);
            if (ctx.Response.HasStarted) return;   // SSE đã bắn → không ghi đè
            ctx.Response.StatusCode = 429;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var snap = store.Snapshot(ex.Tenant);
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "Đã hết quota AI cho tenant này.",
                quota = snap,
            }, Json));
        }
    }
}
