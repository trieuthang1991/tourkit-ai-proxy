using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// 1 lượt thẩm định hồ sơ visa của 1 cá nhân. Lưu data/visa-assessments.json keyed by Id.
/// Vòng đời: upload → Status="extracted" (AI đọc xong hồ sơ) → score → Status="scored".
/// File gốc lưu tạm data/visa-files/{id}/, tự xóa sau 7 ngày (FilesPurged=true khi đã dọn).
public record VisaAssessment(
    [property: JsonPropertyName("id")]          string Id,
    [property: JsonPropertyName("applicantName")] string ApplicantName,
    [property: JsonPropertyName("country")]     string? Country,        // AI tự nhận diện từ hồ sơ
    [property: JsonPropertyName("status")]      string Status,          // extracted | scored
    [property: JsonPropertyName("extraction")]  VisaExtraction Extraction,
    [property: JsonPropertyName("result")]      VisaResult? Result,     // null tới khi chấm điểm
    [property: JsonPropertyName("fileCount")]   int FileCount,
    [property: JsonPropertyName("filesPurged")] bool FilesPurged,
    [property: JsonPropertyName("createdAt")]   string CreatedAt,       // ISO-8601
    [property: JsonPropertyName("updatedAt")]   string UpdatedAt
);

/// Kết quả AI đọc hồ sơ (bước 1). Profile = bản tóm tắt gộp (NV sửa được trước khi chấm).
public record VisaExtraction(
    [property: JsonPropertyName("profile")] string Profile,            // markdown/text gộp toàn hồ sơ — editable
    [property: JsonPropertyName("files")]   List<VisaFileExtraction> Files
);

/// Thông tin AI trích từ 1 file. Readable=false khi ảnh mờ/không đọc được.
public record VisaFileExtraction(
    [property: JsonPropertyName("fileName")]     string FileName,
    [property: JsonPropertyName("docType")]      string DocType,       // passport|bank_statement|employment|property|... |unknown
    [property: JsonPropertyName("docTypeLabel")] string DocTypeLabel,  // nhãn tiếng Việt
    [property: JsonPropertyName("summary")]      string Summary,       // 1-3 câu nội dung chính
    [property: JsonPropertyName("readable")]     bool Readable,
    [property: JsonPropertyName("note")]         string? Note
);

/// Kết quả chấm điểm (bước 2).
public record VisaResult(
    [property: JsonPropertyName("passRate")]    int PassRate,          // 0-100 (%)
    [property: JsonPropertyName("level")]       string Level,          // cao | trung_binh | thap
    [property: JsonPropertyName("strengths")]   List<string> Strengths,
    [property: JsonPropertyName("weaknesses")]  List<string> Weaknesses,
    [property: JsonPropertyName("missingDocs")] List<string> MissingDocs,
    [property: JsonPropertyName("suggestions")] List<string> Suggestions,
    [property: JsonPropertyName("summary")]     string Summary,
    [property: JsonPropertyName("aiModel")]     string? AiModel,
    [property: JsonPropertyName("aiProvider")]  string? AiProvider
);

// ─── Request DTOs ─────────────────────────────────────────────────────────────
/// Body cho POST /api/v1/visa/assess/{id}/score — hồ sơ (đã sửa) + AI prefs.
public record VisaScoreRequest(
    [property: JsonPropertyName("profile")]  string? Profile,   // bản hồ sơ NV đã sửa (null = dùng bản AI đọc)
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")]    string? Model,
    [property: JsonPropertyName("apiKey")]   string? ApiKey
);

// ─── Wizard scoring (9 câu hỏi + upload) ─────────────────────────────────────
/// Câu trả lời từ wizard /visa — flat object để serialize gọn vào prompt.
public record VisaWizardAnswers(
    [property: JsonPropertyName("country")]           string? Country,
    [property: JsonPropertyName("maritalStatus")]     string? MaritalStatus,
    [property: JsonPropertyName("highRiskProvince")]  string? HighRiskProvince,
    [property: JsonPropertyName("travelHistory")]     List<string>? TravelHistory,
    [property: JsonPropertyName("visaRefusal")]       string? VisaRefusal,
    [property: JsonPropertyName("occupation")]        string? Occupation,
    [property: JsonPropertyName("income")]            string? Income,
    [property: JsonPropertyName("financialAssets")]   List<string>? FinancialAssets,
    [property: JsonPropertyName("contact")]           VisaWizardContact? Contact);
public record VisaWizardContact(string? FullName, string? Phone, string? Email);

/// Metadata file đã upload (frontend gửi multipart). NOT đọc content — chỉ list để AI biết
/// "user đã có giấy tờ X". OCR/vision read phase 2.
public record VisaWizardFileSlot(string DocKey, string DocLabel, int Count, long TotalBytes);
