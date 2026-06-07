using System.IO.Compression;
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
    private const long MaxBytes = 25L * 1024 * 1024;   // 25MB/file
    // ZIP: giới hạn cao hơn — user thường nén 1 bộ hồ sơ full vào 1 file.
    private const int MaxFilesAfterUnzip = 30;
    private const long MaxTotalUnzippedBytes = 300L * 1024 * 1024;   // chống zip bomb (PDF/DOCX có thể to)

    // Whitelist 3 nhóm — quyết định cách gửi cho AI:
    //   Image → vision (Images field)
    //   PDF   → document (Documents field, Claude+OpenAI Responses đọc trực tiếp)
    //   DOCX  → extract text bằng OpenXml → bỏ vào prompt
    private static readonly HashSet<string> ImageTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif" };
    private const string PdfType  = "application/pdf";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static bool IsAllowed(string contentType, string fileName)
    {
        if (ImageTypes.Contains(contentType)) return true;
        if (string.Equals(contentType, PdfType, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(contentType, DocxType, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsZipFile(contentType, fileName)) return true;
        // Fallback theo phần mở rộng (1 số browser/Windows gửi MIME generic)
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".pdf" or ".docx" or ".zip";
    }

    private static bool IsZipFile(string contentType, string fileName)
    {
        if (string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// Giải nén ZIP an toàn: bỏ folder entry, hidden file, __MACOSX, path traversal;
    /// reject zip bomb (mỗi entry ≤ 25MB, tổng ≤ 300MB). Trả list (name, bytes).
    private static List<(string Name, byte[] Data)> ExpandZip(byte[] zipBytes)
    {
        var result = new List<(string, byte[])>();
        long total = 0;
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            // Skip folder / file rỗng / metadata
            if (entry.Length == 0) continue;
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            if (entry.FullName.Contains("..", StringComparison.Ordinal))
                throw new IOException($"Entry '{entry.FullName}' có path traversal — từ chối");
            var fileName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            if (fileName.StartsWith('.')) continue;                                                 // .DS_Store, ._*
            if (entry.FullName.StartsWith("__MACOSX", StringComparison.Ordinal)) continue;

            // Zip bomb defense
            if (entry.Length > MaxBytes)
                throw new IOException($"Entry '{fileName}' trong ZIP vượt {MaxBytes / 1024 / 1024}MB");
            total += entry.Length;
            if (total > MaxTotalUnzippedBytes)
                throw new IOException($"ZIP vượt {MaxTotalUnzippedBytes / 1024 / 1024}MB sau giải nén");

            using var es = entry.Open();
            using var em = new MemoryStream();
            es.CopyTo(em);
            result.Add((fileName, em.ToArray()));
        }
        return result;
    }

    /// Đoán MIME từ phần mở rộng — dùng cho entry trong ZIP (ZipArchive không trả ContentType).
    private static string GuessMimeFromName(string fileName) => Path.GetExtension(fileName)?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => PdfType,
        ".docx" => DocxType,
        _ => "application/octet-stream"
    };

    private static (VisaExtractionService.UploadKind Kind, string? ResolvedMime) ClassifyFile(string contentType, string fileName)
    {
        if (ImageTypes.Contains(contentType)) return (VisaExtractionService.UploadKind.Image, contentType);
        if (string.Equals(contentType, PdfType, StringComparison.OrdinalIgnoreCase)) return (VisaExtractionService.UploadKind.Pdf, PdfType);
        if (string.Equals(contentType, DocxType, StringComparison.OrdinalIgnoreCase)) return (VisaExtractionService.UploadKind.Text, DocxType);
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => (VisaExtractionService.UploadKind.Image, "image/jpeg"),
            ".png"  => (VisaExtractionService.UploadKind.Image, "image/png"),
            ".webp" => (VisaExtractionService.UploadKind.Image, "image/webp"),
            ".gif"  => (VisaExtractionService.UploadKind.Image, "image/gif"),
            ".pdf"  => (VisaExtractionService.UploadKind.Pdf,   PdfType),
            ".docx" => (VisaExtractionService.UploadKind.Text,  DocxType),
            _ => (VisaExtractionService.UploadKind.Image, contentType)
        };
    }

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

            // Bước A: ĐỌC tất cả file user upload → mở rộng ZIP (1 zip → N entry) thành flat list.
            var flat = new List<(string Name, string ContentType, byte[] Data)>();
            foreach (var f in files)
            {
                if (f.Length == 0) continue;
                if (f.Length > MaxBytes)
                    return Results.BadRequest(new { error = $"File '{f.FileName}' vượt {MaxBytes / 1024 / 1024}MB" });
                if (!IsAllowed(f.ContentType, f.FileName))
                    return Results.BadRequest(new { error = $"File '{f.FileName}' không hỗ trợ ({f.ContentType}). Chỉ nhận ảnh (JPG/PNG/WEBP/GIF), PDF, DOCX hoặc ZIP. (DOC cũ 97-2003 không hỗ trợ — convert sang DOCX/PDF)" });

                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                if (IsZipFile(f.ContentType, f.FileName))
                {
                    List<(string Name, byte[] Data)> entries;
                    try { entries = ExpandZip(bytes); }
                    catch (Exception ex) { return Results.BadRequest(new { error = $"ZIP '{f.FileName}' lỗi: {ex.Message}" }); }

                    foreach (var (name, data) in entries)
                    {
                        // Bỏ qua entry không phải loại hỗ trợ (vd .txt, .jpg trong subfolder lạ)
                        if (!IsAllowed("", name)) continue;
                        var mime = GuessMimeFromName(name);
                        flat.Add((name, mime, data));
                    }
                }
                else
                {
                    flat.Add((f.FileName, f.ContentType, bytes));
                }
            }

            if (flat.Count == 0) return Results.BadRequest(new { error = "Không tìm thấy file hồ sơ hợp lệ (sau khi giải nén ZIP nếu có)" });
            if (flat.Count > MaxFilesAfterUnzip) return Results.BadRequest(new { error = $"Tổng {flat.Count} file (kể cả trong ZIP) vượt giới hạn {MaxFilesAfterUnzip}" });

            // Bước B: classify + build UploadFile (như flow cũ, dùng list flat đã giải nén)
            var uploads = new List<VisaExtractionService.UploadFile>();
            var rawBytes = new List<(string name, byte[] data)>();
            foreach (var (name, contentType, bytes) in flat)
            {
                rawBytes.Add((name, bytes));
                var (kind, mime) = ClassifyFile(contentType, name);
                if (kind == VisaExtractionService.UploadKind.Text)
                {
                    string text;
                    try { text = DocxExtractor.ExtractText(bytes); }
                    catch (Exception ex) { return Results.BadRequest(new { error = $"File '{name}' không phải DOCX hợp lệ: {ex.Message}" }); }
                    if (string.IsNullOrWhiteSpace(text))
                        return Results.BadRequest(new { error = $"File '{name}' rỗng — không trích được text" });
                    uploads.Add(new VisaExtractionService.UploadFile(name, "", VisaExtractionService.UploadKind.Text, text));
                }
                else
                {
                    var dataUrl = $"data:{mime ?? contentType};base64,{Convert.ToBase64String(bytes)}";
                    uploads.Add(new VisaExtractionService.UploadFile(name, dataUrl, kind));
                }
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
