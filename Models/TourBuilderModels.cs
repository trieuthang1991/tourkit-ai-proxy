using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// AI bóc tách 1 đoạn mô tả tự do → form Tour GIT (Type=3). 3 khối:
/// Thông tin tour + Khách đại diện + Phần thu (revenue Sale thu khách). Phép null mọi field.
/// LƯU Ý 2026-06-13: KHỐI "Dịch vụ điều hành (chi)" đã BỎ — phần chi NCC dùng Wizard báo giá riêng.
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
    [property: JsonPropertyName("warnings")]     List<string> Warnings                 // AI lưu ý NV (vd: ngày không rõ)
);

public record TourBuilderExpense(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("unitPrice")] long UnitPrice,
    [property: JsonPropertyName("quantity")]  int Quantity,
    [property: JsonPropertyName("vatPercent")] double VatPercent
);

public record TourBuilderRequest(
    [property: JsonPropertyName("prompt")]   string Prompt,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")]    string? Model,
    [property: JsonPropertyName("apiKey")]   string? ApiKey
);
