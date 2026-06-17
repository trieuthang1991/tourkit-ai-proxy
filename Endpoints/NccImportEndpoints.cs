using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.NccImport;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Bóc tách + chuẩn hoá file NCC để import lên hệ thống.
///
///   POST /api/v1/ncc-import/extract        — multipart (file) HOẶC JSON {text}
///                                            → { rows, source, latencyMs, … }
///   POST /api/v1/ncc-import/export         — JSON { rows } → tải file_import_ncc.xlsx
///   GET  /api/v1/ncc-import/template       — tải template gốc (gợi ý cho user)
///   GET  /api/v1/ncc-import/meta           — { types[], statuses[] } cho dropdown FE
public static class NccImportEndpoints
{
    public static IEndpointRouteBuilder MapNccImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/ncc-import");

        // ── EXTRACT: hỗ trợ 2 mode (multipart file HOẶC JSON text) ────────────
        g.MapPost("/extract", async (HttpContext ctx, NccImportService svc, ILogger<Program> log) =>
        {
            try
            {
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var file = form.Files.GetFile("file");
                    if (file == null || file.Length == 0)
                        return Results.BadRequest(new { error = "Thiếu file. Đính kèm trường 'file' trong multipart." });

                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (ext == ".xlsx" || ext == ".xls")
                    {
                        await using var s = file.OpenReadStream();
                        var r = svc.ParseExcel(s, ctx.RequestAborted);
                        log.LogInformation("[ncc-import] excel {N} rows from {F}", r.Rows.Count, file.FileName);
                        return Results.Json(r);
                    }
                    if (ext == ".pdf")
                    {
                        await using var s = file.OpenReadStream();
                        var r = await svc.ExtractFromPdfAsync(s, null, null, ctx.RequestAborted);
                        log.LogInformation("[ncc-import] pdf+ai {N} rows from {F}", r.Rows.Count, file.FileName);
                        return Results.Json(r);
                    }
                    if (ext == ".docx" || ext == ".doc")
                    {
                        await using var s = file.OpenReadStream();
                        var r = await svc.ExtractFromDocxAsync(s, null, null, ctx.RequestAborted);
                        log.LogInformation("[ncc-import] docx+ai {N} rows from {F}", r.Rows.Count, file.FileName);
                        return Results.Json(r);
                    }
                    if (ext == ".csv" || ext == ".txt")
                    {
                        using var sr = new StreamReader(file.OpenReadStream(),
                            System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        var txt = await sr.ReadToEndAsync(ctx.RequestAborted);
                        if (ext == ".csv")
                        {
                            var r = svc.ParseCsv(txt, ctx.RequestAborted);
                            return Results.Json(r);
                        }
                        // .txt → đẩy qua AI
                        var ai = await svc.ExtractFromTextAsync(txt, null, null, ctx.RequestAborted);
                        return Results.Json(ai);
                    }
                    return Results.BadRequest(new { error = $"Định dạng .{ext.TrimStart('.')} chưa hỗ trợ. Dùng .xlsx, .pdf, .docx, .csv hoặc dán text." });
                }
                else
                {
                    var req = await ctx.Request.ReadFromJsonAsync<NccExtractTextReq>(ctx.RequestAborted);
                    if (req == null || string.IsNullOrWhiteSpace(req.Text))
                        return Results.BadRequest(new { error = "Thiếu trường 'text'." });
                    var ai = await svc.ExtractFromTextAsync(req.Text, req.Provider, req.Model, ctx.RequestAborted);
                    return Results.Json(ai);
                }
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning(ex, "[ncc-import] parse fail");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[ncc-import] unhandled");
                return Results.Json(new { error = $"Lỗi server ({ex.GetType().Name})", detail = ex.Message }, statusCode: 500);
            }
        }).DisableAntiforgery();

