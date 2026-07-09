using System.Text.Json;
using TourkitAiProxy.Services.Admin;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Mail;
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
///   GET  /api/v1/admin/ui/tk-sessions                            — phiên đăng nhập TourKit đang active
///   DELETE /api/v1/admin/ui/tk-sessions/{id}                     — kick 1 phiên (force logout)
///   GET  /api/v1/admin/ui/outbound-mails?tenantId=&amp;kind=&amp;status= — hàng đợi mail (cross-tenant) + counts
///   GET  /api/v1/admin/ui/mail-templates                         — list template mail (global)
///   PUT  /api/v1/admin/ui/mail-templates/{code}                  — tạo/sửa template
///   DELETE /api/v1/admin/ui/mail-templates/{code}                — xóa template
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

        // GET /api/v1/admin/ui/tk-sessions
        // → list phiên TourKit đang active (đọc từ in-mem cache của TkSessionStore).
        // ChatMemory size để thấy phiên nào đã chat nhiều — không trả nội dung memory.
        g.MapGet("/tk-sessions", (TkSessionStore sessions) =>
        {
            var now = DateTime.UtcNow;
            var items = sessions.ListActive()
                .OrderByDescending(s => s.LastUsed)
                .Select(s => new
                {
                    id           = s.Id,
                    tenantId     = s.TenantId,
                    username     = s.Username,
                    fullName     = s.FullName,
                    companyName  = s.CompanyName,
                    lastUsedUtc  = s.LastUsed,
                    idleSeconds  = (long)(now - s.LastUsed).TotalSeconds,
                    chatTurns    = s.ChatMemory?.History?.Count ?? 0,
                    lastTool     = s.ChatMemory?.LastTool,
                    hasJwt       = !string.IsNullOrEmpty(s.Jwt),
                })
                .ToList();
            return Results.Json(new { items, total = items.Count });
        });

        // DELETE /api/v1/admin/ui/tk-sessions/{id} → kick session (xóa cache + SQL).
        g.MapDelete("/tk-sessions/{id}", async (
            string id,
            TkSessionStore sessions,
            HttpContext http,
            CancellationToken ct) =>
        {
            var by = http.Items[RequireAdminSessionExtensions.HttpItemKey] as string ?? "admin";
            var removed = await sessions.KickAsync(id, ct);
            return Results.Json(new { ok = removed, kicked = removed, by });
        });

        // ── Hàng đợi mail outbound (cross-tenant) ──────────────────────────────────
        // GET /api/v1/admin/ui/outbound-mails?tenantId=&kind=&status=&limit=50
        g.MapGet("/outbound-mails", async (
            string? tenantId, string? kind, int? status, int? limit,
            MailQueueRepository queue,
            TkSessionRepository tkRepo,
            CancellationToken ct) =>
        {
            var t  = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            var k  = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
            var take = Math.Clamp(limit ?? 50, 1, 500);

            var itemsTask  = queue.ListForAdminAsync(t, k, status, take, ct);
            var countsTask = queue.CountByStatusForAdminAsync(t, k, ct);
            await Task.WhenAll(itemsTask, countsTask);
            var rows   = await itemsTask;
            var counts = await countsTask;

            var tenantIds = rows.Select(r => r.TenantId).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            Dictionary<string, string> names;
            try { names = await tkRepo.GetTenantNamesAsync(tenantIds, ct); }
            catch { names = new(); }

            return Results.Json(new
            {
                items = rows.Select(r => new
                {
                    id           = r.Id,
                    tenantId     = r.TenantId,
                    tenantName   = names.TryGetValue(r.TenantId, out var n) ? n : r.TenantId,
                    kind         = r.Kind,
                    sourceId     = r.SourceId,
                    templateCode = r.TemplateCode,
                    toEmail      = r.ToEmail,
                    toName       = r.ToName,
                    cc           = r.Cc,
                    subject      = r.Subject,
                    paramsJson   = r.Params,
                    status       = r.Status,
                    statusText   = StatusText(r.Status),
                    retryCount   = r.RetryCount,
                    errorMessage = r.ErrorMessage,
                    scheduledUtc = r.ScheduledUtc,
                    createdUtc   = r.CreatedUtc,
                    processedUtc = r.ProcessedUtc
                }).ToList(),
                counts = new
                {
                    pending   = counts.GetValueOrDefault(0),
                    sent      = counts.GetValueOrDefault(1),
                    failed    = counts.GetValueOrDefault(2),
                    cancelled = counts.GetValueOrDefault(3),
                    skipped   = counts.GetValueOrDefault(4),
                    all       = counts.Values.Sum()
                }
            });

            static string StatusText(byte s) => s switch
            {
                0 => "Chờ gửi", 1 => "Đã gửi", 2 => "Lỗi", 3 => "Đã hủy", 4 => "Bỏ qua", _ => "?"
            };
        });

        // ── Quản lý template mail (global) ─────────────────────────────────────────
        // GET /api/v1/admin/ui/mail-templates
        g.MapGet("/mail-templates", async (MailTemplateRepository repo, CancellationToken ct) =>
        {
            var items = await repo.ListAsync(ct);
            return Results.Json(new { items });
        });

        // PUT /api/v1/admin/ui/mail-templates/{code} — upsert
        g.MapPut("/mail-templates/{code}", async (
            string code,
            MailTemplateUpsertReq req,
            HttpContext http,
            MailTemplateRepository repo,
            CancellationToken ct) =>
        {
            code = (code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest(new { error = "code trống" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Tên template không được trống" });
            if (string.IsNullOrWhiteSpace(req.Subject))
                return Results.BadRequest(new { error = "Tiêu đề (Subject) không được trống" });
            if (string.IsNullOrWhiteSpace(req.BodyHtml))
                return Results.BadRequest(new { error = "Nội dung (BodyHtml) không được trống" });

            // SampleParams nếu có phải là JSON object hợp lệ (preview an toàn).
            if (!string.IsNullOrWhiteSpace(req.SampleParams))
            {
                try { using var _ = JsonDocument.Parse(req.SampleParams); }
                catch { return Results.BadRequest(new { error = "SampleParams không phải JSON hợp lệ" }); }
            }

            var by = http.Items[RequireAdminSessionExtensions.HttpItemKey] as string ?? "admin";
            var saved = await repo.UpsertAsync(new MailTemplate
            {
                Code = code,
                Name = req.Name.Trim(),
                Subject = req.Subject,
                BodyHtml = req.BodyHtml,
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                SampleParams = string.IsNullOrWhiteSpace(req.SampleParams) ? null : req.SampleParams,
                Enabled = req.Enabled
            }, by, ct);
            return Results.Json(saved);
        });

        // DELETE /api/v1/admin/ui/mail-templates/{code}
        g.MapDelete("/mail-templates/{code}", async (
            string code, MailTemplateRepository repo, CancellationToken ct) =>
        {
            var removed = await repo.DeleteAsync((code ?? "").Trim(), ct);
            return Results.Json(new { ok = removed, removed });
        });

        return routes;
    }
}

public record MailTemplateUpsertReq(
    string Name,
    string Subject,
    string BodyHtml,
    string? Description,
    string? SampleParams,
    bool Enabled);

public record AdminQuotaTopUpReq(int Amount);
public record ConsultLeadContactedReq(bool Contacted);
