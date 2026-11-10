namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// Cổng của <c>/mcp</c> — chỉ gắn vào nhánh đường dẫn đó (<c>McpEndpoints.MapMcpServer</c>), và chỉ khi
/// bật <c>Features:Mcp</c>. Đạt thì đặt <see cref="McpCaller"/> vào <c>HttpContext.Items</c> rồi cho đi
/// tiếp; trượt thì trả JSON <c>{ error }</c> tiếng Việt và DỪNG — SDK MCP không bao giờ thấy request.
///
/// <para>401 luôn kèm <c>WWW-Authenticate: Bearer</c>: client MCP (Claude Code, mcp-remote) dựa vào
/// header này để biết là lỗi khoá chứ không phải máy chủ hỏng.</para>
/// </summary>
public sealed class McpAuthMiddleware
{
    private readonly RequestDelegate _next;

    public McpAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, McpAuthenticator auth)
    {
        var r = await auth.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        if (r.Caller != null)
        {
            ctx.Items[McpAuthenticator.ItemKey] = r.Caller;
            await _next(ctx);
            return;
        }

        ctx.Response.StatusCode = r.Status;
        if (r.Status == StatusCodes.Status401Unauthorized) ctx.Response.Headers.WWWAuthenticate = "Bearer";
        await ctx.Response.WriteAsJsonAsync(new { error = r.Error }, ctx.RequestAborted);
    }
}
