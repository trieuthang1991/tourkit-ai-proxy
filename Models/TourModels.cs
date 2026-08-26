using System.Text.Json;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// Nháp tour AI sinh ra (itinerary + marketing + costing). Lưu Redis/file theo tenant.
/// Các trường lồng (request/itinerary/marketing/rows) giữ nguyên dạng JS gửi lên (JsonElement).
public record SavedTour(
    [property: JsonPropertyName("id")]             string Id,
    [property: JsonPropertyName("title")]          string? Title,
    [property: JsonPropertyName("request")]        JsonElement Request,
    [property: JsonPropertyName("itinerary")]      JsonElement Itinerary,
    [property: JsonPropertyName("marketing")]      JsonElement Marketing,
    [property: JsonPropertyName("rows")]           JsonElement Rows,
    [property: JsonPropertyName("nccCoveragePct")] int NccCoveragePct,
    [property: JsonPropertyName("createdAt")]      string CreatedAt,
    [property: JsonPropertyName("createdBy")]      string? CreatedBy,
    [property: JsonPropertyName("status")]         string Status = "draft",  // draft | sent | success — badge ở Wizard landing

    // ── Đồng bộ CRM (sheet bug 103 + 104) ────────────────────────────────────────────────────
    // Nháp đã đẩy sang CRM chưa, thành đơn loại gì, lúc nào. Giữ lại để: (a) không tạo trùng đơn khi
    // bấm đồng bộ lần hai, (b) màn Tính giá Tour hiện được "đã lên CRM · mã ...", trả lời đúng câu
    // hỏi "tour tạo ở đây rồi đi đâu về đâu".
    [property: JsonPropertyName("crmTourId")]      int? CrmTourId = null,
    [property: JsonPropertyName("crmTourType")]    int? CrmTourType = null,    // 3 = GIT (tours) · 2 = FIT (tour_samples)
    [property: JsonPropertyName("crmTourCode")]    string? CrmTourCode = null,
    [property: JsonPropertyName("crmSyncedAt")]    string? CrmSyncedAt = null  // ISO-8601
);
