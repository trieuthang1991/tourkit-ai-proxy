using TourkitAiProxy.Services.Admin;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin UI endpoints — backing cho /admin-trav-ai/* pages. Tất cả require X-Admin-Session.
///
/// Thêm trang admin mới = thêm route ở đây + 1 component trong wwwroot/pages/admin.jsx
/// + 1 entry vào ADMIN_NAV (xem "Admin governance" trong CLAUDE.md).
///
///   GET  /api/v1/admin/ui/ai-usage?days=30&amp;tenantId=    — aggregate cross-tenant
///   GET  /api/v1/admin/ui/quota                            — list quota mọi tenant
///   POST /api/v1/admin/ui/quota/{tenant}/topup             — cộng quota cho tenant
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
            var realTenantIds = byTenant.Where(t => t.TenantId != AdminUsageRepository.SystemTenantKey).Select(t => t.TenantId);
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
                    tenantName = t.TenantId == AdminUsageRepository.SystemTenantKey ? "(System tasks)"
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

        // GET /api/v1/admin/ui/quota — list quota mọi tenant + display name
        g.MapGet("/quota", async (
            TenantQuotaStore quota,
            TkSessionRepository sessions,
            CancellationToken ct) =>
        {
            var snapshots = quota.ListAll(); // đã OrderByDescending(Used)
            var tenantIds = snapshots
                .Select(s => s.Tenant)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            Dictionary<string, string> names;
            try { names = await sessions.GetTenantNamesAsync(tenantIds, ct); }
            catch { names = new Dictionary<string, string>(); }

            var items = snapshots.Select(s => new
            {
                tenantId = s.Tenant,
                displayName = names.TryGetValue(s.Tenant, out var n) ? n : s.Tenant,
                limit = s.Limit,
                used = s.Used,
                remaining = s.Remaining,
                usedPct = s.UsedPct,
                warn = s.Warn,
                exhausted = s.Exhausted,
                updatedAtUtc = s.UpdatedAt
            }).ToList();

            return Results.Json(new { items });
        });

        // POST /api/v1/admin/ui/quota/{tenant}/topup — cộng quota cho tenant
        g.MapPost("/quota/{tenant}/topup", (
            string tenant,
            AdminQuotaTopUpReq req,
            TenantQuotaStore quota) =>
        {
            if (string.IsNullOrWhiteSpace(tenant))
                return Results.BadRequest(new { error = "tenant trống" });
            if (req.Amount < 1 || req.Amount > 100_000)
                return Results.BadRequest(new { error = "amount phải trong [1, 100000]" });

            var snap = quota.TopUp(tenant.Trim(), req.Amount);
            return Results.Json(new
            {
                tenantId = snap.Tenant,
                limit = snap.Limit,
                used = snap.Used,
                remaining = snap.Remaining,
                usedPct = snap.UsedPct,
                warn = snap.Warn,
                exhausted = snap.Exhausted,
                updatedAtUtc = snap.UpdatedAt
            });
        });

        return routes;
    }
}

public record AdminQuotaTopUpReq(int Amount);
