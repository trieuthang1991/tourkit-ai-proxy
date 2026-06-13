using SharpCompress.Archives;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourKit;
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
        if (IsArchiveFile(contentType, fileName)) return true;
        // Fallback theo phần mở rộng (1 số browser/Windows gửi MIME generic)
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".pdf" or ".docx" or ".zip" or ".rar";
    }

    private static bool IsArchiveFile(string contentType, string fileName)
    {
        var ct = contentType?.ToLowerInvariant() ?? "";
        if (ct is "application/zip" or "application/x-zip-compressed"
              or "application/x-rar-compressed" or "application/vnd.rar" or "application/x-rar") return true;
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext is ".zip" or ".rar";
    }

    /// Giải nén ZIP/RAR an toàn (qua SharpCompress — auto-detect format):
    /// bỏ folder entry, hidden file, __MACOSX, path traversal; reject bomb
    /// (mỗi entry ≤ 25MB, tổng ≤ 300MB). Trả list (name, bytes).
    private static List<(string Name, byte[] Data)> ExpandArchive(byte[] archiveBytes)
    {
        var result = new List<(string, byte[])>();
        long total = 0;
        using var ms = new MemoryStream(archiveBytes);
        using var archive = ArchiveFactory.OpenArchive(ms);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            string fullName = (entry.Key ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(fullName)) continue;
            if (fullName.Contains("..", StringComparison.Ordinal))
                throw new IOException($"Entry '{fullName}' có path traversal — từ chối");

            string fileName = Path.GetFileName(fullName) ?? "";
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            if (fileName.StartsWith('.')) continue;                                       // .DS_Store, ._*
            if (fullName.StartsWith("__MACOSX", StringComparison.Ordinal)) continue;

            // Bomb defense
            if (entry.Size > MaxBytes)
                throw new IOException($"Entry '{fileName}' trong archive vượt {MaxBytes / 1024 / 1024}MB");
            total += entry.Size;
            if (total > MaxTotalUnzippedBytes)
                throw new IOException($"Archive vượt {MaxTotalUnzippedBytes / 1024 / 1024}MB sau giải nén");

            using var es = entry.OpenEntryStream();
            using var em = new MemoryStream();
            es.CopyTo(em);
            if (em.Length == 0) continue;
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

        // ─── POST /visa/score-wizard ─── 9 câu hỏi + files → AI chấm ─────────────
        // Multipart form:
        //   - "answers"  (text JSON) : VisaWizardAnswers
        //   - "filesMeta" (text JSON) : List<VisaWizardFileSlot> — metadata file (docKey,docLabel,count,bytes)
        //   - files[]    (binary) : file thực — lưu store, KHÔNG đọc OCR/vision ở phase này
        //   - "provider", "model", "apiKey" (text optional) : AI prefs override
        // Output: VisaResult (passRate, level, strengths, weaknesses, missingDocs, suggestions, summary).
        // ─── POST /visa/lead ─── User bấm "Liên hệ tư vấn" ở result → log lead ───
        // Phase 1: append vào data/visa-leads.jsonl (sales team đọc thủ công).
        // Phase 2: wire POST /api/booking-tickets vào TourKit CRM (cần upstream support).
        v1.MapPost("/visa/lead", async (HttpRequest request, HttpContext ctx, TkSessionStore sessions, IWebHostEnvironment env, ILogger<Program> log) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new { error = "Body rỗng." });
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var dir = Path.Combine(env.ContentRootPath, "data");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "visa-leads.jsonl");
                var entry = new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    tenant,
                    payload = doc.RootElement
                };
                await File.AppendAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(entry) + "\n");
                return Results.Json(new { saved = true, message = "Đã ghi nhận, bộ phận visa sẽ liên hệ trong 1-2 giờ làm việc." });
            }
            catch (Exception ex) { log.LogError(ex, "Visa lead append fail"); return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // ─── GET /visa/questions ─── per-tenant config (null = dùng default frontend) ───
        v1.MapGet("/visa/questions", (HttpContext ctx, TkSessionStore sessions, VisaQuestionRepository qrepo) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            var cfg = qrepo.Get(tenant);
            return Results.Json(new
            {
                hasOverride = cfg != null,
                questionsJson = cfg?.QuestionsJson,
                updatedBy = cfg?.UpdatedBy,
                updatedAt = cfg?.UpdatedAt
            });
        });

        // ─── PUT /visa/questions ─── upsert config (body = raw JSON array) ───
        v1.MapPut("/visa/questions", async (HttpRequest request, HttpContext ctx, TkSessionStore sessions, VisaQuestionRepository qrepo) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (sid, tenant) = auth.Value;
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new { error = "Body rỗng." });
            // Validate JSON array
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Phải là JSON array của câu hỏi." });
                if (doc.RootElement.GetArrayLength() == 0)
                    return Results.BadRequest(new { error = "Cần ít nhất 1 câu hỏi." });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "JSON sai cú pháp: " + ex.Message }); }

            var updatedBy = sessions.Get(sid)?.FullName ?? sid;
            var ok = qrepo.Save(tenant, body, updatedBy);
            return ok ? Results.Json(new { saved = true }) : Results.Json(new { error = "Lưu lỗi" }, statusCode: 500);
        });

        // ─── DELETE /visa/questions ─── reset về default ───
        v1.MapDelete("/visa/questions", (HttpContext ctx, TkSessionStore sessions, VisaQuestionRepository qrepo) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            qrepo.Delete(tenant);
            return Results.Json(new { reset = true });
        });

        v1.MapPost("/visa/score-wizard", async (HttpRequest request, HttpContext ctx, TkSessionStore sessions,
            VisaScoringService scorer, VisaRepository repo, VisaExtractionService extractor, ILogger<Program> log, CancellationToken ct) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Cần multipart/form-data với 'answers' + 'filesMeta'." });

            var form = await request.ReadFormAsync(ct);
            var answersJson = form["answers"].ToString();
            var filesMetaJson = form["filesMeta"].ToString();
            if (string.IsNullOrWhiteSpace(answersJson))
                return Results.BadRequest(new { error = "Thiếu trường 'answers'." });

            // PropertyNameCaseInsensitive: frontend gửi camelCase (docKey, count) — record PascalCase (DocKey, Count).
            // System.Text.Json default case-sensitive → properties null/0 → AI tưởng "0 file" dù FE gửi đủ.
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            VisaWizardAnswers? answers;
            try { answers = System.Text.Json.JsonSerializer.Deserialize<VisaWizardAnswers>(answersJson, jsonOpts); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Lỗi parse 'answers': " + ex.Message }); }
            if (answers == null) return Results.BadRequest(new { error = "'answers' rỗng." });

            List<VisaWizardFileSlot> filesMeta = new();
            if (!string.IsNullOrWhiteSpace(filesMetaJson))
            {
                try { filesMeta = System.Text.Json.JsonSerializer.Deserialize<List<VisaWizardFileSlot>>(filesMetaJson, jsonOpts) ?? new(); }
                catch (Exception ex) { return Results.BadRequest(new { error = "Lỗi parse 'filesMeta': " + ex.Message }); }
            }

            var provider = form["provider"].ToString();
            var model    = form["model"].ToString();
            var apiKey   = form["apiKey"].ToString();

            // Vision OCR: nếu có file thật trong form → đọc nội dung qua Anthropic Vision rồi gắn vào prompt.
            // Mỗi file ~600-1000 token (Haiku rẻ). 6 file × ~30đ + scoring ~30đ = ~210đ/lần (Premium quality).
            // Lỗi vision (provider chưa support, hết quota) → fail-soft, scoring vẫn chạy với metadata only.
            List<VisaFileExtraction> extractedFiles = new();
            try
            {
                var uploads = new List<VisaExtractionService.UploadFile>();
                foreach (var f in form.Files)
                {
                    if (f.Length == 0 || f.Length > 25L * 1024 * 1024) continue;   // skip rỗng/quá lớn
                    var (kind, mime) = ClassifyFile(f.ContentType ?? "", f.FileName);
                    if (kind == VisaExtractionService.UploadKind.Text) continue;   // skip DOCX (cần extract riêng, deferred)
                    using var ms = new MemoryStream();
                    await f.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    var dataUrl = $"data:{mime ?? f.ContentType};base64,{Convert.ToBase64String(bytes)}";
                    // tên hiển thị: bỏ prefix "{docKey}__" nếu có (frontend gửi vd "passport__01-ho-chieu-mau.pdf")
                    var displayName = f.FileName;
                    var sepIdx = displayName.IndexOf("__", StringComparison.Ordinal);
                    if (sepIdx > 0) displayName = displayName[(sepIdx + 2)..];
                    uploads.Add(new VisaExtractionService.UploadFile(displayName, dataUrl, kind));
                }
                if (uploads.Count > 0)
                {
                    var (extraction, _, _) = await extractor.ExtractAsync(uploads,
                        string.IsNullOrWhiteSpace(provider) ? "anthropic" : provider,
                        string.IsNullOrWhiteSpace(model) ? null : model,
                        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, ct);
                    extractedFiles = extraction.Files ?? new();
                }
            }
            catch (Exception visionEx)
            {
                log.LogWarning(visionEx, "Vision OCR failed, fall back to metadata-only scoring");
                // không return — vẫn cho score chạy không vision
            }

            try
            {
                var result = await scorer.ScoreWizardAsync(answers, filesMeta,
                    string.IsNullOrWhiteSpace(provider) ? null : provider,
                    string.IsNullOrWhiteSpace(model) ? null : model,
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                    ct, extractedFiles);

                // Lưu lịch sử vào dbo.VisaAssessments — wizard answers → Extraction.Profile,
                // file metadata → Files. Để analytics + NV xem lại sau.
                try
                {
                    var id = "VW-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "-" + Guid.NewGuid().ToString("N")[..6];
                    var nowIso = DateTime.UtcNow.ToString("o");
                    var fileExts = filesMeta.Select(f => new VisaFileExtraction(
                        FileName: f.DocLabel, DocType: f.DocKey, DocTypeLabel: f.DocLabel,
                        Summary: $"{f.Count} file ({f.TotalBytes / 1024} KB)", Readable: true, Note: null)).ToList();
                    var extraction = new VisaExtraction(
                        Profile: System.Text.Json.JsonSerializer.Serialize(answers,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                        Files: fileExts);
                    var assessment = new VisaAssessment(
                        Id: id,
                        ApplicantName: answers.Contact?.FullName ?? "(Wizard)",
                        Country: answers.Country,
                        Status: "scored",
                        Extraction: extraction, Result: result,
                        FileCount: filesMeta.Sum(f => f.Count),
                        FilesPurged: true,   // wizard không lưu file binary, chỉ metadata
                        CreatedAt: nowIso, UpdatedAt: nowIso);
                    repo.Save(tenant, assessment);
                }
                catch { /* fail-soft: không lưu được cũng vẫn trả result cho user */ }

                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // ─── POST /visa/assess ─── upload + AI đọc hồ sơ (bước 1) ────────────────
        v1.MapPost("/visa/assess", async (HttpRequest request, HttpContext ctx, TkSessionStore sessions,
            VisaExtractionService extractor, VisaRepository repo, VisaFileStore store, CancellationToken ct) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;

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
                    return Results.BadRequest(new { error = $"File '{f.FileName}' không hỗ trợ ({f.ContentType}). Chỉ nhận ảnh (JPG/PNG/WEBP/GIF), PDF, DOCX, ZIP hoặc RAR. (DOC cũ 97-2003 không hỗ trợ — convert sang DOCX/PDF)" });

                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                if (IsArchiveFile(f.ContentType, f.FileName))
                {
                    List<(string Name, byte[] Data)> entries;
                    try { entries = ExpandArchive(bytes); }
                    catch (Exception ex) { return Results.BadRequest(new { error = $"Archive '{f.FileName}' lỗi: {ex.Message}" }); }

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
                    store.Save(tenant, id, i, rawBytes[i].name, rawBytes[i].data);

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

                repo.Save(tenant, assessment);
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
            HttpContext ctx, TkSessionStore sessions,
            VisaScoringService scorer, VisaRepository repo,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace, CancellationToken ct) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;

            var a = repo.Get(tenant, id);
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
                repo.Save(tenant, updated);
                // Đính trace nếu ?debug=1 / X-Debug header
                var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
                if (traceObj != null) return Results.Json(new { assessment = updated, _trace = traceObj });
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
        v1.MapGet("/visa/assessments", (HttpContext ctx, TkSessionStore sessions, VisaRepository repo) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            return Results.Json(repo.All(tenant));
        });

        // ─── GET /visa/assessments/{id} ─── chi tiết ─────────────────────────────
        v1.MapGet("/visa/assessments/{id}", (string id, HttpContext ctx, TkSessionStore sessions, VisaRepository repo) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            var a = repo.Get(tenant, id);
            return a is null ? Results.NotFound(new { error = "Không tìm thấy" }) : Results.Json(a);
        });

        // ─── DELETE /visa/assessments/{id} ─── xóa kết quả + file ────────────────
        v1.MapDelete("/visa/assessments/{id}", (string id, HttpContext ctx, TkSessionStore sessions, VisaRepository repo, VisaFileStore store) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Unauthorized();
            var (_, tenant) = auth.Value;
            store.DeleteAssessment(tenant, id);
            return repo.Delete(tenant, id) ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Không tìm thấy" });
        });
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// Extract sessionId + tenantId từ request. Return null nếu missing/invalid session.
    /// Handler caller trả 401 nếu null.
    private static (string SessionId, string TenantId)? RequireSession(
        HttpContext ctx, TourkitAiProxy.Services.TourKit.TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
            ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId);
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
