using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// SmartMail AI — hộp thư Gmail + phân loại AI + soạn nháp trả lời.
///   GET   /api/v1/mail/account              — trạng thái cấu hình hộp thư {address, configured}
///   POST  /api/v1/mail/account              — lưu creds Gmail {address, appPassword} (App Password mã hóa)
///   POST  /api/v1/mail/sync                 — IMAP kéo N thư mới nhất, phân loại email MỚI, lưu → {items, counts, classified}
///   GET   /api/v1/mail?status=&category=&search= — list đã lọc + counts
///   GET   /api/v1/mail/{id}                  — chi tiết 1 email
///   POST  /api/v1/mail/{id}/reply/draft      — SSE: stream nháp trả lời {tone, instruction, provider?, model?, apiKey?}
///   PATCH /api/v1/mail/{id}/status           — đổi trạng thái {status}
/// </summary>
public static class MailEndpoints
{
    private const int SyncMax = 30;
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── GET /mail/account ─── trạng thái cấu hình hộp thư (KHÔNG trả App Password) ──
        v1.MapGet("/mail/account", (MailAccountStore account) =>
            Results.Json(new { address = account.CurrentAddress(""), configured = account.IsConfigured(""), signature = account.Signature("") }));

        // ─── POST /mail/account ─── nhập creds Gmail + chữ ký từ UI ──────────────
        v1.MapPost("/mail/account", (MailAccountRequest req, MailAccountStore account) =>
        {
            if (string.IsNullOrWhiteSpace(req.Address) || string.IsNullOrWhiteSpace(req.AppPassword))
                return Results.BadRequest(new { error = "Thiếu địa chỉ Gmail hoặc App Password" });
            // App Password Gmail là 16 ký tự (có thể có khoảng trắng) — bỏ khoảng trắng.
            account.Set("", req.Address.Trim(), req.AppPassword.Replace(" ", "").Trim(), req.Signature);
            return Results.Json(new { ok = true, address = account.CurrentAddress(""), configured = account.IsConfigured(""), signature = account.Signature("") });
        });

        // ─── POST /mail/sync ───────────────────────────────────────────────────
        v1.MapPost("/mail/sync", async (
            IMailSource source, MailRepository repo, MailClassifier classifier,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace,
            ILogger<Program> log, HttpContext ctx) =>
        {
            IReadOnlyList<MailItem> fetched;
            try
            {
                fetched = await source.FetchRecentAsync(SyncMax, ctx.RequestAborted);
            }
            catch (InvalidOperationException ex)   // chưa cấu hình
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "IMAP sync lỗi");
                return Results.Json(new { error = "Không kết nối được hộp thư: " + ex.Message }, statusCode: 502);
            }

            int classified = 0;
            foreach (var mail in fetched)
            {
                if (repo.Has(mail.Id)) continue;   // đã có = đã phân loại → bỏ qua (tiết kiệm token)
                var (cat, sum) = await classifier.ClassifyAsync(mail, ctx.RequestAborted);
                repo.Upsert(mail with { Category = cat, AiSummary = sum });
                classified++;
            }
            log.LogInformation("[mail] sync: {Fetched} kéo về, {New} phân loại mới", fetched.Count, classified);

            var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
            return Results.Json(new { items = repo.Filter(null, null, null), counts = repo.Counts(), classified, _trace = traceObj });
        });

        // ─── GET /mail ─────────────────────────────────────────────────────────
        v1.MapGet("/mail", (MailRepository repo, string? status, string? category, string? search) =>
            Results.Json(new { items = repo.Filter(status, category, search), counts = repo.Counts() }));

        // ─── GET /mail/{id} ────────────────────────────────────────────────────
        v1.MapGet("/mail/{id}", (string id, MailRepository repo) =>
        {
            var m = repo.Get(id);
            return m == null ? Results.NotFound(new { error = "Không tìm thấy email" }) : Results.Json(m);
        });

        // ─── POST /mail/{id}/read ─── đánh dấu đã đọc khi mở email ───────────────
        v1.MapPost("/mail/{id}/read", (string id, MailRepository repo) =>
            repo.SetRead(id, true) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Không tìm thấy email" }));

        // ─── POST /mail/compose/draft (SSE) ─── AI soạn email MỚI từ brief ──────
        v1.MapPost("/mail/compose/draft", async (
            ComposeDraftRequest req, MailReplyService replyService,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace,
            ILogger<Program> log, HttpContext ctx) =>
        {
            await StartSseAsync(ctx);
            var emit = Sse(ctx);
            try
            {
                var text = await replyService.ComposeNewStreamAsync("", req, async d => await emit(new { delta = d }), ctx.RequestAborted);
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
            ComposeSendRequest req, IMailSender sender, ILogger<Program> log, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.To)) return Results.BadRequest(new { error = "Thiếu người nhận" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Nội dung rỗng" });
            try
            {
                await sender.SendAsync(req.To.Trim(), null, req.Subject ?? "", req.Text, null, ctx.RequestAborted);
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
            ILogger<Program> log, HttpContext ctx) =>
        {
            var mail = repo.Get(id);
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
                var text = await replyService.DraftStreamAsync("", mail, req,
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
            ILogger<Program> log, HttpContext ctx) =>
        {
            var mail = repo.Get(id);
            if (mail == null) return Results.NotFound(new { error = "Không tìm thấy email" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Nội dung trả lời rỗng" });

            try
            {
                await sender.SendReplyAsync(mail, req.Text, ctx.RequestAborted);
                // Lưu nội dung đã gửi + chuyển trạng thái Đã phản hồi.
                var draft = new MailDraft(req.Tone ?? mail.Draft?.Tone ?? "lich_su", req.Instruction, req.Text, DateTime.UtcNow.ToString("o"));
                repo.SetDraft(id, draft, status: "da_phan_hoi");
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
        v1.MapPatch("/mail/{id}/status", (string id, UpdateStatusRequest req, MailRepository repo) =>
        {
            if (!MailTaxonomy.IsStatus(req.Status))
                return Results.BadRequest(new { error = "status không hợp lệ" });
            return repo.SetStatus(id, req.Status)
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
}
