using System.Text.Json;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Quản lý khoá MCP của chính người đăng nhập (phiên TourKit qua <c>X-Session-Id</c>):
///   POST   /api/v1/mcp/tokens {label}  → {id, token, createdUtc}  (khoá hiện MỘT lần duy nhất)
///   GET    /api/v1/mcp/tokens          → danh sách, KHÔNG có khoá
///   DELETE /api/v1/mcp/tokens/{id}     → 204 / 404
///
/// <para>Mọi thao tác lọc theo (tenant, username) của phiên: người khác — kể cả cùng công ty — không
/// thấy, không thu hồi được khoá của bạn (404, không phải 403, để không lộ khoá có tồn tại).</para>
/// </summary>
public static class McpEndpoints
{
    public const string TokensPrefix = "/api/v1/mcp/tokens";
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
            try { body = await ctx.Request.ReadFromJsonAsync<CreateReq>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
            catch (JsonException) { return Results.BadRequest(new { error = "Nội dung gửi lên không phải JSON hợp lệ" }); }

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

        g.MapDelete("/{id:long}", async (long id, HttpContext ctx, TkSessionStore sessions, IMcpTokenStore store, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            return await store.RevokeAsync(id, a.TenantId, a.Username, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "Không tìm thấy khoá" });
        });
    }
}
