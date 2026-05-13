using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// Khách hàng tour. Đọc từ data/customers.seed.json (read-only seed).
/// Trong production thay bằng table customer thực + repository theo CRM.
public record Customer(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("phone")]      string? Phone,
    [property: JsonPropertyName("email")]      string? Email,
    [property: JsonPropertyName("age")]        int? Age,
    [property: JsonPropertyName("gender")]     string? Gender,
    [property: JsonPropertyName("location")]   string? Location,
    [property: JsonPropertyName("segment")]    string Segment,             // VIP / Thường / Mới
    [property: JsonPropertyName("createdAt")]  string CreatedAt,
    [property: JsonPropertyName("source")]     string? Source,
    [property: JsonPropertyName("metrics")]    CustomerMetrics Metrics,
    [property: JsonPropertyName("purchases")]  List<TourPurchase> Purchases,
    [property: JsonPropertyName("careLogs")]   List<CareLog> CareLogs
);

public record CustomerMetrics(
    [property: JsonPropertyName("totalTours")]          int TotalTours,
    [property: JsonPropertyName("totalSpent")]          long TotalSpent,
    [property: JsonPropertyName("aov")]                 long Aov,
    [property: JsonPropertyName("lastPurchaseDate")]    string? LastPurchaseDate,
    [property: JsonPropertyName("lastPurchaseDaysAgo")] int? LastPurchaseDaysAgo,
    [property: JsonPropertyName("avgDaysBetweenOrders")] int? AvgDaysBetweenOrders,
    [property: JsonPropertyName("careInteractions")]    int CareInteractions,
    [property: JsonPropertyName("lastCareDaysAgo")]     int? LastCareDaysAgo,
    [property: JsonPropertyName("complaintCount")]      int ComplaintCount,
    [property: JsonPropertyName("cancelCount")]         int CancelCount
);

public record TourPurchase(
    [property: JsonPropertyName("date")]        string Date,
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("nights")]      int Nights,
    [property: JsonPropertyName("pax")]         int Pax,
    [property: JsonPropertyName("amount")]      long Amount,
    [property: JsonPropertyName("channel")]     string? Channel              // Online / Showroom / Hotline / Sale
);

public record CareLog(
    [property: JsonPropertyName("date")]      string Date,
    [property: JsonPropertyName("channel")]   string Channel,                // call / zalo / email / inperson
    [property: JsonPropertyName("summary")]   string Summary,
    [property: JsonPropertyName("sentiment")] string Sentiment,              // positive / neutral / negative
    [property: JsonPropertyName("outcome")]   string? Outcome
);

/// List item rút gọn cho /api/v1/customers — kèm trạng thái review hiện tại.
public record CustomerListItem(
    [property: JsonPropertyName("id")]               string Id,
    [property: JsonPropertyName("name")]             string Name,
    [property: JsonPropertyName("segment")]          string Segment,
    [property: JsonPropertyName("totalSpent")]       long TotalSpent,
    [property: JsonPropertyName("totalTours")]       int TotalTours,
    [property: JsonPropertyName("lastPurchaseDaysAgo")] int? LastPurchaseDaysAgo,
    [property: JsonPropertyName("rank")]             string? Rank,           // null = chưa review
    [property: JsonPropertyName("reviewStatus")]     string ReviewStatus,    // none / fresh / stale
    [property: JsonPropertyName("reviewAgeHours")]   int? ReviewAgeHours,
    [property: JsonPropertyName("summaryLine")]      string? SummaryLine
);