        // ── EXTRACT-QUOTE: PDF/text → báo giá dạng GRID (giữ cấu trúc bảng gốc) ──
        g.MapPost("/extract-quote", async (HttpContext ctx, NccImportService svc, ILogger<Program> log) =>
        {
            try
            {
                NccQuoteResult r;
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var file = form.Files.GetFile("file");
                    if (file == null || file.Length == 0)
                        return Results.BadRequest(new { error = "Thiếu file. Đính kèm trường 'file'." });
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (ext != ".pdf")
                        return Results.BadRequest(new { error = $"Trích báo giá hiện hỗ trợ .pdf (hoặc dán text). Định dạng .{ext.TrimStart('.')} chưa hỗ trợ." });
                    await using var s = file.OpenReadStream();
                    r = await svc.ExtractQuoteFromPdfAsync(s, null, null, ctx.RequestAborted);
                    log.LogInformation("[ncc-import] quote pdf {F} ({Ms}ms)", file.FileName, r.LatencyMs);
                }
                else
                {
                    var req = await ctx.Request.ReadFromJsonAsync<NccExtractTextReq>(ctx.RequestAborted);
                    if (req == null || string.IsNullOrWhiteSpace(req.Text))
                        return Results.BadRequest(new { error = "Thiếu trường 'text'." });
                    r = await svc.ExtractQuoteFromTextAsync(req.Text, req.Provider, req.Model, ctx.RequestAborted);
                }
                return Results.Json(new { quote = r.Quote, latencyMs = r.LatencyMs, tokensIn = r.TokensIn, tokensOut = r.TokensOut, warning = r.Warning });
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning(ex, "[ncc-import] extract-quote parse fail");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[ncc-import] extract-quote unhandled");
                return Results.Json(new { error = $"Lỗi server ({ex.GetType().Name}): {ex.Message}" }, statusCode: 500);
            }
        }).DisableAntiforgery();

        // ── EXPORT: rows → file Excel chuẩn ────────────────────────────────────
        g.MapPost("/export", (NccExportReq req) =>
        {
            if (req?.Rows == null || req.Rows.Count == 0)
                return Results.BadRequest(new { error = "Không có dòng nào để xuất." });
            var bytes = NccExcelExporter.BuildXlsx(req.Rows);
            return Results.File(
                bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"file_import_ncc_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        });

        // ── TEMPLATE: tải template gốc để user xem mẫu ────────────────────────
        g.MapGet("/template", (IWebHostEnvironment env) =>
        {
            var path = Path.Combine(env.WebRootPath, "files", "file_import_ncc.xlsx");
            if (!File.Exists(path)) return Results.NotFound(new { error = "Template chưa có ở wwwroot/files/" });
            return Results.File(
                File.OpenRead(path),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "file_import_ncc_template.xlsx");
        });

        // ── META: enum cho dropdown FE ─────────────────────────────────────────
        g.MapGet("/meta", () => Results.Json(new
        {
            types = NccImportService.AllowedTypes,
            statuses = NccImportService.AllowedStatus
        }));

        // ── SERVICES: loại dịch vụ NCC (Hotel/Vé/Xe/HDV…) cho dropdown — cần đăng nhập TourKit ──
        g.MapGet("/services", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = SessionId(ctx);
            if (string.IsNullOrEmpty(sid))
                return Results.Json(new { error = "Chưa đăng nhập TourKit (thiếu sessionId)" }, statusCode: 401);
            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid, ctx.RequestAborted);
                JsonElement data;
                try { data = await api.GetAsync(jwt, "/api/ai/services", ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid, ctx.RequestAborted);
                    data = await api.GetAsync(jwt, "/api/ai/services", ctx.RequestAborted);
                }
                return Results.Json(data);   // envelope AiResult { items:[{id,name}], … }
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "[ncc-import] services"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // ── SAVE: báo giá đã bóc tách → tạo NCC trong CRM (qua TourKit.Api) — cần đăng nhập ──
        g.MapPost("/save", async (HttpContext ctx, TourKitApiClient api, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = SessionId(ctx);
            if (string.IsNullOrEmpty(sid))
                return Results.Json(new { error = "Chưa đăng nhập TourKit (thiếu sessionId)" }, statusCode: 401);

            NccSaveReq? req;
            try { req = await ctx.Request.ReadFromJsonAsync<NccSaveReq>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "Body JSON không hợp lệ" }); }
            if (req == null || req.Quote.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Thiếu 'quote' đã bóc tách" });
            if (req.ServiceId <= 0)
                return Results.BadRequest(new { error = "Chưa chọn loại dịch vụ (serviceId)" });

            var payload = NccQuoteMapper.ToCreateProvider(req.Quote, req.ServiceId, req.ProviderCode);
            if (string.IsNullOrWhiteSpace(payload.ProviderName))
                return Results.BadRequest(new { error = "Báo giá thiếu tên NCC — nhập tên trước khi lưu" });

            try
            {
                var jwt = await sessions.GetValidJwtAsync(sid, ctx.RequestAborted);
                JsonElement data;
                try { data = await api.PostAsync(jwt, "/api/ai/providers", payload, ctx.RequestAborted); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(sid, ctx.RequestAborted);
                    data = await api.PostAsync(jwt, "/api/ai/providers", payload, ctx.RequestAborted);
                }
                log.LogInformation("[ncc-import] save OK serviceId={S} prices={P}", req.ServiceId, payload.Prices.Count);
                return Results.Json(new { ok = true, result = data, priceCount = payload.Prices.Count });
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) { log.LogError(ex, "[ncc-import] save unhandled"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        }).DisableAntiforgery();

        return routes;
    }

    /// sessionId từ header X-Session-Id (ưu tiên) hoặc query ?sessionId= — giống các endpoint /mail, /visa.
    private static string? SessionId(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
           ?? ctx.Request.Query["sessionId"].FirstOrDefault();
}
