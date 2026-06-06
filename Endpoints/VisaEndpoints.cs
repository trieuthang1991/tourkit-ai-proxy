using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Visa;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Thẩm định Visa AI — upload hồ sơ → AI đọc (vision) → chấm tỉ lệ đậu/rớt.
///   POST   /api/v1/visa/assess              — multipart files[] (+applicantName) → đọc hồ sơ → {id, extraction}
///   POST   /api/v1/visa/assess/{id}/score   — chấm điểm (body: profile đã sửa? + AI prefs)
///   GET    /api/v1/visa/assessments         — lịch sử
///   GET    /api/v1/visa/assessments/{id}    — chi tiết
///   DELETE /api/v1/visa/assessments/{id}    — xóa kết quả + file gốc
/// </summary>
public static class VisaEndpoints
{
    private const int MaxFiles = 10;
    private const long MaxBytes = 10L * 1024 * 1024;   // 10MB/file
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif" };

    public static void MapVisaEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── POST /visa/assess ─── upload + AI đọc hồ sơ (bước 1) ────────────────
        v1.MapPost("/visa/assess", async (HttpRequest request,
            VisaExtractionService extractor, VisaRepository repo, VisaFileStore store, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Cần gửi dạng multipart/form-data (files[])" });

            var form = await request.ReadFormAsync(ct);
            var files = form.Files;
            if (files.Count == 0) return Results.BadRequest(new { error = "Chưa chọn file hồ sơ nào" });
            if (files.Count > MaxFiles) return Results.BadRequest(new { error = $"Tối đa {MaxFiles} file/lần" });

            var applicantName = form["applicantName"].ToString().Trim();
            var provider = NullIfBlank(form["provider"].ToString());
            var model = NullIfBlank(form["model"].ToString());
            var apiKey = NullIfBlank(form["apiKey"].ToString());

            var uploads = new List<VisaExtractionService.UploadFile>();
            var rawBytes = new List<(string name, byte[] data)>();
            foreach (var f in files)
            {
                if (f.Length == 0) continue;
                if (f.Length > MaxBytes)
                    return Results.BadRequest(new { error = $"File '{f.FileName}' vượt 10MB" });
                if (!AllowedTypes.Contains(f.ContentType))
                    return Results.BadRequest(new { error = $"File '{f.FileName}' không hỗ trợ ({f.ContentType}). Chỉ nhận ảnh JPG/PNG/WEBP. (PDF: tách thành ảnh từng trang)" });

                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                rawBytes.Add((f.FileName, bytes));
                var dataUrl = $"data:{f.ContentType};base64,{Convert.ToBase64String(bytes)}";
                uploads.Add(new VisaExtractionService.UploadFile(f.FileName, dataUrl));
            }
            if (uploads.Count == 0) return Results.BadRequest(new { error = "File rỗng" });

            store.Purge();   // dọn rác cũ trước khi lưu mới
            var id = Guid.NewGuid().ToString("N");
            try
            {
                var (extraction, name, country) = await extractor.ExtractAsync(uploads, provider, model, apiKey, ct);

                // Lưu file gốc tạm (tự xóa sau 7 ngày)
                for (int i = 0; i < rawBytes.Count; i++)
                    store.Save(id, i, rawBytes[i].name, rawBytes[i].data);

                var now = DateTime.UtcNow.ToString("o");
                var assessment = new VisaAssessment(
                    Id: id,
                    ApplicantName: !string.IsNullOrWhiteSpace(applicantName) ? applicantName
                                   : (!string.IsNullOrWhiteSpace(name) ? name! : "Hồ sơ chưa đặt tên"),
                    Country: country,
                    Status: "extracted",
                    Extraction: extraction,
                    Result: null,
                    FileCount: uploads.Count,
                    FilesPurged: false,
                    CreatedAt: now,
                    UpdatedAt: now);

                repo.Save(assessment);
                return Results.Json(assessment);
            }
            catch (InvalidOperationException ex)   // ví dụ: provider không có vision
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Đọc hồ sơ lỗi: " + ex.Message }, statusCode: 502);
            }
        }).DisableAntiforgery();

        // ─── POST /visa/assess/{id}/score ─── chấm điểm (bước 2) ─────────────────
        v1.MapPost("/visa/assess/{id}/score", async (string id, VisaScoreRequest req,
            VisaScoringService scorer, VisaRepository repo, CancellationToken ct) =>
        {
            var a = repo.Get(id);
            if (a is null) return Results.NotFound(new { error = "Không tìm thấy hồ sơ" });

            var profile = !string.IsNullOrWhiteSpace(req.Profile) ? req.Profile! : a.Extraction.Profile;
            if (string.IsNullOrWhiteSpace(profile))
                return Results.BadRequest(new { error = "Hồ sơ rỗng — không có gì để chấm" });

            try
            {
                var result = await scorer.ScoreAsync(profile, a.Country, req.Provider, req.Model, req.ApiKey, ct);
                // Lưu lại cả profile NV đã sửa (nếu có)
                var updated = a with
                {
                    Status = "scored",
                    Result = result,
                    Extraction = a.Extraction with { Profile = profile },
                    UpdatedAt = DateTime.UtcNow.ToString("o")
                };
                repo.Save(updated);
                return Results.Json(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Chấm điểm lỗi: " + ex.Message }, statusCode: 502);
            }
        });

        // ─── GET /visa/assessments ─── lịch sử ───────────────────────────────────
        v1.MapGet("/visa/assessments", (VisaRepository repo) => Results.Json(repo.All()));

        // ─── GET /visa/assessments/{id} ─── chi tiết ─────────────────────────────
        v1.MapGet("/visa/assessments/{id}", (string id, VisaRepository repo) =>
        {
            var a = repo.Get(id);
            return a is null ? Results.NotFound(new { error = "Không tìm thấy" }) : Results.Json(a);
        });

        // ─── DELETE /visa/assessments/{id} ─── xóa kết quả + file ────────────────
        v1.MapDelete("/visa/assessments/{id}", (string id, VisaRepository repo, VisaFileStore store) =>
        {
            store.DeleteAssessment(id);
            return repo.Delete(id) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Không tìm thấy" });
        });
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
