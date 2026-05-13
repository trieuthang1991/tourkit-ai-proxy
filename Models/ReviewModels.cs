using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// Kết quả review 1 KH từ AI. Lưu vào data/reviews.json keyed by CustomerId.
/// `DataFingerprint` = SHA256(customer data) — đổi tức là KH có data mới → review stale.
public record CustomerReview(
    [property: JsonPropertyName("id")]                 string Id,
    [property: JsonPropertyName("customerId")]         string CustomerId,
    [property: JsonPropertyName("rank")]               string Rank,                  // A/B/C/D
    [property: JsonPropertyName("rankReason")]         string RankReason,
    [property: JsonPropertyName("alert")]              ReviewAlert Alert,
    [property: JsonPropertyName("portrait")]           string Portrait,
    [property: JsonPropertyName("strengths")]          List<string> Strengths,
    [property: JsonPropertyName("concerns")]           List<string> Concerns,
    [property: JsonPropertyName("preferences")]        string Preferences,
    [property: JsonPropertyName("actionNow")]          ReviewAction ActionNow,
    [property: JsonPropertyName("action30Days")]       List<string> Action30Days,
    [property: JsonPropertyName("productSuggestions")] List<string> ProductSuggestions,
    [property: JsonPropertyName("summaryLine")]        string SummaryLine,
    [property: JsonPropertyName("dataFingerprint")]    string DataFingerprint,
    [property: JsonPropertyName("aiModel")]            string AiModel,
    [property: JsonPropertyName("aiProvider")]         string AiProvider,
    [property: JsonPropertyName("tokensIn")]           int TokensIn,
    [property: JsonPropertyName("tokensOut")]          int TokensOut,
    [property: JsonPropertyName("generatedAt")]        string GeneratedAt,
    [property: JsonPropertyName("feedback")]           ReviewFeedback? Feedback
);

public record ReviewAlert(
    [property: JsonPropertyName("level")]   string Level,                            // high / medium / none
    [property: JsonPropertyName("message")] string? Message
);

public record ReviewAction(
    [property: JsonPropertyName("task")]   string Task,
    [property: JsonPropertyName("reason")] string Reason
);

public record ReviewFeedback(
    [property: JsonPropertyName("rating")]      string Rating,                       // helpful / not_helpful
    [property: JsonPropertyName("note")]        string? Note,
    [property: JsonPropertyName("submittedAt")] string SubmittedAt
);

/// DTOs cho request endpoints.
public record BatchReviewRequest(
    [property: JsonPropertyName("customerIds")] List<string> CustomerIds,
    [property: JsonPropertyName("forceFresh")]  bool ForceFresh = false
);

public record FeedbackRequest(
    [property: JsonPropertyName("rating")] string Rating,
    [property: JsonPropertyName("note")]   string? Note
);

/// Batch job state (in-memory, không persist qua restart).
public class BatchJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public List<string> CustomerIds { get; init; } = new();
    public int Total => CustomerIds.Count;
    public int Done   { get; set; }
    public int Errors { get; set; }
    public int Cached { get; set; }
    public string Status { get; set; } = "queued";                                   // queued/processing/done/cancelled
    public DateTime StartedAt  { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public CancellationTokenSource Cts { get; } = new();

    /// Channel để stream event ra cho SSE handler. Mỗi event 1 message.
    public System.Threading.Channels.Channel<BatchEvent> Events { get; }
        = System.Threading.Channels.Channel.CreateUnbounded<BatchEvent>();
}

public record BatchEvent(
    string Type,                                                                     // start / progress / done / error / cached / cancelled
    string? CustomerId = null,
    object? Payload    = null,
    string? Error      = null
);
