using TourkitAiProxy.Models;
using TourkitAiProxy.Services.NccImport;

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

        return routes;
    }
}
