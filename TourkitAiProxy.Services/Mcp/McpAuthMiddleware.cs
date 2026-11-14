namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// Cổng của <c>/mcp</c> — chỉ gắn vào nhánh đường dẫn đó (<c>McpEndpoints.MapMcpServer</c>), và chỉ khi
/// bật <c>Features:Mcp</c>. Đạt thì đặt <see cref="McpCaller"/> vào <c>HttpContext.Items</c> rồi cho đi
/// tiếp; trượt thì trả JSON <c>{ error }</c> tiếng Việt và DỪNG — SDK MCP không bao giờ thấy request.
///
/// <para>401 luôn kèm <c>WWW-Authenticate</c> (<see cref="McpAuthenticator.WwwAuthenticate"/>): client MCP
/// (Claude Code, mcp-remote) dựa vào header này để biết là lỗi khoá chứ không phải máy chủ hỏng. Khi cờ
/// <c>Features:McpOAuth</c> có hiệu lực, header là <c>Bearer resource_metadata="{PublicBaseUrl}/.well-known/oauth-protected-resource",
/// scope="tourkit.read"</c> — Claude.ai/ChatGPT chỉ bắt đầu đăng nhập OAuth khi gặp đúng 401 này. Tắt thì là <c>Bearer</c>
/// như giai đoạn 1.</para>
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
        if (r.Status == StatusCodes.Status401Unauthorized) ctx.Response.Headers.WWWAuthenticate = auth.WwwAuthenticate;
        await ctx.Response.WriteAsJsonAsync(new { error = r.Error }, ctx.RequestAborted);
    }
}
