using TourkitAiProxy.Services.Admin;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin UI endpoints — backing cho /admin-trav-ai/* pages. Tất cả require X-Admin-Session.
///
/// Thêm trang admin mới = thêm route ở đây + 1 component trong wwwroot/pages/admin.jsx
/// + 1 entry vào ADMIN_NAV (xem "Admin governance" trong CLAUDE.md).
///
///   GET /api/v1/admin/ui/ai-usage?days=30&amp;tenantId=  — Task 4
/// </summary>
public static class AdminUiEndpoints
{
    public static IEndpointRouteBuilder MapAdminUiEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/ui").RequireAdminSession();

        // Placeholder ping — verify filter chạy. Sẽ xoá khi /ai-usage hoàn thiện.
        g.MapGet("/ping", (HttpContext ctx) =>
        {
            var user = ctx.Items[RequireAdminSessionExtensions.HttpItemKey] as string ?? "?";
            return Results.Json(new { ok = true, user });
        });

        return routes;
    }
}
