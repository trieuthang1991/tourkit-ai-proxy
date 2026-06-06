using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// Cơ hội bán hàng (booking-ticket) dạng nhẹ từ /api/ai/booking-tickets — đầu vào heuristic.
public record DealOpportunity(
    int Id,
    string? Code,
    string? CustomerName,
    string? Phone,
    string? Title,
    long TotalPrice,
    int Status,
    string? StatusName,
    int Source,
    string? SourceName,
    string? MarketName,
    string? Assignees,
    string CreatedAt,      // ISO
    int AgeDays            // ngày kể từ tạo
);

/// Kết quả AI chấm sâu 1 deal (dùng detail + lịch sử hành động Sale).
public record DealScore(
    [property: JsonPropertyName("winRate")]    int WinRate,       // 0-100
    [property: JsonPropertyName("level")]      string Level,      // cao|trung_binh|thap
    [property: JsonPropertyName("signals")]    List<string> Signals,
    [property: JsonPropertyName("risks")]      List<string> Risks,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("reason")]     string Reason,
    [property: JsonPropertyName("aiModel")]    string? AiModel,
    [property: JsonPropertyName("aiProvider")] string? AiProvider
);

/// 1 dòng trên bảng xếp hạng ưu tiên (heuristic + AI gộp).
public record DealBoardItem(
    [property: JsonPropertyName("id")]            int Id,
    [property: JsonPropertyName("code")]          string? Code,
    [property: JsonPropertyName("customerName")]  string CustomerName,
    [property: JsonPropertyName("phone")]         string? Phone,
    [property: JsonPropertyName("title")]         string? Title,
    [property: JsonPropertyName("totalPrice")]    long TotalPrice,
    [property: JsonPropertyName("statusName")]    string? StatusName,
    [property: JsonPropertyName("sourceName")]    string? SourceName,
    [property: JsonPropertyName("assignees")]     string? Assignees,
    [property: JsonPropertyName("ageDays")]       int AgeDays,
    [property: JsonPropertyName("winRate")]       int? WinRate,         // null nếu chỉ heuristic (chưa chấm sâu)
    [property: JsonPropertyName("level")]         string? Level,
    [property: JsonPropertyName("priorityScore")] double PriorityScore, // 0-100
    [property: JsonPropertyName("expectedValue")] long ExpectedValue,   // winRate% × giá trị
    [property: JsonPropertyName("deep")]          bool Deep,            // đã AI chấm sâu chưa
    [property: JsonPropertyName("riskFlag")]      string? RiskFlag,     // nguoi|sap_khoi_hanh|null
    [property: JsonPropertyName("analysis")]      DealScore? Analysis
);

/// Bảng đã cache (cho GET /deals/board mở lại không cần chạy lại).
public record DealBoard(
    [property: JsonPropertyName("items")]       List<DealBoardItem> Items,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("scanned")]     int Scanned,    // tổng cơ hội mở quét được
    [property: JsonPropertyName("deepScored")]  int DeepScored  // số deal AI chấm sâu
);

/// Body POST /api/v1/deals/analyze.
public record DealAnalyzeRequest(
    [property: JsonPropertyName("assignee")] string? Assignee,
    [property: JsonPropertyName("source")]   string? Source,
    [property: JsonPropertyName("topN")]     int? TopN,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")]    string? Model,
    [property: JsonPropertyName("apiKey")]   string? ApiKey
);
