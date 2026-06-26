using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.TourKit;
using MailSyncResult = TourkitAiProxy.Services.Mail.MailSyncResult;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// SmartMail AI — hộp thư Gmail + phân loại AI + soạn nháp trả lời.
///   GET   /api/v1/mail/account              — trạng thái cấu hình hộp thư {address, configured}
///   POST  /api/v1/mail/account              — lưu creds Gmail {address, appPassword} (App Password mã hóa)
///   POST  /api/v1/mail/sync                 — IMAP kéo N thư mới nhất, phân loại email MỚI, lưu → {items, counts, classified}
///   GET   /api/v1/mail?status=&category=&search= — list đã lọc + counts
///   POST  /api/v1/mail/sync?max=N            — JSON 1 lần (default max=100, cap 500)
///   POST  /api/v1/mail/sync/stream?max=N     — SSE progress {stage,current,total,subject}
///   GET   /api/v1/mail/{id}                  — chi tiết 1 email
///   POST  /api/v1/mail/{id}/reply/draft      — SSE: stream nháp trả lời {tone, instruction, provider?, model?, apiKey?}
///   PATCH /api/v1/mail/{id}/status           — đổi trạng thái {status}
/// </summary>
public static class MailEndpoints
{
    /// Cap mặc định khi không truyền ?max — đủ cover lần đầu sync hộp thư nhỏ.
    private const int SyncMaxDefault = 100;
    /// Cap tuyệt đối, tránh user nhập số quá lớn → IMAP timeout.
    private const int SyncMaxAbsolute = 500;
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── GET /mail/account ─── trạng thái cấu hình hộp thư (KHÔNG trả App Password) ──
        v1.MapGet("/mail/account", (HttpContext ctx, MailAccountStore account, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            return Results.Json(new
            {
                address = account.CurrentAddress(tenant, user),
                configured = account.IsConfigured(tenant, user),
                signature = account.Signature(tenant, user)
            });
        });

        // ─── POST /mail/account ─── nhập creds Gmail + chữ ký từ UI ──────────────
        v1.MapPost("/mail/account", (HttpContext ctx, MailAccountRequest req, MailAccountStore account, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            if (string.IsNullOrWhiteSpace(req.Address) || string.IsNullOrWhiteSpace(req.AppPassword))
                return Results.BadRequest(new { error = "Thiếu địa chỉ Gmail hoặc App Password" });
            // App Password Gmail là 16 ký tự (có thể có khoảng trắng) — bỏ khoảng trắng.
            account.Set(tenant, user, req.Address.Trim(), req.AppPassword.Replace(" ", "").Trim(), req.Signature);
            return Results.Json(new { ok = true, address = account.CurrentAddress(tenant, user), configured = account.IsConfigured(tenant, user), signature = account.Signature(tenant, user) });
        });

