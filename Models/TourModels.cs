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
    [property: JsonPropertyName("status")]         string Status = "draft"   // draft | sent | success — badge ở Wizard landing
);
