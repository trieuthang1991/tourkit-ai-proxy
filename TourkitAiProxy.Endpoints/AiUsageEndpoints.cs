using TourkitAiProxy.Services;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// AI usage monitor — biết feature/user/tenant nào tiêu bao nhiêu token, tiền.
///   GET /api/v1/ai/usage[?days=1]   — summary: tổng, theo feature, theo model, theo user, budget
///   GET /api/v1/ai/usage/log[?n=50] — N dòng log gần nhất
/// </summary>
public static class AiUsageEndpoints
{
    private const long DefaultBudgetVndDay = 50_000;

    public static void MapAiUsageEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapGet("/ai/usage", (AiUsageLog log, IConfiguration cfg, int days = 1) =>
        {
            var all = log.Read(10_000);
            var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 30));
            var rows = all.Where(e => DateTime.TryParse(e.Timestamp, out var t) && t >= cutoff).ToList();

            long totalIn = rows.Sum(e => (long)e.InputTokens);
            long totalOut = rows.Sum(e => (long)e.OutputTokens);
            long totalCost = rows.Sum(e => e.CostVnd);
            int cacheHits = rows.Count(e => e.Cached);

            var byFeature = rows.GroupBy(e => e.Feature).Select(g => new
            {
                feature = g.Key, calls = g.Count(),
                inTok = g.Sum(e => e.InputTokens), outTok = g.Sum(e => e.OutputTokens),
                costVnd = g.Sum(e => e.CostVnd),
            }).OrderByDescending(x => x.costVnd).ToList();

            var byModel = rows.GroupBy(e => e.Provider + ":" + e.Model).Select(g => new
            {
                model = g.Key, calls = g.Count(),
                inTok = g.Sum(e => e.InputTokens), outTok = g.Sum(e => e.OutputTokens),
                costVnd = g.Sum(e => e.CostVnd),
            }).OrderByDescending(x => x.costVnd).ToList();

            var byUser = rows.Where(e => !string.IsNullOrEmpty(e.SessionId))
                .GroupBy(e => e.SessionId!).Select(g => new
                {
                    session = g.Key,
                    tenant = g.FirstOrDefault(x => x.Tenant != null)?.Tenant,
                    calls = g.Count(),
                    costVnd = g.Sum(e => e.CostVnd),
                }).OrderByDescending(x => x.costVnd).Take(10).ToList();

            var budgetVnd = cfg.GetValue<long?>("AiBudget:DailyVndPerTenant") ?? DefaultBudgetVndDay;
            var byTenantToday = rows.Where(e => DateTime.TryParse(e.Timestamp, out var t) && t >= DateTime.UtcNow.Date)
                .GroupBy(e => e.Tenant ?? "(unknown)").Select(g => new
                {
                    tenant = g.Key, costVnd = g.Sum(e => e.CostVnd),
                    pct = Math.Round(g.Sum(e => e.CostVnd) * 100.0 / Math.Max(1, budgetVnd), 1),
                    overBudget = g.Sum(e => e.CostVnd) > budgetVnd,
                }).OrderByDescending(x => x.costVnd).ToList();

            return Results.Json(new
            {
                rangeDays = days, generatedAt = DateTime.UtcNow.ToString("o"),
                totals = new { calls = rows.Count, cacheHits, inTok = totalIn, outTok = totalOut, costVnd = totalCost },
                budget = new { dailyVnd = budgetVnd, perTenantToday = byTenantToday },
                byFeature, byModel, byUser,
            });
        });

        v1.MapGet("/ai/usage/log", (AiUsageLog log, int n = 50)
            => Results.Json(log.Read(Math.Clamp(n, 10, 1000)).OrderByDescending(e => e.Timestamp).ToList()));
    }
}
