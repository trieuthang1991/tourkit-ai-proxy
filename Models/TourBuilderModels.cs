using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// AI bóc tách 1 đoạn mô tả tự do → form Tour GIT (Type=3). Schema BÁM sát mobile TourCreate.razor
/// khối: Thông tin tour + Người đặt + Phần thu + Dịch vụ điều hành. Phép null mọi field — AI điền
/// được gì điền nấy; NV bổ sung phần thiếu trên form bên phải.
public record TourBuilderDraft(
    [property: JsonPropertyName("title")]        string? Title,
    [property: JsonPropertyName("marketName")]   string? MarketName,
    [property: JsonPropertyName("tourType")]     string? TourType,        // Nội địa | Inbound | Outbound
    [property: JsonPropertyName("startDate")]    string? StartDate,        // yyyy-MM-dd
    [property: JsonPropertyName("endDate")]      string? EndDate,
    [property: JsonPropertyName("adultCount")]   int? AdultCount,
    [property: JsonPropertyName("childCount")]   int? ChildCount,
    [property: JsonPropertyName("customerName")] string? CustomerName,
    [property: JsonPropertyName("customerPhone")] string? CustomerPhone,
    [property: JsonPropertyName("customerEmail")] string? CustomerEmail,
    [property: JsonPropertyName("note")]         string? Note,
    [property: JsonPropertyName("expenses")]     List<TourBuilderExpense> Expenses,    // Phần thu
    [property: JsonPropertyName("services")]     List<TourBuilderServiceItem> Services,    // Dịch vụ điều hành (chi)
    [property: JsonPropertyName("warnings")]     List<string> Warnings                 // AI lưu ý NV (vd: ngày không rõ)
);

public record TourBuilderExpense(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("unitPrice")] long UnitPrice,
    [property: JsonPropertyName("quantity")]  int Quantity,
    [property: JsonPropertyName("vatPercent")] double VatPercent
);

public record TourBuilderServiceItem(
    [property: JsonPropertyName("name")]         string Name,        // vd "Khách sạn ABC 3 đêm"
    [property: JsonPropertyName("providerName")] string? ProviderName,
    [property: JsonPropertyName("quantity")]     int Quantity,
    [property: JsonPropertyName("nights")]       int Nights,
    [property: JsonPropertyName("netPrice")]     long NetPrice,
    [property: JsonPropertyName("vatPercent")]   double VatPercent
);

public record TourBuilderRequest(
    [property: JsonPropertyName("prompt")]   string Prompt,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")]    string? Model,
    [property: JsonPropertyName("apiKey")]   string? ApiKey
);