        // ─── DELETE /mail/account ─── ngắt kết nối Gmail của user hiện tại ───────
        // Mặc định CHỈ xoá credentials (App Password) của user — giữ lịch sử mail đã sync để xem lại.
        // ?wipeMails=true → xoá luôn dbo.Mails + dbo.MailSyncState của TENANT (lưu ý: chung tenant).
        v1.MapDelete("/mail/account", (HttpContext ctx, MailAccountStore account,
            MailRepository repo, MailSyncStore sync, TkSessionStore sessions,
            ILogger<Program> log, bool? wipeMails) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            var acctRows = account.Clear(tenant, user);
            int mailRows = 0, syncRows = 0;
            if (wipeMails == true)
            {
                mailRows = repo.ClearTenant(tenant);
                syncRows = sync.Clear(tenant);
            }
            log.LogInformation("[mail] disconnect tenant={Tenant} user={User} wipeMails={Wipe} account={A} mails={M} sync={S}",
                tenant, user, wipeMails == true, acctRows, mailRows, syncRows);
            return Results.Json(new { ok = true, cleared = new { account = acctRows, mails = mailRows, sync = syncRows } });
        });

        // ─── POST /mail/sync ───────────────────────────────────────────────────
        // Query: ?max=N (default 100, cap 500). Backward compat trả JSON 1 lần.
        v1.MapPost("/mail/sync", async (
            MailSyncService sync, MailRepository repo,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace,
            TkSessionStore sessions,
            ILogger<Program> log, HttpContext ctx, int? max) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            var fetchCap = Math.Clamp(max ?? SyncMaxDefault, 1, SyncMaxAbsolute);

            MailSyncResult result;
            try
            {
                result = await sync.RunAsync(tenant, user, fetchCap, ctx.RequestAborted);
            }
            catch (InvalidOperationException ex)   // chưa cấu hình
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "IMAP sync lỗi");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }

            var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
            return Results.Json(new
            {
                items = repo.Filter(tenant, null, null, null),
                counts = repo.Counts(tenant),
                classified = result.Classified,
                fetched = result.Fetched,
                _trace = traceObj
            });
        });

        // ─── POST /mail/sync/stream ────────────────────────────────────────────
        // SSE — stream progress để frontend hiện thanh tiến độ thay vì spinner mơ hồ.
        // Events: {stage:"fetching"} → {stage:"classifying", current, total, subject}
        //         (lặp lại từng mail) → {stage:"done", classified, fetched, counts, items}
        v1.MapPost("/mail/sync/stream", async (
            IMailSource source, MailRepository repo, MailClassifier classifier,
            TkSessionStore sessions, ILogger<Program> log, HttpContext ctx, int? max) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) { ctx.Response.StatusCode = 401; return; }
            var (_, tenant, user) = auth.Value;
            var fetchCap = Math.Clamp(max ?? SyncMaxDefault, 1, SyncMaxAbsolute);

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            async Task Emit(object obj)
            {
                var json = JsonSerializer.Serialize(obj, SseJson);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                await Emit(new { stage = "fetching", message = $"Đang kéo email từ Gmail (tối đa {fetchCap})..." });
                IReadOnlyList<MailItem> fetched;
                try
                {
                    fetched = await source.FetchRecentAsync(tenant, user, fetchCap, ctx.RequestAborted);
                }
                catch (InvalidOperationException ex)
                {
                    await Emit(new { stage = "error", message = ex.Message }); return;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "IMAP sync lỗi");
                    await Emit(new { stage = "error", message = "Không kết nối được hộp thư: " + ex.Message }); return;
                }

                // Đếm email mới cần classify (skip đã có)
                var newMails = fetched.Where(m => !repo.Has(tenant, m.Id)).ToList();
                await Emit(new { stage = "fetched", fetched = fetched.Count, toClassify = newMails.Count });

                int classified = 0;
                for (int i = 0; i < newMails.Count; i++)
                {
                    var mail = newMails[i];
                    await Emit(new {
                        stage = "classifying",
                        current = i + 1,
                        total = newMails.Count,
                        subject = mail.Subject?.Length > 60 ? mail.Subject[..60] + "..." : mail.Subject
                    });
                    var (cat, sum) = await classifier.ClassifyAsync(mail, ctx.RequestAborted);
                    repo.Upsert(tenant, mail with { Category = cat, AiSummary = sum });
                    classified++;
                }
                log.LogInformation("[mail] sync/stream: {Fetched} kéo về (cap {Cap}), {New} phân loại mới", fetched.Count, fetchCap, classified);

                await Emit(new {
                    stage = "done",
                    fetched = fetched.Count,
                    classified,
                    items = repo.Filter(tenant, null, null, null),
                    counts = repo.Counts(tenant)
                });
            }
            catch (OperationCanceledException) { /* client disconnect */ }
        });

        // ─── GET /mail ─────────────────────────────────────────────────────────
        v1.MapGet("/mail", (HttpContext ctx, MailRepository repo, TkSessionStore sessions, string? status, string? category, string? search) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            return Results.Json(new { items = repo.Filter(tenant, status, category, search), counts = repo.Counts(tenant) });
        });

        // ─── GET /mail/{id} ────────────────────────────────────────────────────
        v1.MapGet("/mail/{id}", (HttpContext ctx, string id, MailRepository repo, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            var m = repo.Get(tenant, id);
            return m == null ? Results.NotFound(new { error = "Không tìm thấy email" }) : Results.Json(m);
        });

        // ─── POST /mail/{id}/read ─── đánh dấu đã đọc khi mở email ───────────────
        v1.MapPost("/mail/{id}/read", (HttpContext ctx, string id, MailRepository repo, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            return repo.SetRead(tenant, id, true)
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "Không tìm thấy email" });
        });

        // ─── POST /mail/compose/draft (SSE) ─── AI soạn email MỚI từ brief ──────
        v1.MapPost("/mail/compose/draft", async (
            ComposeDraftRequest req, MailReplyService replyService,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace,
            TkSessionStore sessions,
            ILogger<Program> log, HttpContext ctx) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Phiên không hợp lệ — đăng nhập lại" });
                return;
            }
            var (_, tenant, user) = auth.Value;

            await StartSseAsync(ctx);
            var emit = Sse(ctx);
            try
            {
                var text = await replyService.ComposeNewStreamAsync(tenant, user, req, async d => await emit(new { delta = d }), ctx.RequestAborted);
                await emit(new { done = true, text });
                if (trace.Current?.Enabled == true) await emit(new { trace = trace.Current.Build() });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Soạn email mới lỗi");
                try { await emit(new { error = "Soạn email lỗi: " + ex.Message }); await emit(new { done = true }); } catch { }
            }
        });

        // ─── POST /mail/compose/send ─── gửi email MỚI qua SMTP ─────────────────
        v1.MapPost("/mail/compose/send", async (
            ComposeSendRequest req, IMailSender sender, TkSessionStore sessions, ILogger<Program> log, HttpContext ctx) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            if (string.IsNullOrWhiteSpace(req.To)) return Results.BadRequest(new { error = "Thiếu người nhận" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Nội dung rỗng" });
            try
            {
                await sender.SendAsync(tenant, user, req.To.Trim(), null, req.Subject ?? "", req.Text, null, ctx.RequestAborted);
                return Results.Json(new { ok = true });
            }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
            catch (Exception ex)
            {
                log.LogError(ex, "Gửi email mới lỗi");
                return Results.Json(new { error = "Gửi email lỗi: " + ex.Message }, statusCode: 502);
            }
        });

        // ─── POST /mail/{id}/reply/draft (SSE) ─────────────────────────────────
        v1.MapPost("/mail/{id}/reply/draft", async (
            string id, DraftReplyRequest req, MailRepository repo, MailReplyService replyService,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace,
            TkSessionStore sessions,
            ILogger<Program> log, HttpContext ctx) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Phiên không hợp lệ — đăng nhập lại" });
                return;
            }
            var (_, tenant, user) = auth.Value;

            var mail = repo.Get(tenant, id);
            if (mail == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "Không tìm thấy email" });
                return;
            }

            await StartSseAsync(ctx);
            var emit = Sse(ctx);
            try
            {
                var text = await replyService.DraftStreamAsync(tenant, user, mail, req,
                    async d => await emit(new { delta = d }), ctx.RequestAborted);
                await emit(new { done = true, text });
                if (trace.Current?.Enabled == true) await emit(new { trace = trace.Current.Build() });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Soạn nháp email {Id} lỗi", id);
                try { await emit(new { error = "Soạn nháp lỗi: " + ex.Message }); await emit(new { done = true }); } catch { }
            }
        });

        // ─── POST /mail/{id}/reply/send ─── gửi nháp (đã sửa) cho khách qua SMTP ──
        v1.MapPost("/mail/{id}/reply/send", async (
            string id, SendReplyRequest req, MailRepository repo, IMailSender sender,
            TkSessionStore sessions,
            ILogger<Program> log, HttpContext ctx) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;

            var mail = repo.Get(tenant, id);
            if (mail == null) return Results.NotFound(new { error = "Không tìm thấy email" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Nội dung trả lời rỗng" });

            try
            {
                await sender.SendReplyAsync(tenant, user, mail, req.Text, ctx.RequestAborted);
                // Lưu nội dung đã gửi + chuyển trạng thái Đã phản hồi.
                var draft = new MailDraft(req.Tone ?? mail.Draft?.Tone ?? "lich_su", req.Instruction, req.Text, DateTime.UtcNow.ToString("o"));
                repo.SetDraft(tenant, id, draft, status: "da_phan_hoi");
                return Results.Json(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Gửi email {Id} lỗi", id);
                return Results.Json(new { error = "Gửi email lỗi: " + ex.Message }, statusCode: 502);
            }
        });

        // ─── PATCH /mail/{id}/status ───────────────────────────────────────────
        v1.MapPatch("/mail/{id}/status", (HttpContext ctx, string id, UpdateStatusRequest req, MailRepository repo, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant, user) = auth.Value;
            if (!MailTaxonomy.IsStatus(req.Status))
                return Results.BadRequest(new { error = "status không hợp lệ" });
            return repo.SetStatus(tenant, id, req.Status)
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "Không tìm thấy email" });
        });

        return routes;
    }

    // ─── SSE helpers (dùng chung reply/draft + compose/draft) ────────────────────
    private static async Task StartSseAsync(HttpContext ctx)
    {
        ctx.Response.Headers["Content-Type"]      = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await ctx.Response.StartAsync(ctx.RequestAborted);
    }

    private static Func<object, Task> Sse(HttpContext ctx) => async payload =>
    {
        var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(payload, SseJson) + "\n\n");
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    };

    /// Extract sessionId + tenantId + username từ request. Return null nếu missing/invalid session.
    /// Handler caller trả 401 nếu null. Username dùng để scope MailAccount per-user
    /// (cùng tenant nhưng 2 nhân viên = 2 hộp thư riêng).
    private static (string SessionId, string TenantId, string Username)? RequireSession(
        HttpContext ctx, TourkitAiProxy.Services.TourKit.TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId, s.Username);
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
