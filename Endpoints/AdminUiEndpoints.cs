using TourkitAiProxy.Services.Admin;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin UI endpoints — backing cho /admin-trav-ai/* pages. Tất cả require X-Admin-Session.
///
/// Thêm trang admin mới = thêm route ở đây + 1 component trong wwwroot/pages/admin.jsx
/// + 1 entry vào ADMIN_NAV (xem "Admin governance" trong CLAUDE.md).
///
///   GET /api/v1/admin/ui/ai-usage?days=30&amp;tenantId=  — aggregate cross-tenant
/// </summary>
public static class AdminUiEndpoints
{
    public static IEndpointRouteBuilder MapAdminUiEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/admin/ui").RequireAdminSession();

        // GET /api/v1/admin/ui/ai-usage?days=30&tenantId=
        g.MapGet("/ai-usage", async (
            int? days, string? tenantId,
            AdminUsageRepository usage,
            TkSessionRepository tkRepo,
            CancellationToken ct) =>
        {
            var d = Math.Clamp(days ?? 30, 1, 365);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddDays(-d);

            var totalsTask = usage.GetTotalsAsync(fromUtc, toUtc, tenantId, ct);
            var byModelTask = usage.GetByModelAsync(fromUtc, toUtc, tenantId, ct);
            var byTenantTask = usage.GetByTenantAsync(fromUtc, toUtc, tenantId, ct);
            var byDayTask = usage.GetByDayAsync(fromUtc, toUtc, tenantId, ct);
            await Task.WhenAll(totalsTask, byModelTask, byTenantTask, byDayTask);

            var totals = await totalsTask;
            var byModel = await byModelTask;
            var byTenant = await byTenantTask;
            var byDay = await byDayTask;

            // Resolve tenantName cho từng row (skip "(system)" sentinel).
            var realTenantIds = byTenant.Where(t => t.TenantId != "(system)").Select(t => t.TenantId);
            var names = await tkRepo.GetTenantNamesAsync(realTenantIds, ct);

            long totalCost = totals.CostVnd;
            return Results.Json(new
            {
                range = new
                {
                    from = fromUtc,
                    to = toUtc,
                    days = d
                },
                totals = new
                {
                    calls = totals.Calls,
                    inTokens = totals.InTokens,
                    outTokens = totals.OutTokens,
                    costVnd = totals.CostVnd
                },
                byModel = byModel.Select(m => new
                {
                    model = m.Model,
                    calls = m.Calls,
                    inTokens = m.InTokens,
                    outTokens = m.OutTokens,
                    costVnd = m.CostVnd
                }).ToList(),
                byTenant = byTenant.Select(t => new
                {
                    tenantId = t.TenantId,
                    tenantName = t.TenantId == "(system)" ? "(System tasks)"
                              : names.TryGetValue(t.TenantId, out var n) ? n
                              : t.TenantId,
                    calls = t.Calls,
                    inTokens = t.InTokens,
                    outTokens = t.OutTokens,
                    costVnd = t.CostVnd,
                    lastCallAt = t.LastCallAt,
                    sharePct = totalCost > 0 ? Math.Round((double)t.CostVnd * 100.0 / totalCost, 2) : 0.0
                }).ToList(),
                byDay = byDay.Select(day => new
                {
                    date = day.Date.ToString("yyyy-MM-dd"),
                    calls = day.Calls,
                    costVnd = day.CostVnd
                }).ToList()
            });
        });

        return routes;
    }
}
