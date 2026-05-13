using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// Flat shape (KHÔNG phải OpenAI messages[]) — đây là contract với frontend.
/// `provider` chọn upstream (opencode-go | nine-routes | …); nếu trống → default
/// theo `Providers:Default` ở appsettings, hoặc "opencode-go" hardcoded.
public record CompleteRequest(
    [property: JsonPropertyName("prompt")]      string Prompt,
    [property: JsonPropertyName("provider")]    string? Provider,
    [property: JsonPropertyName("model")]       string? Model,
    [property: JsonPropertyName("maxTokens")]   int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("system")]      string? System
);
