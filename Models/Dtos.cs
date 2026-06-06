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
    [property: JsonPropertyName("system")]      string? System,
    // API key client gửi kèm (OpenAI/Anthropic) — dùng per-request, KHÔNG lưu server.
    [property: JsonPropertyName("apiKey")]       string? ApiKey = null,
    // Ảnh đính kèm (data-URL base64, vd "data:image/jpeg;base64,...") cho yêu cầu multimodal/vision.
    // CHỈ OpenAIProvider/AnthropicProvider xử lý; provider khác bỏ qua. Mặc định null → text-only như cũ.
    [property: JsonPropertyName("images")]        IReadOnlyList<string>? Images = null
);

/// 1 ảnh đã tách khỏi data-URL: media type + base64 thuần (dùng build body multimodal cho provider).
public record ImagePart(string MediaType, string Base64)
{
    /// Tách "data:image/png;base64,XXXX" → ImagePart("image/png","XXXX"). Trả null nếu không phải data-URL hợp lệ.
    public static ImagePart? FromDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var s = dataUrl.Trim();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = s.IndexOf(',');
        if (comma < 0) return null;
        var meta = s.Substring(5, comma - 5);            // "image/png;base64"
        var data = s.Substring(comma + 1);
        var semi = meta.IndexOf(';');
        var media = semi >= 0 ? meta.Substring(0, semi) : meta;
        if (string.IsNullOrWhiteSpace(media)) media = "image/jpeg";
        if (string.IsNullOrWhiteSpace(data)) return null;
        return new ImagePart(media, data);
    }
}
