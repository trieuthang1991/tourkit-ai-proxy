using System.Text.Json;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Mcp;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Quản lý khoá MCP của chính người đăng nhập (phiên TourKit qua <c>X-Session-Id</c>):
///   POST   /api/v1/mcp/tokens {label}  → {id, token, createdUtc}  (khoá hiện MỘT lần duy nhất)
///   GET    /api/v1/mcp/tokens          → danh sách, KHÔNG có khoá
///   DELETE /api/v1/mcp/tokens/{id}     → 204 / 404  (xoá luôn khỏi đệm xác thực — chết NGAY)
///
/// <para>Và chính máy chủ MCP tại <see cref="McpPath"/> (<see cref="MapMcpServer"/>): Streamable HTTP
/// của SDK, đứng sau <see cref="McpAuthMiddleware"/> (khoá <c>Authorization: Bearer tkmcp_…</c>).</para>
///
/// <para>Mọi thao tác lọc theo (tenant, username) của phiên: người khác — kể cả cùng công ty — không
/// thấy, không thu hồi được khoá của bạn (404, không phải 403, để không lộ khoá có tồn tại).</para>
/// </summary>
public static class McpEndpoints
{
    public const string TokensPrefix = "/api/v1/mcp/tokens";
    public const string McpPath = "/mcp";
    private const int MaxLabel = 100;
    private const int MaxActivePerUser = 20;

    private record CreateReq(string? Label);

    public static void MapMcpEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup(TokensPrefix);

        g.MapPost("", async (HttpContext ctx, TkSessionStore sessions, IMcpTokenStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            CreateReq? body = null;
            // Thiếu thân/Content-Type không phải JSON thì ReadFromJsonAsync ném InvalidOperationException → 500; chặn thành 400.
            if (!ctx.Request.HasJsonContentType())
                return Results.BadRequest(new { error = "Cần gửi JSON (Content-Type: application/json) có trường label" });
            try { body = await ctx.Request.ReadFromJsonAsync<CreateReq>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
            catch (JsonException) { return Results.BadRequest(new { error = "Nội dung gửi lên không phải JSON hợp lệ" }); }
            catch (InvalidOperationException) { return Results.BadRequest(new { error = "Thiếu nội dung JSON" }); }

            var label = (body?.Label ?? "").Trim();
            if (label.Length == 0) return Results.BadRequest(new { error = "Cần đặt tên cho khoá (label)" });
            if (label.Length > MaxLabel) return Results.BadRequest(new { error = $"Tên khoá tối đa {MaxLabel} ký tự" });

            // Chặn tạo vô hạn: mỗi khoá còn sống là một cửa vào dữ liệu công ty.
            var mine = await store.ListAsync(a.TenantId, a.Username, ct);
            if (mine.Count(r => r.RevokedUtc == null) >= MaxActivePerUser)
                return Results.BadRequest(new { error = $"Tối đa {MaxActivePerUser} khoá đang dùng — hãy thu hồi bớt" });

            var (id, token) = await store.CreateAsync(a.TenantId, a.Username, a.SessionId, label, ct);
            return Results.Json(new { id, token, createdUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });

        g.MapGet("", async (HttpContext ctx, TkSessionStore sessions, IMcpTokenStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            var rows = await store.ListAsync(a.TenantId, a.Username, ct);
            // Chiếu thủ công: không bao giờ trả SessionId (là bí mật đăng nhập) ra ngoài.
            var items = rows.Select(r => new
            {
                id = r.Id, label = r.Label,
                createdUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc),
                lastUsedUtc = r.LastUsedUtc is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : (DateTime?)null,
                revokedUtc = r.RevokedUtc is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : (DateTime?)null,
            });
            return Results.Json(new { items }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });

        g.MapDelete("/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions, IMcpTokenStore store,
            McpAuthenticator auth, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await store.RevokeAsync(id, a.TenantId, a.Username, ct))
                return Results.NotFound(new { error = "Không tìm thấy khoá" });
            // Thu hồi phải có hiệu lực NGAY, không chờ đệm xác thực 60 giây hết hạn (cùng tiến trình).
            auth.Evict(id);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Máy chủ MCP tại <see cref="McpPath"/>. Cổng xác thực gắn bằng <c>UseWhen</c> theo đúng tiền tố này —
    /// chỉ request tới <c>/mcp</c> mới đi qua nó, phần còn lại của app không đổi gì. Middleware được thêm
    /// vào pipeline TRƯỚC bước thực thi endpoint (WebApplication tự đặt UseEndpoints ở cuối), nên request
    /// không có khoá bị chặn trước khi SDK đọc tới thân JSON-RPC.
    ///
    /// <para>Chỉ gọi khi bật <c>Features:Mcp</c> — xem <c>EndpointRegistration.MapMcp</c>.</para>
    /// </summary>
    public static void MapMcpServer(this WebApplication app)
    {
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(McpPath),
            b => b.UseMiddleware<McpAuthMiddleware>());
        app.MapMcp(McpPath);
    }
}
