using System.Text.Json;
using TourkitAiProxy.Services.Admin;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Admin UI endpoints — backing cho /admin-trav-ai/* pages. Tất cả require X-Admin-Session.
///
/// Thêm trang admin mới = thêm route ở đây + 1 component trong wwwroot/pages/admin.jsx
/// + 1 entry vào ADMIN_NAV (xem "Admin governance" trong CLAUDE.md).
///
///   GET  /api/v1/admin/ui/ai-usage?days=30&amp;tenantId=          — aggregate cross-tenant
///   GET  /api/v1/admin/ui/quota                                  — list quota mọi tenant
///   POST /api/v1/admin/ui/quota/{tenant}/topup                   — cộng quota cho tenant
///   GET  /api/v1/admin/ui/consult-leads?status=                  — danh sách đăng ký tư vấn (landing)
///   POST /api/v1/admin/ui/consult-leads/{id}/contacted           — đánh dấu đã liên hệ (toggle)
///   GET  /api/v1/admin/ui/chat-unresolved?days=&amp;tag=            — câu hỏi /assistant AI không suy luận được
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

        // GET /api/v1/admin/ui/consult-leads?status=all|pending|contacted
        // → đọc data/consult-leads.jsonl + status side-car.
        g.MapGet("/consult-leads", async (
            string? status,
            ConsultLeadRepository repo,
            CancellationToken ct) =>
        {
            var all = await repo.ListAsync(ct);
            var filter = (status ?? "all").Trim().ToLowerInvariant();
            var items = filter switch
            {
                "pending"   => all.Where(r => !r.Contacted),
                "contacted" => all.Where(r =>  r.Contacted),
                _           => all
            };

            return Results.Json(new
            {
                items = items.Select(r => new
                {
                    id           = r.Id,
                    createdUtc   = r.CreatedUtc,
                    fullName     = r.FullName,
                    phone        = r.Phone,
                    email        = r.Email,
                    company      = r.Company,
                    feature      = r.Feature,
                    note         = r.Note,
                    ip           = r.Ip,
                    contacted    = r.Contacted,
                    contactedUtc = r.ContactedUtc,
                    contactedBy  = r.ContactedBy
                }).ToList(),
                totals = new
                {
                    all       = all.Count,
                    pending   = all.Count(r => !r.Contacted),
                    contacted = all.Count(r =>  r.Contacted)
                }
            });
        });

        // POST /api/v1/admin/ui/consult-leads/{id}/contacted   { contacted: true|false }
        g.MapPost("/consult-leads/{id}/contacted", (
            string id,
            ConsultLeadContactedReq req,
            HttpContext ctx,
            ConsultLeadRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id trống" });

            var by = ctx.Items[RequireAdminSessionExtensions.HttpItemKey] as string ?? "admin";
            if (req.Contacted) repo.MarkContacted(id, by);
            else               repo.MarkUncontacted(id);

            return Results.Json(new { ok = true, id, contacted = req.Contacted });
        });

        // GET /api/v1/admin/ui/chat-unresolved?days=7&tag=
        // → câu hỏi /assistant AI không trả được (9 trigger tag — xem UnresolvedQuestionsLog).
        g.MapGet("/chat-unresolved", async (
            int? days, string? tag,
            UnresolvedQuestionsLog log,
            TkSessionRepository tkRepo,
            CancellationToken ct) =>
        {
            var d = Math.Clamp(days ?? 7, 1, 90);
            var t = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();

            // Read trả entries từ MỚI → CŨ, đã filter theo days + tag (nếu có).
            var entries = log.Read(days: d, tag: t, maxEntries: 500);

            // Resolve tenant name (tenantId có thể là "host" như "staging.tourkit.vn" — không match TkSessions.
            // GetTenantNamesAsync best-effort, miss → fallback chính tenantId).
            var tenantIds = entries
                .Select(e => GetStr(e, "tenantId"))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList()!;
            Dictionary<string, string> names;
            try { names = await tkRepo.GetTenantNamesAsync(tenantIds!, ct); }
            catch { names = new(); }

            // Đếm tổng theo tag để chip filter hiện count — chạy 1 lần unfiltered.
            var totalsByTag = new Dictionary<string, int>();
            if (t != null)
            {
                // User đang lọc → cần đếm lại unfiltered để chip "Tất cả" có số.
                foreach (var e in log.Read(days: d, tag: null, maxEntries: 500))
                {
                    var tg = GetStr(e, "tag") ?? "(unknown)";
                    totalsByTag[tg] = totalsByTag.GetValueOrDefault(tg) + 1;
                }
            }
            else
            {
                foreach (var e in entries)
                {
                    var tg = GetStr(e, "tag") ?? "(unknown)";
                    totalsByTag[tg] = totalsByTag.GetValueOrDefault(tg) + 1;
                }
            }

            var items = entries.Select(e => new
            {
                ts             = GetStr(e, "ts"),
                tag            = GetStr(e, "tag"),
                sessionId      = GetStr(e, "sessionId"),
                tenantId       = GetStr(e, "tenantId"),
                tenantName     = ResolveName(GetStr(e, "tenantId"), names),
                question       = GetStr(e, "question"),
                toolChosen     = GetStr(e, "toolChosen"),
                plannerRaw     = GetStr(e, "plannerRaw"),
                aiReplyPreview = GetStr(e, "aiReplyPreview"),
                provider       = GetStr(e, "provider"),
                model          = GetStr(e, "model"),
                iterations     = GetInt(e, "iterations"),
                latencyMs      = GetLong(e, "latencyMs"),
                tokensIn       = GetInt(e, "tokensIn"),
                tokensOut      = GetInt(e, "tokensOut"),
                history        = GetHistory(e)
            }).ToList();

            return Results.Json(new
            {
                range  = new { days = d },
                tag    = t,
                items,
                totals = totalsByTag
            });

            static string? ResolveName(string? id, Dictionary<string, string> names)
                => string.IsNullOrWhiteSpace(id) ? null
                 : names.TryGetValue(id!, out var n) ? n : id;

            static string? GetStr(JsonElement el, string name)
                => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                   ? v.GetString() : null;

            static int? GetInt(JsonElement el, string name)
                => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                   ? v.GetInt32() : null;

            static long? GetLong(JsonElement el, string name)
                => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                   ? v.GetInt64() : null;

            static List<object> GetHistory(JsonElement el)
            {
                var list = new List<object>();
                if (!el.TryGetProperty("history", out var h) || h.ValueKind != JsonValueKind.Array)
                    return list;
                foreach (var turn in h.EnumerateArray())
                {
                    list.Add(new
                    {
                        role    = GetStr(turn, "role"),
                        content = GetStr(turn, "content")
                    });
                }
                return list;
            }
        });

        return routes;
    }
}

public record AdminQuotaTopUpReq(int Amount);
public record ConsultLeadContactedReq(bool Contacted);
