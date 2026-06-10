using System.Text.Json;
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// <summary>
/// 1 báo giá tour user lưu lại. Persist trong dbo.TourQuotes (per-tenant scope).
/// `DataJson` chứa full form (expenses/services/note/warnings/markup state…) — raw passthrough cho FE,
/// các field index hóa (Title/CustomerName/Total/Margin…) để filter + sort không phải parse JSON.
/// </summary>
public record TourQuote(
    [property: JsonPropertyName("id")]             string Id,
    [property: JsonPropertyName("title")]          string? Title,
    [property: JsonPropertyName("customerName")]   string? CustomerName,
    [property: JsonPropertyName("customerPhone")]  string? CustomerPhone,
    [property: JsonPropertyName("marketName")]     string? MarketName,
    [property: JsonPropertyName("tourType")]       string? TourType,
    [property: JsonPropertyName("startDate")]      string? StartDate,
    [property: JsonPropertyName("endDate")]        string? EndDate,
    [property: JsonPropertyName("adultCount")]     int AdultCount,
    [property: JsonPropertyName("childCount")]     int ChildCount,
    [property: JsonPropertyName("totalNet")]       long TotalNet,
    [property: JsonPropertyName("totalRevenue")]   long TotalRevenue,
    [property: JsonPropertyName("profit")]         long Profit,
    [property: JsonPropertyName("marginPercent")]  double? MarginPercent,
    [property: JsonPropertyName("data")]           JsonElement Data,           // full form JSON
    [property: JsonPropertyName("createdBy")]      string? CreatedBy,
    [property: JsonPropertyName("createdAt")]      string CreatedAt,           // ISO
    [property: JsonPropertyName("updatedAt")]      string UpdatedAt
);

/// List item rút gọn (không kèm DataJson) — cho /tour-quotes list endpoint.
public record TourQuoteListItem(
    [property: JsonPropertyName("id")]             string Id,
    [property: JsonPropertyName("title")]          string? Title,
    [property: JsonPropertyName("customerName")]   string? CustomerName,
    [property: JsonPropertyName("customerPhone")]  string? CustomerPhone,
    [property: JsonPropertyName("marketName")]     string? MarketName,
    [property: JsonPropertyName("startDate")]      string? StartDate,
    [property: JsonPropertyName("endDate")]        string? EndDate,
    [property: JsonPropertyName("adultCount")]     int AdultCount,
    [property: JsonPropertyName("childCount")]     int ChildCount,
    [property: JsonPropertyName("totalNet")]       long TotalNet,
    [property: JsonPropertyName("totalRevenue")]   long TotalRevenue,
    [property: JsonPropertyName("profit")]         long Profit,
    [property: JsonPropertyName("marginPercent")]  double? MarginPercent,
    [property: JsonPropertyName("createdBy")]      string? CreatedBy,
    [property: JsonPropertyName("createdAt")]      string CreatedAt,
    [property: JsonPropertyName("updatedAt")]      string UpdatedAt
);

/// Body POST /api/v1/tour-quotes (save). Id null → tạo mới (server sinh); có id → update.
public record SaveTourQuoteRequest(
    [property: JsonPropertyName("id")]             string? Id,
    [property: JsonPropertyName("title")]          string? Title,
    [property: JsonPropertyName("customerName")]   string? CustomerName,
    [property: JsonPropertyName("customerPhone")]  string? CustomerPhone,
    [property: JsonPropertyName("marketName")]     string? MarketName,
    [property: JsonPropertyName("tourType")]       string? TourType,
    [property: JsonPropertyName("startDate")]      string? StartDate,
    [property: JsonPropertyName("endDate")]        string? EndDate,
    [property: JsonPropertyName("adultCount")]     int AdultCount,
    [property: JsonPropertyName("childCount")]     int ChildCount,
    [property: JsonPropertyName("totalNet")]       long TotalNet,
    [property: JsonPropertyName("totalRevenue")]   long TotalRevenue,
    [property: JsonPropertyName("profit")]         long Profit,
    [property: JsonPropertyName("marginPercent")]  double? MarginPercent,
    [property: JsonPropertyName("data")]           JsonElement Data
);
