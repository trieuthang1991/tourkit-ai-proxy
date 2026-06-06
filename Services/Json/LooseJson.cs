using System.Text.Json;

namespace TourkitAiProxy.Services.Json;

/// <summary>
/// Trích JSON object đầu tiên từ output AI (có thể bọc ```json fences, kèm prose, hoặc
/// "thinking" trước/sau). Tách từ logic trong ReviewService.ParseReviewJson để dùng chung.
/// </summary>
public static class LooseJson
{
    /// <summary>
    /// Gỡ fences + trim đến top-level object cân bằng `{...}` (string/escape aware).
    /// Trả về chuỗi JSON object, hoặc null nếu không tìm thấy.
    /// </summary>
    public static string? ExtractFirstObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

        var start = cleaned.IndexOf('{');
        if (start < 0) return null;
        cleaned = cleaned.Substring(start);

        int depth = 0, end = -1; bool inStr = false, esc = false;
        for (int i = 0; i < cleaned.Length; i++)
        {
            var ch = cleaned[i];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end > 0) cleaned = cleaned.Substring(0, end + 1);
        return cleaned;
    }

    /// <summary>
    /// Parse object JSON đầu tiên thành JsonDocument (caller dispose). Throw nếu không parse được.
    /// </summary>
    public static JsonDocument ParseFirstObject(string raw)
    {
        var json = ExtractFirstObject(raw)
            ?? throw new InvalidOperationException("Output không chứa JSON object hợp lệ");
        return JsonDocument.Parse(json);
    }
}
