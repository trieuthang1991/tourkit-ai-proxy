using System.Text.Json;

namespace TourkitAiProxy.Services;

/// Parse response/SSE chunk từ OpenCode Go.
/// Hai shape: "anthropic" (messages endpoint) và "openai" (chat/completions).
public static class UpstreamParser
{
    /// TryGetProperty an toàn: chỉ true khi <paramref name="el"/> là Object VÀ có property.
    /// System.Text.Json.TryGetProperty trên non-Object ném InvalidOperationException
    /// (first-chance, ồn debugger) — wrapper này quan trọng khi handle nested fields
    /// có thể null (vd: "delta": null trong chunk cuối OpenAI).
    public static bool TryObj(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    /// <param name="TextFromReasoning">
    /// TRUE khi <c>content</c> rỗng và Text được lấy tạm từ <c>reasoning_content</c>/<c>reasoning</c>
    /// — tức đây là CHUỖI SUY NGHĨ của model, KHÔNG phải câu trả lời. Hữu ích để debug (thấy model
    /// tiêu token vào đâu), nhưng TUYỆT ĐỐI không được đem hiển thị cho người dùng: đã có lần lọt
    /// nguyên phần "Chúng ta cần trả lời câu hỏi…" vào thư bản tin gửi giám đốc. Bên nào sinh chữ
    /// cho người đọc thì phải coi cờ này NGANG VỚI text rỗng và dùng bản dự phòng.
    /// </param>
    public record ParsedResponse(string Text, int InputTokens, int OutputTokens, string FinishReason,
                                 bool TextFromReasoning = false);

    /// Parse buffered upstream response. `stop_reason: max_tokens` được normalize → "length"
    /// để cả 2 path surface OpenAI-style finishReason.
    public static ParsedResponse Parse(string raw, string fmt)
    {
        string text = "", finishReason = "";
        bool fromReasoning = false;
        int inTok = 0, outTok = 0;
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (fmt == "anthropic")
        {
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out var t)) text += t.GetString();
            if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
            {
                var v = sr.GetString() ?? "";
                finishReason = v == "max_tokens" ? "length" : v;
            }
            if (root.TryGetProperty("usage", out var usg))
            {
                if (usg.TryGetProperty("input_tokens",  out var i)) inTok  = i.GetInt32();
                if (usg.TryGetProperty("output_tokens", out var o)) outTok = o.GetInt32();
            }
        }
        else
        {
            if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in ch.EnumerateArray())
                {
                    if (c.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finishReason = fr.GetString() ?? "";

                    if (c.TryGetProperty("message", out var m))
                    {
                        if (m.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrEmpty(s)) text += s;
                        }
                        if (string.IsNullOrEmpty(text))
                        {
                            foreach (var name in new[] { "reasoning_content", "reasoning" })
                            {
                                if (m.TryGetProperty(name, out var rc) && rc.ValueKind == JsonValueKind.String)
                                {
                                    var s = rc.GetString();
                                    // Đánh dấu để bên gọi biết đây là phần NGHĨ, không phải câu trả lời.
                                    if (!string.IsNullOrEmpty(s)) { text += s; fromReasoning = true; break; }
                                }
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(text) && c.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var dc))
                        text += dc.GetString();
                    if (string.IsNullOrEmpty(text) && c.TryGetProperty("text", out var pt))
                        text += pt.GetString();
                }
            }
            if (root.TryGetProperty("usage", out var usg))
            {
                if (usg.TryGetProperty("prompt_tokens",     out var i)) inTok  = i.GetInt32();
                if (usg.TryGetProperty("completion_tokens", out var o)) outTok = o.GetInt32();
            }
        }
        return new ParsedResponse(text, inTok, outTok, finishReason, fromReasoning);
    }
}
