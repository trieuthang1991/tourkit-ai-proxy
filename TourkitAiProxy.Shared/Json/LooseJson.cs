using System.Text.Json;

namespace TourkitAiProxy.Shared.Json;

/// <summary>
/// Trích JSON object đầu tiên từ output AI (có thể bọc ```json fences, kèm prose, hoặc
/// "thinking" trước/sau). Tách từ logic trong ReviewService.ParseReviewJson để dùng chung.
///
/// <para><b>Không lấy dấu mở ĐẦU TIÊN, mà lấy khối ĐẦU TIÊN PARSE ĐƯỢC.</b> Model suy luận
/// (DeepSeek/Kimi…) hay chảy nội tâm ra trước câu trả lời, và đoạn nghĩ đó có thể chứa dấu
/// <c>{</c> hoặc <c>[</c>. Bản đầu cắt trúng đoạn nghĩ rồi trả về rác, để nơi gọi lãnh
/// <c>JsonException</c> — hỏng thật trên staging 25/08/2026 ở gợi ý trạng thái deal, với đúng
/// chuỗi <c>{'. Need decide each.</c>. Xem <c>LooseJsonTests</c>.</para>
/// </summary>
public static class LooseJson
{
    /// <summary>
    /// Gỡ fences + trim đến top-level object cân bằng `{...}` (string/escape aware).
    /// Trả về chuỗi JSON object, hoặc null nếu không tìm thấy.
    /// </summary>
    public static string? ExtractFirstObject(string raw) => TrichKhoi(raw, '{', '}');

    /// <summary>
    /// Parse object JSON đầu tiên thành JsonDocument (caller dispose). Throw nếu không parse được.
    /// </summary>
    public static JsonDocument ParseFirstObject(string raw)
    {
        var json = ExtractFirstObject(raw)
            ?? throw new InvalidOperationException("Output không chứa JSON object hợp lệ");
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Trích top-level array `[...]` HOẶC object `{...}` đầu tiên trong output AI.
    /// Dùng cho prompt trả list (vd extract danh sách NCC, danh sách KH…).
    /// </summary>
    public static string? ExtractFirstArrayOrObject(string raw)
    {
        var cleaned = GoRao(raw);
        if (cleaned is null) return null;

        // Thử CẢ HAI kiểu mở rồi chọn khối parse được nằm SỚM nhất trong văn bản. Không thể quyết
        // theo "dấu nào xuất hiện trước" như bản cũ: chính dấu xuất hiện trước mới hay là dấu nằm
        // trong đoạn AI tự nghĩ.
        var obj = TimKhoiParseDuoc(cleaned, '{', '}');
        var arr = TimKhoiParseDuoc(cleaned, '[', ']');
        if (obj is null) return arr?.Json ?? DuPhong(cleaned);
        if (arr is null) return obj.Value.Json;
        return arr.Value.ViTri < obj.Value.ViTri ? arr.Value.Json : obj.Value.Json;
    }

    // ── Bên trong ────────────────────────────────────────────────────────────

    private static string? GoRao(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Replace("```json", "").Replace("```", "").Trim();
    }

    private static string? TrichKhoi(string raw, char mo, char dong)
    {
        var cleaned = GoRao(raw);
        if (cleaned is null) return null;
        return TimKhoiParseDuoc(cleaned, mo, dong)?.Json ?? DuPhong(cleaned, mo, dong);
    }

    /// <summary>
    /// Duyệt MỌI dấu mở trong văn bản, cắt khối cân bằng từ đó, trả về khối đầu tiên
    /// <see cref="JsonDocument"/> nuốt được.
    /// </summary>
    private static (string Json, int ViTri)? TimKhoiParseDuoc(string s, char mo, char dong)
    {
        for (var i = s.IndexOf(mo); i >= 0; i = s.IndexOf(mo, i + 1))
        {
            var khoi = CatKhoiCanBang(s, i, mo, dong);
            if (khoi is null) continue;        // mở mà không đóng → thử dấu mở kế tiếp
            try
            {
                using var _ = JsonDocument.Parse(khoi);
                return (khoi, i);
            }
            catch (JsonException) { /* rác — thử dấu mở kế tiếp */ }
        }
        return null;
    }

    /// <summary>
    /// Cắt từ <paramref name="start"/> tới dấu đóng cân bằng. Có nhận biết chuỗi + escape, nên
    /// dấu ngoặc nằm trong giá trị chuỗi không làm lệch độ sâu. Null nếu không đóng lại được.
    /// </summary>
    private static string? CatKhoiCanBang(string s, int start, char mo, char dong)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch == mo) depth++;
            else if (ch == dong)
            {
                depth--;
                if (depth == 0) return s[start..(i + 1)];
            }
        }
        return null;
    }

    /// <summary>
    /// Không khối nào parse nổi thì trả về ứng viên đầu tiên như bản cũ vẫn làm — nơi gọi tự
    /// quyết. Đổi thành <c>null</c> ở đây là âm thầm đổi ý nghĩa của mọi chỗ đang dùng.
    /// </summary>
    private static string? DuPhong(string s, char mo = '{', char dong = '}')
    {
        var i = s.IndexOf(mo);
        if (i < 0) return null;
        return CatKhoiCanBang(s, i, mo, dong) ?? s[i..];
    }
}
